using System.Text;
using Microsoft.Data.SqlClient;

namespace Lakeflow.Connectors.SqlServer;

/// <summary>Builds the connector's SQL. Every statement goes through <see cref="ReadOnly"/> before it runs.</summary>
internal static class SqlText
{
    /// <summary>Applied once per connection: never wait long on locks, always lose a deadlock.</summary>
    public const string SessionSettings =
        "SET LOCK_TIMEOUT 5000; SET DEADLOCK_PRIORITY LOW; SET TRANSACTION ISOLATION LEVEL READ COMMITTED; SET NOCOUNT ON;";

    /// <summary>Same as T-SQL QUOTENAME: <c>[name]</c> with <c>]</c> doubled.</summary>
    public static string Quote(string identifier)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier);
        if (identifier.Length > 128) throw new ArgumentException($"Identifier too long: {identifier[..20]}...");
        return "[" + identifier.Replace("]", "]]") + "]";
    }

    public static string Table(Lakeflow.Contracts.TableRef table) =>
        string.IsNullOrEmpty(table.Schema) ? Quote(table.Name) : $"{Quote(table.Schema)}.{Quote(table.Name)}";

    /// <summary>
    /// Keyset "after" predicate over ordered columns: <c>(a &gt; @p0) OR (a = @p0 AND b &gt; @p1) ...</c>.
    /// Adds one parameter per column with the given values.
    /// </summary>
    public static string After(IReadOnlyList<string> columnExpressions, IReadOnlyList<SqlParameter> values, string prefix = "")
    {
        if (columnExpressions.Count != values.Count) throw new ArgumentException("One value per column.");
        var terms = new List<string>();
        for (var i = 0; i < columnExpressions.Count; i++)
        {
            var term = new StringBuilder("(");
            for (var j = 0; j < i; j++) term.Append($"{prefix}{columnExpressions[j]} = {values[j].ParameterName} AND ");
            term.Append($"{prefix}{columnExpressions[i]} > {values[i].ParameterName})");
            terms.Add(term.ToString());
        }
        return "(" + string.Join(" OR ", terms) + ")";
    }

    /// <summary>
    /// Refuses anything but a single read: the text must start with SELECT and contain no statement separator
    /// outside quoted identifiers. A guard against programming mistakes; the read-only login is the real control.
    /// </summary>
    public static string ReadOnly(string sql)
    {
        var trimmed = sql.TrimStart();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only SELECT statements may run against a source.");
        var inBracket = false;
        var inString = false;
        foreach (var ch in trimmed)
        {
            if (inBracket) { if (ch == ']') inBracket = false; continue; }
            if (inString) { if (ch == '\'') inString = false; continue; }
            switch (ch)
            {
                case '[': inBracket = true; break;
                case '\'': inString = true; break;
                case ';': throw new InvalidOperationException("Only single statements may run against a source.");
            }
        }
        return sql;
    }
}
