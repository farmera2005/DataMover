namespace Lakeflow.Contracts;

public enum SchemaChangeKind
{
    Unchanged,
    /// <summary>New columns or harmless changes (length, nullability): captured automatically.</summary>
    Additive,
    /// <summary>A dropped or retyped column, or a changed key: the table pauses and the console alerts.</summary>
    Breaking,
}

public sealed record SchemaDiff(
    SchemaChangeKind Kind,
    IReadOnlyList<ColumnSchema> Added,
    IReadOnlyList<ColumnSchema> Dropped,
    IReadOnlyList<(ColumnSchema Before, ColumnSchema After)> Retyped,
    IReadOnlyList<(ColumnSchema Before, ColumnSchema After)> Relaxed,
    bool KeyChanged)
{
    public string Describe()
    {
        var parts = new List<string>();
        if (Added.Count > 0) parts.Add("added " + string.Join(", ", Added.Select(c => c.Name)));
        if (Dropped.Count > 0) parts.Add("dropped " + string.Join(", ", Dropped.Select(c => c.Name)));
        if (Retyped.Count > 0) parts.Add("retyped " + string.Join(", ", Retyped.Select(r => $"{r.Before.Name} {TypeText(r.Before)} -> {TypeText(r.After)}")));
        if (Relaxed.Count > 0) parts.Add("changed " + string.Join(", ", Relaxed.Select(r => r.Before.Name)));
        if (KeyChanged) parts.Add("primary key changed");
        return parts.Count == 0 ? "unchanged" : string.Join("; ", parts);
    }

    private static string TypeText(ColumnSchema c) => c switch
    {
        { Precision: not null, Scale: not null } => $"{c.DataType}({c.Precision},{c.Scale})",
        { MaxLength: -1 } => $"{c.DataType}(max)",
        { MaxLength: not null } => $"{c.DataType}({c.MaxLength})",
        _ => c.DataType,
    };
}

/// <summary>Classifies schema drift between the schema a table was captured with and its current schema.</summary>
public static class SchemaComparer
{
    public static SchemaDiff Compare(TableSchema previous, TableSchema current)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var before = previous.Columns.ToDictionary(c => c.Name, comparer);
        var after = current.Columns.ToDictionary(c => c.Name, comparer);

        var added = current.Columns.Where(c => !before.ContainsKey(c.Name)).ToList();
        var dropped = previous.Columns.Where(c => !after.ContainsKey(c.Name)).ToList();
        var retyped = new List<(ColumnSchema, ColumnSchema)>();
        var relaxed = new List<(ColumnSchema, ColumnSchema)>();
        foreach (var old in previous.Columns)
        {
            if (!after.TryGetValue(old.Name, out var now)) continue;
            if (!comparer.Equals(old.DataType, now.DataType) || old.Precision != now.Precision || old.Scale != now.Scale)
                retyped.Add((old, now));
            else if (old.MaxLength != now.MaxLength || old.Nullable != now.Nullable)
                relaxed.Add((old, now));
        }
        var keyChanged = !previous.KeyColumns.SequenceEqual(current.KeyColumns, comparer);

        var kind = dropped.Count > 0 || retyped.Count > 0 || keyChanged ? SchemaChangeKind.Breaking
            : added.Count > 0 || relaxed.Count > 0 ? SchemaChangeKind.Additive
            : SchemaChangeKind.Unchanged;
        return new SchemaDiff(kind, added, dropped, retyped, relaxed, keyChanged);
    }
}
