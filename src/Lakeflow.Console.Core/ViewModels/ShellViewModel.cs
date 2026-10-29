using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lakeflow.Contracts;

namespace Lakeflow.Console.Core.ViewModels;

public enum ShellState { Loading, NeedsSetup, SetupClosed, SignedOut, SignedIn, Unreachable }

/// <summary>Sign-in state for the whole console: first-run setup, the sign-in form, or the signed-in user.</summary>
public sealed partial class ShellViewModel(ConsoleApi api) : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSignedIn))] private SignedInUser? _user;
    [ObservableProperty] private ShellState _state = ShellState.Loading;
    [ObservableProperty] private string _userName = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    public bool IsSignedIn => User is not null;

    /// <summary>Works out where to start: setup (no user yet), sign-in, or straight in with an existing session.</summary>
    [RelayCommand]
    public async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var setup = await api.GetSetupStatusAsync(ct);
            if (setup.NeedsSetup)
            {
                State = setup.WindowOpen ? ShellState.NeedsSetup : ShellState.SetupClosed;
                return;
            }
            var me = await api.GetMeAsync(ct);
            User = me is null ? null : SignedInUser.From(me);
            State = User is null ? ShellState.SignedOut : ShellState.SignedIn;
        }
        catch (Exception ex) when (ex is ConsoleApiException or HttpRequestException)
        {
            Error = ex.Message;
            State = ShellState.Unreachable;
        }
    }

    [RelayCommand]
    public async Task SignInAsync(CancellationToken ct)
    {
        Error = null;
        IsBusy = true;
        try
        {
            User = SignedInUser.From(await api.SignInAsync(new SignInRequest(UserName.Trim(), Password), ct));
            State = ShellState.SignedIn;
        }
        catch (ConsoleApiException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Password = "";
            IsBusy = false;
        }
    }

    /// <summary>Called by the setup wizard once it has created (and signed in) the first administrator.</summary>
    public void SignedIn(Me me)
    {
        User = SignedInUser.From(me);
        State = ShellState.SignedIn;
    }

    [RelayCommand]
    public async Task SignOutAsync()
    {
        try
        {
            await api.SignOutAsync();
        }
        catch (Exception ex) when (ex is ConsoleApiException or HttpRequestException)
        {
        }
        User = null;
        State = ShellState.SignedOut;
    }
}
