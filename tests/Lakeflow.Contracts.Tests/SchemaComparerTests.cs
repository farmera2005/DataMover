using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Contracts.Tests;

public class SchemaComparerTests
{
    private static readonly TableRef Table = new("dbo", "Tickets");

    private static TableSchema Schema(params ColumnSchema[] columns) => new(Table, columns, ["Id"]);

    private static readonly ColumnSchema Id = new("Id", "int", false, 1);
    private static readonly ColumnSchema Name = new("Name", "nvarchar", true, 2, MaxLength: 50);
    private static readonly ColumnSchema Net = new("Net", "decimal", true, 3, Precision: 18, Scale: 2);

    [Fact]
    public void Same_schema_is_unchanged() =>
        Assert.Equal(SchemaChangeKind.Unchanged, SchemaComparer.Compare(Schema(Id, Name, Net), Schema(Id, Name, Net)).Kind);

    [Fact]
    public void Column_name_case_and_order_do_not_matter() =>
        Assert.Equal(SchemaChangeKind.Unchanged,
            SchemaComparer.Compare(Schema(Id, Name, Net), Schema(Net, Id, Name with { Name = "NAME" })).Kind);

    [Fact]
    public void New_column_is_additive()
    {
        var diff = SchemaComparer.Compare(Schema(Id, Name), Schema(Id, Name, Net));
        Assert.Equal(SchemaChangeKind.Additive, diff.Kind);
        Assert.Equal("Net", Assert.Single(diff.Added).Name);
        Assert.Equal("added Net", diff.Describe());
    }

    [Fact]
    public void Wider_or_nullable_column_is_additive()
    {
        var diff = SchemaComparer.Compare(Schema(Id, Name), Schema(Id, Name with { MaxLength = 100, Nullable = false }));
        Assert.Equal(SchemaChangeKind.Additive, diff.Kind);
        Assert.Single(diff.Relaxed);
    }

    [Fact]
    public void Dropped_column_is_breaking()
    {
        var diff = SchemaComparer.Compare(Schema(Id, Name, Net), Schema(Id, Net, new ColumnSchema("Other", "int", true, 4)));
        Assert.Equal(SchemaChangeKind.Breaking, diff.Kind);
        Assert.Equal("Name", Assert.Single(diff.Dropped).Name);
        Assert.Equal("added Other; dropped Name", diff.Describe());
    }

    [Fact]
    public void Retyped_or_rescaled_column_is_breaking()
    {
        Assert.Equal(SchemaChangeKind.Breaking, SchemaComparer.Compare(Schema(Id, Net), Schema(Id, Net with { DataType = "float", Precision = null, Scale = null })).Kind);
        var rescaled = SchemaComparer.Compare(Schema(Id, Net), Schema(Id, Net with { Scale = 4 }));
        Assert.Equal(SchemaChangeKind.Breaking, rescaled.Kind);
        Assert.Equal("retyped Net decimal(18,2) -> decimal(18,4)", rescaled.Describe());
    }

    [Fact]
    public void Key_change_is_breaking()
    {
        var diff = SchemaComparer.Compare(Schema(Id, Name), new TableSchema(Table, [Id, Name], ["Id", "Name"]));
        Assert.Equal(SchemaChangeKind.Breaking, diff.Kind);
        Assert.True(diff.KeyChanged);
    }
}
