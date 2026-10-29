using Lakeflow.Console.Core;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Lakeflow.Console.Auth;

/// <summary>
/// Entra ID sign-in through the Windows account broker (WAM): the user's Windows work account signs in silently where
/// possible, and Conditional Access applies MFA. Tokens stay in the broker; nothing is written by the app.
/// </summary>
public sealed class MsalTokenProvider : IAccessTokenProvider
{
    private readonly IPublicClientApplication _app;
    private readonly string[] _scopes;
    private readonly Func<IntPtr> _window;

    public MsalTokenProvider(string tenantId, string clientId, string scope, Func<IntPtr> window)
    {
        _scopes = [scope];
        _window = window;
        _app = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithDefaultRedirectUri()
            .WithParentActivityOrWindow(window)
            .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows) { Title = "Lakeflow Console" })
            .Build();
    }

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        var account = (await _app.GetAccountsAsync()).FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
        try
        {
            return (await _app.AcquireTokenSilent(_scopes, account).ExecuteAsync(ct)).AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            return (await _app.AcquireTokenInteractive(_scopes).WithParentActivityOrWindow(_window()).ExecuteAsync(ct)).AccessToken;
        }
    }

    public async Task SignOutAsync()
    {
        foreach (var account in await _app.GetAccountsAsync()) await _app.RemoveAsync(account);
    }
}
