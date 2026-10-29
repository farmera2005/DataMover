using Lakeflow.Console.Core;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.JSInterop;

namespace Lakeflow.Console.Web;

/// <summary>Seals passwords with the browser's WebCrypto (wwwroot/js/seal.js); .NET's RSA does not run in browsers.</summary>
public sealed class BrowserSecretSealer(IJSRuntime js) : ISecretSealer, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async Task<string> SealAsync(string secret, string publicKeySpki, string thumbprint)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/seal.js");
        return await _module.InvokeAsync<string>("sealSecret", secret, publicKeySpki, thumbprint);
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }
}

/// <summary>Hands the user a file (e.g. a log bundle).</summary>
public sealed class BrowserFiles(IJSRuntime js)
{
    public async Task SaveAsync(string name, byte[] content, string type)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./js/seal.js");
        await module.InvokeVoidAsync("saveFile", name, Convert.ToBase64String(content), type);
    }
}

/// <summary>
/// Who is signed in and with which Lakeflow role. Roles are read from the API access token (where Entra puts the
/// API's app roles); the API enforces them, this only shapes the pages.
/// </summary>
public sealed class UserSession(Microsoft.AspNetCore.Components.WebAssembly.Authentication.IAccessTokenProvider tokens)
{
    private SignedInUser? _user;

    public async Task<SignedInUser?> GetAsync()
    {
        if (_user is not null) return _user;
        var result = await tokens.RequestAccessToken();
        return result.TryGetToken(out var token) ? _user = SignedInUser.FromToken(token.Value) : null;
    }
}
