using System.Diagnostics;
using System.Security.Cryptography;
using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Console.Core.Tests;

/// <summary>
/// The web console seals passwords with WebCrypto (src/Lakeflow.Console.Web/wwwroot/js/seal.js). Node runs that same
/// file with its WebCrypto here, and the collector's C# code must open the result.
/// </summary>
public class BrowserSealingTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lakeflow.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    /// <param name="insecure">As on a plain-HTTP page: no WebCrypto.</param>
    private static async Task<string?> SealWithNodeAsync(string secret, string spki, string thumbprint, bool insecure = false)
    {
        var seal = new Uri(Path.Combine(Repo(), "src", "Lakeflow.Console.Web", "wwwroot", "js", "seal.js")).AbsoluteUri;
        var prelude = insecure ? "Object.defineProperty(globalThis, 'crypto', { value: undefined });" : "";
        var script = $"{prelude} const {{ sealSecret }} = await import({Js(seal)}); process.stdout.write(String(await sealSecret({Js(secret)}, {Js(spki)}, {Js(thumbprint)})));";
        var start = new ProcessStartInfo("node", ["--input-type=module", "-e", script]) { RedirectStandardOutput = true, RedirectStandardError = true };
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception) { return null; } // node not installed
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private static string Js(string value) => System.Text.Json.JsonSerializer.Serialize(value);

    [SkippableFact]
    public async Task Passwords_sealed_in_the_browser_open_only_on_the_collector()
    {
        using var collector = RSA.Create(3072);
        var spki = Convert.ToBase64String(collector.ExportSubjectPublicKeyInfo());
        const string thumbprint = "4F2A9C0B7D1E";
        const string password = "Gr@in-2026 é ü \"quoted\"";

        var sealedValue = await SealWithNodeAsync(password, spki, thumbprint);
        Skip.If(sealedValue is null, "node is not installed");

        Assert.True(SealedSecret.IsWellFormed(sealedValue!));
        Assert.Equal(thumbprint, SealedSecret.KeyId(sealedValue!));
        Assert.Equal(password, SealedSecret.Open(sealedValue!, collector, thumbprint));
        Assert.DoesNotContain("Gr@in", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sealedValue!)));
        Assert.ThrowsAny<CryptographicException>(() => SealedSecret.Open(sealedValue!, collector, "OTHER-CERT"));
        using var other = RSA.Create(3072);
        Assert.ThrowsAny<CryptographicException>(() => SealedSecret.Open(sealedValue!, other, thumbprint));
    }

    [SkippableFact]
    public async Task Without_WebCrypto_the_browser_seals_nothing_and_leaves_it_to_the_API()
    {
        using var collector = RSA.Create(2048);
        var result = await SealWithNodeAsync("pw", Convert.ToBase64String(collector.ExportSubjectPublicKeyInfo()), "TP", insecure: true);
        Skip.If(result is null, "node is not installed");
        Assert.Equal("null", result);
    }

    [Fact]
    public async Task DotNet_sealer_matches_the_contract()
    {
        using var collector = RSA.Create(2048);
        var sealedValue = await new DotNetSecretSealer().SealAsync("pw", Convert.ToBase64String(collector.ExportSubjectPublicKeyInfo()), "TP");
        Assert.Equal("pw", SealedSecret.Open(sealedValue!, collector, "TP"));
    }
}
