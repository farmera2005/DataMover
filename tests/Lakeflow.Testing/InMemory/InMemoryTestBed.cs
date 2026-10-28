using Lakeflow.Contracts;

namespace Lakeflow.Testing.InMemory;

public sealed class InMemoryTestBed(CaptureMethod method) : IContractTestBed
{
    public InMemorySource Source { get; } = new();

    public Task<TableConfig> CreateTableAsync(string name)
    {
        Source.CreateTable(name);
        return Task.FromResult(new TableConfig { Table = new TableRef("dbo", name), Method = method });
    }

    public ISourceConnector CreateConnector() => new InMemoryConnector(Source);

    public Task InsertAsync(TableConfig table, int id, string? name, decimal? qty) => UpdateAsync(table, id, name, qty);

    public Task UpdateAsync(TableConfig table, int id, string? name, decimal? qty)
    {
        Source.Upsert(table.Table.Name, id, new Dictionary<string, object?> { ["name"] = name, ["qty"] = qty });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(TableConfig table, int id)
    {
        Source.Delete(table.Table.Name, id);
        return Task.CompletedTask;
    }

    public Task AddColumnAsync(TableConfig table, string column)
    {
        Source.AddColumn(table.Table.Name, new ColumnSchema(column, "nvarchar", true, 0, MaxLength: 200));
        return Task.CompletedTask;
    }

    public Task DropColumnAsync(TableConfig table, string column)
    {
        Source.DropColumn(table.Table.Name, column);
        return Task.CompletedTask;
    }

    public Task RetypeQtyAsync(TableConfig table)
    {
        Source.RetypeColumn(table.Table.Name, "qty", "float");
        return Task.CompletedTask;
    }

    public Task SetTextAsync(TableConfig table, int id, string column, string? value)
    {
        Source.Upsert(table.Table.Name, id, new Dictionary<string, object?> { [column] = value });
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
