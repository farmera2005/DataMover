using System.Security.Cryptography;

namespace Lakeflow.Contracts;

/// <summary>Encrypts small secrets at rest at rest (collector: buffer key, source passwords, SAS token; control API: settings secrets, CA key).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedBytes);
}

/// <summary>
/// For Linux (the collector and the control API in Docker; no DPAPI): AES-256-GCM with a master key kept in its own file, created on first start with
/// owner-only permissions. Keep that file on a separate volume from the data,
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
