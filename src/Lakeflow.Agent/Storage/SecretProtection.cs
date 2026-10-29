using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Lakeflow.Agent.Storage;

/// <summary>Encrypts small secrets at rest on the agent machine (buffer key, source passwords, cached SAS token).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>Windows DPAPI, machine scope: readable by services on this machine only, not if the files are copied elsewhere.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Lakeflow.Agent.v1"u8.ToArray();

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);

    public byte[] Unprotect(byte[] protectedBytes) => ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
}

/// <summary>
/// For the Linux collector (no DPAPI): AES-256-GCM with a master key kept in its own file, created on first start with
/// owner-only permissions. Keep that file on a separate volume from the data (as GrainTime keeps its encryption key),
/// so a copy of the data volume alone reveals nothing.
/// </summary>
public sealed class FileKeyProtector : ISecretProtector
{
    private readonly byte[] _key;

    public FileKeyProtector(string keyFile)
    {
        if (File.Exists(keyFile))
        {
            _key = Convert.FromBase64String(File.ReadAllText(keyFile).Trim());
            if (_key.Length != 32) throw new InvalidOperationException($"{keyFile} is not a Lakeflow master key.");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(keyFile))!);
        _key = RandomNumberGenerator.GetBytes(32);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var writer = new StreamWriter(keyFile, System.Text.Encoding.ASCII, options);
        writer.Write(Convert.ToBase64String(_key));
    }

    public byte[] Protect(byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public byte[] Unprotect(byte[] data)
    {
        if (data.Length < 28) throw new CryptographicException("Protected value is too short.");
        var plain = new byte[data.Length - 28];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
        return plain;
    }
}
