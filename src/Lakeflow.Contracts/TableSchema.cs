namespace Lakeflow.Contracts;

/// <summary>A source table, qualified by schema where the engine has one.</summary>
public sealed record TableRef(string? Schema, string Name)
{
    public string QualifiedName => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
    public override string ToString() => QualifiedName;

    public static TableRef Parse(string qualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedName);
        var dot = qualifiedName.IndexOf('.');
        return dot < 0 ? new TableRef(null, qualifiedName) : new TableRef(qualifiedName[..dot], qualifiedName[(dot + 1)..]);
    }
}

/// <param name="DataType">Engine type name, lower case (e.g. <c>decimal</c>, <c>nvarchar</c>).</param>
/// <param name="MaxLength">Character or byte length; -1 for max; null when not applicable.</param>
public sealed record ColumnSchema(
    string Name,
    string DataType,
    bool Nullable,
    int Ordinal,
    int? MaxLength = null,
    int? Precision = null,
    int? Scale = null);

public sealed record TableSchema(TableRef Table, IReadOnlyList<ColumnSchema> Columns, IReadOnlyList<string> KeyColumns)
{
    public ColumnSchema? Find(string column) =>
        Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
}
