using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace Lakeflow.Agent.Update;

public enum CollectorPlatform { WindowsService, Docker, Other }

/// <summary>What self-update needs from the machine; faked in tests.</summary>
public interface IUpdatePlatform
{
    CollectorPlatform Kind { get; }
    /// <summary>The Authenticode publisher (certificate subject) a file is signed by; null if unsigned or not checkable here.</summary>
    string? Publisher(string path);
    /// <summary>The publisher of the running agent; null if it is unsigned.</summary>
    string? OwnPublisher { get; }
    /// <summary>The updater service's folder (SYSTEM writes, the agent reads): its result and install logs.</summary>
    string UpdaterFolder { get; }
}

public sealed class UpdatePlatform : IUpdatePlatform
{
    public static string DefaultUpdaterFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lakeflow", "Updater");

    public CollectorPlatform Kind { get; } =
        OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService() ? CollectorPlatform.WindowsService
        : File.Exists("/.dockerenv") || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? CollectorPlatform.Docker
        : CollectorPlatform.Other;

    public string? Publisher(string path) => OperatingSystem.IsWindows() ? Authenticode.Publisher(path) : null;

    public string? OwnPublisher => Environment.ProcessPath is { } exe ? Publisher(exe) : null;

    public string UpdaterFolder => DefaultUpdaterFolder;
}

/// <summary>Authenticode checks through Windows itself (WinVerifyTrust): a valid signature, chaining to a trusted root.</summary>
[SupportedOSPlatform("windows")]
public static class Authenticode
{
    /// <summary>The signer's subject, if the file carries an Authenticode signature (not yet checked for trust).</summary>
    public static string? Publisher(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the documented way to read an Authenticode signer.
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return certificate.Subject;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>0 when Windows trusts the file's signature; otherwise the WinVerifyTrust result (e.g. 0x800B0100: not signed).</summary>
    public static int Verify(string path)
    {
        var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path };
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0 /* WTD_REVOKE_NONE */,
                dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = filePointer, dwStateAction = 1 /* WTD_STATEACTION_VERIFY */,
            };
            var result = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            data.dwStateAction = 2; // WTD_STATEACTION_CLOSE
            WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(filePointer);
            Marshal.FreeHGlobal(filePointer);
        }
    }

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WintrustData pWVTData);
}
