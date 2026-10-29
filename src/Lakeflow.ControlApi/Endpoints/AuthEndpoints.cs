using Lakeflow.ControlApi.Data;
using Lakeflow.ControlApi.Security;
using Lakeflow.ControlApi.Services;
using Lakeflow.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Lakeflow.ControlApi.Endpoints;

/// <summary>
/// First-run setup, sign-in and user accounts. Setup creates the first administrator (a local account that always
/// signs in, whatever happens to the directory) and is only accepted while no user exists, within an hour of start.
/// </summary>
public static class AuthEndpoints
{
    private static readonly SemaphoreSlim SetupGate = new(1, 1);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/setup", async (LakeflowDb db, SetupWindow window, CancellationToken ct) =>
            new SetupStatus(!await db.Users.AnyAsync(ct), window.IsOpen, window.ClosesUtc)).AllowAnonymous();
        app.MapPost("/auth/setup", SetupAsync).AllowAnonymous().RequireRateLimiting("sign-in");
        app.MapPost("/auth/sign-in", SignInAsync).AllowAnonymous().RequireRateLimiting("sign-in");
        app.MapPost("/auth/sign-out", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        }).AllowAnonymous();
        app.MapGet("/auth/me", async Task<Results<Ok<Me>, UnauthorizedHttpResult>> (HttpContext http, LakeflowDb db, CancellationToken ct) =>
            await CurrentAsync(http, db, ct) is { } user ? TypedResults.Ok(ToMe(user)) : TypedResults.Unauthorized())
            .RequireAuthorization(Policies.Viewer);
        app.MapPost("/auth/password", ChangePasswordAsync).RequireAuthorization(Policies.Viewer).RequireRateLimiting("sign-in");

        var admin = app.MapGroup("/users").RequireAuthorization(Policies.Admin);
        admin.MapGet("", async (LakeflowDb db, CancellationToken ct) =>
            (await db.Users.OrderBy(u => u.UserName).ToListAsync(ct)).Select(ToDto));
        admin.MapPost("", CreateUserAsync);
        admin.MapPut("/{id:guid}", UpdateUserAsync);
        admin.MapDelete("/{id:guid}", DeleteUserAsync);
    }

    private static async Task<Results<Ok<Me>, ValidationProblem, ProblemHttpResult>> SetupAsync(
        SetupRequest request, HttpContext http, LakeflowDb db, SettingsStore settings, SetupWindow window, ServerCertificates certificates,
        TimeProvider time, IActor actor, CancellationToken ct)
    {
        await SetupGate.WaitAsync(ct);
        try
        {
            if (await db.Users.AnyAsync(ct)) return TypedResults.Problem("Setup is already done; sign in instead.", statusCode: 409);
            if (!window.IsOpen)
                return TypedResults.Problem("Setup must be finished within an hour of the control API starting. Restart the api container and try again.",
                    statusCode: 403);
            var errors = new List<string>();
            var name = Accounts.Normalize(request.UserName ?? "");
            if (name.Length is 0 or > 256 || name.Any(char.IsWhiteSpace)) errors.Add("Choose a user name without spaces.");
            if (Accounts.CheckPassword(request.Password) is { } weak) errors.Add(weak);
            if (errors.Count > 0) return Invalid(errors);

            ((HttpActor)actor).ActAs($"setup ({name})");
            var user = new User
            {
                Id = Guid.NewGuid(), UserName = name, DisplayName = Clean(request.DisplayName), Role = ConsoleRole.Admin,
                CreatedUtc = time.GetUtcNow(), LastSignInUtc = time.GetUtcNow(),
            };
            Accounts.SetPassword(user, request.Password);
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            // The name the browser used is how people reach this server; certificates and install files use it.
            var host = http.Request.Host.Host;
            if (await settings.GetAsync<ServerSetting>(SettingsStore.Server, ct) is null && !string.IsNullOrEmpty(host))
            {
                await settings.SetAsync(SettingsStore.Server, new ServerSetting(host), null, ct);
                certificates.Load(host, null);
            }
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.Principal(user));
            return TypedResults.Ok(ToMe(user));
        }
        finally
        {
            SetupGate.Release();
        }
    }

    private static async Task<Results<Ok<Me>, ProblemHttpResult>> SignInAsync(SignInRequest request, HttpContext http, Accounts accounts,
        IActor actor, CancellationToken ct)
    {
        ((HttpActor)actor).ActAs($"sign-in ({Accounts.Normalize(request.UserName ?? "")})");
        var (user, error) = await accounts.SignInAsync(request.UserName ?? "", request.Password ?? "", ct);
        if (user is null) return TypedResults.Problem(error, statusCode: 401);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.Principal(user));
        return TypedResults.Ok(ToMe(user));
    }

    public sealed record PasswordChange(string CurrentPassword, string NewPassword);

    private static async Task<Results<NoContent, ValidationProblem>> ChangePasswordAsync(PasswordChange request, HttpContext http,
        LakeflowDb db, Accounts accounts, CancellationToken ct)
    {
        var user = await CurrentAsync(http, db, ct);
        if (user is not { AuthSource: User.Local }) return Invalid(["Directory accounts change their password in the directory."]);
        var (check, _) = await accounts.SignInAsync(user.UserName, request.CurrentPassword ?? "", ct);
        if (check is null) return Invalid(["The current password is not correct."]);
        if (Accounts.CheckPassword(request.NewPassword) is { } weak) return Invalid([weak]);
        Accounts.SetPassword(check, request.NewPassword);
        await db.SaveChangesAsync(ct);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthSetup.Principal(check));
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<UserDto>, ValidationProblem>> CreateUserAsync(UserRequest request, LakeflowDb db, TimeProvider time, CancellationToken ct)
    {
        var name = Accounts.Normalize(request.UserName ?? "");
        var errors = new List<string>();
        if (name.Length is 0 or > 256 || name.Any(char.IsWhiteSpace)) errors.Add("Choose a user name without spaces.");
        else if (await db.Users.AnyAsync(u => u.UserName == name, ct)) errors.Add($"A user named {name} already exists.");
        if (Accounts.CheckPassword(request.Password) is { } weak) errors.Add(weak);
        if (errors.Count > 0) return Invalid(errors);
        var user = new User
        {
            Id = Guid.NewGuid(), UserName = name, DisplayName = Clean(request.DisplayName), Role = request.Role, Disabled = request.Disabled,
            CreatedUtc = time.GetUtcNow(),
        };
        Accounts.SetPassword(user, request.Password!);
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(user));
    }

    /// <summary>Role and status for any user; name and password for local accounts. The last active local administrator stays one.</summary>
    private static async Task<Results<Ok<UserDto>, NotFound, ValidationProblem>> UpdateUserAsync(Guid id, UserRequest request, HttpContext http,
        LakeflowDb db, CancellationToken ct)
    {
        if (await db.Users.FindAsync([id], ct) is not { } user) return TypedResults.NotFound();
        var errors = new List<string>();
        var demoted = user.Role == ConsoleRole.Admin && (request.Role != ConsoleRole.Admin || request.Disabled);
        if (demoted && user.AuthSource == User.Local && await IsLastLocalAdminAsync(db, user, ct))
            errors.Add("This is the last active local administrator; keep it so someone can always sign in.");
        if (user.AuthSource == User.Ldap && request.Role != user.Role)
            errors.Add("A directory user's role comes from their groups (Sign-in & LDAP settings).");
        if (!string.IsNullOrEmpty(request.Password))
        {
            if (user.AuthSource != User.Local) errors.Add("Directory users have no password here.");
            else if (Accounts.CheckPassword(request.Password) is { } weak) errors.Add(weak);
        }
        if (errors.Count > 0) return Invalid(errors);

        var changed = user.Role != request.Role || user.Disabled != request.Disabled;
        user.Role = request.Role;
        user.Disabled = request.Disabled;
        if (user.AuthSource == User.Local) user.DisplayName = Clean(request.DisplayName);
        if (!string.IsNullOrEmpty(request.Password)) Accounts.SetPassword(user, request.Password);
        else if (changed) user.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDto(user));
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> DeleteUserAsync(Guid id, HttpContext http, LakeflowDb db, CancellationToken ct)
    {
        if (await db.Users.FindAsync([id], ct) is not { } user) return TypedResults.NotFound();
        if (user.Id.ToString() == http.User.FindFirst(UserClaims.UserId)?.Value) return Invalid(["You cannot delete your own account."]);
        if (user is { Role: ConsoleRole.Admin, AuthSource: User.Local } && await IsLastLocalAdminAsync(db, user, ct))
            return Invalid(["This is the last active local administrator; keep it so someone can always sign in."]);
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<bool> IsLastLocalAdminAsync(LakeflowDb db, User user, CancellationToken ct) =>
        !await db.Users.AnyAsync(u => u.Id != user.Id && u.AuthSource == User.Local && u.Role == ConsoleRole.Admin && !u.Disabled, ct);

    private static async Task<User?> CurrentAsync(HttpContext http, LakeflowDb db, CancellationToken ct) =>
        Guid.TryParse(http.User.FindFirst(UserClaims.UserId)?.Value, out var id) ? await db.Users.FindAsync([id], ct) : null;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 256)];

    private static Me ToMe(User u) => new(u.UserName, u.DisplayName, u.Role, u.AuthSource);

    private static UserDto ToDto(User u) => new(u.Id, u.UserName, u.DisplayName, u.AuthSource, u.Role, u.Disabled, u.CreatedUtc, u.LastSignInUtc);

    internal static ValidationProblem Invalid(IEnumerable<string> errors) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["config"] = errors.ToArray() });
}
