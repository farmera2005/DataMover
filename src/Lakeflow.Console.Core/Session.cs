using Lakeflow.Contracts;

namespace Lakeflow.Console.Core;

/// <summary>Who is signed in (from <c>/auth/me</c>; the API enforces roles, this only shapes the pages).</summary>
public sealed record SignedInUser(string UserName, string? DisplayName, ConsoleRole Role, string AuthSource)
{
    public string Name => DisplayName ?? UserName;
    public bool CanOperate => Role >= ConsoleRole.Operator;
    public bool CanAdminister => Role == ConsoleRole.Admin;
    public bool IsLocal => AuthSource == "local";

    public static SignedInUser From(Me me) => new(me.UserName, me.DisplayName, me.Role, me.AuthSource);
}
