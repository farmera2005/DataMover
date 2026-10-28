using System.Text;

namespace Lakeflow.Contracts;

/// <summary>Names shared by the agent, the control API and the Fabric notebooks.</summary>
public static class LakeNames
{
    /// <summary>Event Hubs partition key: <c>site_id|table</c>, so each table's changes stay in order.</summary>
    public static string PartitionKey(string siteId, string table) => $"{siteId}|{table}";

    /// <summary>One hub per division, e.g. <c>grain</c>.</summary>
    public static string HubName(Division division) => Identifier(division.ToString());

    /// <summary><c>bronze.&lt;division&gt;_&lt;source&gt;_&lt;table&gt;_changes</c>.</summary>
    public static string BronzeTable(Division division, string sourceSystem, TableRef table) =>
        $"bronze.{Identifier(division.ToString())}_{Identifier(sourceSystem)}_{TablePart(table)}_changes";

    /// <summary><c>silver.&lt;source&gt;_&lt;table&gt;</c>.</summary>
    public static string SilverTable(string sourceSystem, TableRef table) =>
        $"silver.{Identifier(sourceSystem)}_{TablePart(table)}";

    // The default schema adds nothing; any other schema is kept so dbo.X and audit.X stay apart.
    private static string TablePart(TableRef table) =>
        string.IsNullOrEmpty(table.Schema) || string.Equals(table.Schema, "dbo", StringComparison.OrdinalIgnoreCase)
            ? Identifier(table.Name)
            : $"{Identifier(table.Schema)}_{Identifier(table.Name)}";

    /// <summary>Lower case, letters/digits/underscores only, no repeated or edge underscores.</summary>
    public static string Identifier(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim().ToLowerInvariant())
        {
            var c = char.IsAsciiLetterOrDigit(ch) ? ch : '_';
            if (c == '_' && (sb.Length == 0 || sb[^1] == '_')) continue;
            sb.Append(c);
        }
        var id = sb.ToString().TrimEnd('_');
        if (id.Length == 0) throw new ArgumentException($"'{text}' has no usable characters for a lake name.", nameof(text));
        return id;
    }
}
