using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;

namespace Lakeflow.ControlApi.Services;

/// <summary>
/// Agent MSIs uploaded in the console, kept as <c>packages/&lt;sha256&gt;.msi</c> in the server's data folder. The server
/// checks only that a file is a signed MSI; the agent checks the signature itself (same publisher as the running agent)
/// before it installs anything.
/// </summary>
public sealed partial class AgentPackages(ServerOptions options)
{
    public const long MaxBytes = 300L * 1024 * 1024;

    private static readonly byte[] CompoundFileMagic = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    // An Authenticode-signed MSI carries its signature in a stream named "\u0005DigitalSignature" (names are UTF-16).
    private static readonly byte[] SignatureStream = Encoding.Unicode.GetBytes("\u0005DigitalSignature");

    private string Folder => Path.Combine(options.DataDirectory, "packages");

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,5}$")]
    private static partial Regex VersionPattern();

    /// <summary>MSI product versions: major.minor.build, at most 255.255.65535.</summary>
    public static bool IsVersion(string? version) => version is not null && VersionPattern().IsMatch(version)
        && version.Split('.').Select(int.Parse).Zip([255, 255, 65535]).All(p => p.First <= p.Second);

    public sealed record Stored(string Sha256, long SizeBytes);

    /// <summary>Saves the upload if it is a signed MSI; otherwise says what is wrong and keeps nothing.</summary>
    public async Task<(Stored? Package, string? Error)> SaveAsync(Stream body, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var temp = Path.Combine(Folder, $"upload-{Guid.NewGuid():N}.tmp");
        try
        {
            long size = 0;
            var signed = false;
            var head = new byte[8];
            var carry = Array.Empty<byte>();
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    if (size < 8) Array.Copy(buffer, 0, head, (int)size, (int)Math.Min(8 - size, read));
                    size += read;
                    if (size > MaxBytes) return (null, $"The file is larger than {MaxBytes / 1024 / 1024} MB.");
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (!signed)
                    {
                        // Search across chunk boundaries too.
                        var window = carry.Concat(buffer.Take(read)).ToArray();
                        signed = window.AsSpan().IndexOf(SignatureStream) >= 0;
                        carry = window[Math.Max(0, window.Length - SignatureStream.Length)..];
                    }
                }
            }
            if (size < 8 || !head.AsSpan().SequenceEqual(CompoundFileMagic)) return (null, "That is not an MSI file.");
            if (!signed) return (null, "The MSI is not signed. Agents install only packages signed by the same publisher as themselves; sign it first (README, \"Agent updates\").");
            var hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            File.Move(temp, PathFor(hash), overwrite: true);
            return (new Stored(hash, size), null);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Stream? Open(AgentPackage package) => File.Exists(PathFor(package.Sha256))
        ? new FileStream(PathFor(package.Sha256), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
        : null;

    public void Delete(AgentPackage package)
    {
        if (File.Exists(PathFor(package.Sha256))) File.Delete(PathFor(package.Sha256));
    }

    private string PathFor(string sha256) => Path.Combine(Folder, $"{sha256}.msi");
}
