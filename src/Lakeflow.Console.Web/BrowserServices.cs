using Lakeflow.Console.Core;
using Microsoft.JSInterop;

namespace Lakeflow.Console.Web;

/// <summary>
/// Seals passwords with the browser's WebCrypto (wwwroot/js/seal.js); .NET's RSA does not run in browsers. On a
/// plain-HTTP page there is no WebCrypto: it returns null and the API seals the password on arrival.
/// </summary>
public sealed class BrowserSecretSealer(IJSRuntime js) : ISecretSealer, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<string?> SealAsync(string secret, string publicKeySpki, string thumbprint)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/seal.js");
        return await _module.InvokeAsync<string?>("sealSecret", secret, publicKeySpki, thumbprint);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}

/// <summary>Hands the user a file (a log bundle, an install file).</summary>
public sealed class BrowserFiles(IJSRuntime js)
{
    public async Task SaveAsync(string name, byte[] content, string type)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/seal.js");
        await module.InvokeVoidAsync("saveFile", name, Convert.ToBase64String(content), type);
    }

    public async Task<bool> IsSecureAsync()
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/seal.js");
        return await module.InvokeAsync<bool>("isSecure");
    }
}
