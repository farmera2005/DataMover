using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Security;

/// <summary>
/// The first-run window: setup (creating the first administrator) is accepted only within an hour of the control API
/// starting, so nobody else on the network can claim a fresh install later. Restarting the api container reopens it
/// while no user exists.
/// </summary>
public sealed class SetupWindow(TimeProvider time)
{
    public static readonly TimeSpan Length = TimeSpan.FromMinutes(60);

    public DateTimeOffset ClosesUtc { get; } = time.GetUtcNow() + Length;

    public bool IsOpen => time.GetUtcNow() < ClosesUtc;
}

/// <summary>Local accounts (PBKDF2 password hashes) and directory sign-in, local first so the break-glass admin always works.</summary>
public sealed class Accounts(LakeflowDb db, SettingsStore settings, LdapDirectory directory, TimeProvider time, ILogger<Accounts> log)
{
    public const int MinPasswordLength = 12;
    private static readonly PasswordHasher<User> Hasher = new();

    public static string Normalize(string userName) => userName.Trim().ToLowerInvariant();

    public static string? CheckPassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength ? $"Passwords need at least {MinPasswordLength} characters." : null;

    public static void SetPassword(User user, string password)
    {
        user.PasswordHash = Hasher.HashPassword(user, password);
        user.SecurityStamp = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// A local account with that name decides on its own (it is never taken over by a directory user); otherwise, with
    /// directory sign-in on, the directory decides and the user's row is created or refreshed (role from groups).
    /// </summary>
    public async Task<(User? User, string? Error)> SignInAsync(string userName, string password, CancellationToken ct)
    {
        const string wrong = "User name or password is not correct.";
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password)) return (null, wrong);
        var name = Normalize(userName);
        var local = await db.Users.FirstOrDefaultAsync(u => u.UserName == name && u.AuthSource == User.Local, ct);
        if (local is not null)
        {
            if (local.PasswordHash is null || Hasher.VerifyHashedPassword(local, local.PasswordHash, password) == PasswordVerificationResult.Failed)
                return (null, wrong);
            if (local.Disabled) return (null, "This account is disabled.");
            local.LastSignInUtc = time.GetUtcNow();
            await db.SaveChangesAsync(ct);
            return (local, null);
        }

        var (ldap, secret) = await settings.GetWithSecretAsync<LdapSettingsDto>(SettingsStore.Ldap, ct);
        if (ldap is not { Enabled: true } || secret is null) return (null, wrong);
        var account = Normalize(LdapDirectory.AccountName(userName));
        var result = await directory.AuthenticateAsync(ldap, System.Text.Encoding.UTF8.GetString(secret), userName, password, ct);
        if (!result.Succeeded || result.Role is not { } role)
        {
            log.LogInformation("Directory sign-in refused for {User}: {Reason}", account, result.Message);
            return (null, result.Message.StartsWith("You are not", StringComparison.Ordinal) ? result.Message : wrong);
        }
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == account, ct);
        if (user is { AuthSource: User.Local }) return (null, wrong); // a local account of that name exists
        if (user is null)
            db.Users.Add(user = new User { Id = Guid.NewGuid(), UserName = account, AuthSource = User.Ldap, Role = role, CreatedUtc = time.GetUtcNow() });
        if (user.Disabled) return (null, "This account is disabled.");
        if (user.Role != role) user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.Role = role;
        user.DisplayName = result.DisplayName;
        user.LastSignInUtc = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return (user, null);
    }
}
