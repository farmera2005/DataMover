using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Lakeflow.Console.Core.ViewModels;

/// <summary>Sign-in state for the whole window.</summary>
public sealed partial class ShellViewModel(IAccessTokenProvider tokens) : ObservableObject
{
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsSignedIn))] private SignedInUser? _user;
    [ObservableProperty] private string? _error;

    public bool IsSignedIn => User is not null;

    [RelayCommand]
    public async Task SignInAsync(CancellationToken ct)
    {
        Error = null;
        try
        {
            var user = SignedInUser.FromToken(await tokens.GetTokenAsync(ct));
            if (user.Role == ConsoleRole.None)
            {
                Error = $"{user.Name} has no Lakeflow role. Ask an Admin to add you to a Lakeflow group.";
                return;
            }
            User = user;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = $"Sign-in failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task SignOutAsync()
    {
        await tokens.SignOutAsync();
        User = null;
    }
}
