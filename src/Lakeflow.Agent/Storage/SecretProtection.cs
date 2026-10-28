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
