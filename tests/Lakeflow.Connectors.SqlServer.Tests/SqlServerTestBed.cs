using Lakeflow.Contracts;
using Lakeflow.Testing;
using Microsoft.Data.SqlClient;

namespace Lakeflow.Connectors.SqlServer.Tests;

/// <summary>
/// Standard contract tables in the shared test database. Every table also has a rowversion <c>rv</c> and a
/// <c>modified_at</c> the bed sets on every write, so one table shape serves all three capture methods.
/// </summary>
public sealed class SqlServerTestBed(TestDatabase db, CaptureMethod method) : IContractTestBed
{
    private readonly List<SqlServerConnector> _connectors = [];

    public async Task<TableConfig> CreateTableAsync(string name)
    {
        await db.ExecAsync(
            $"CREATE TABLE dbo.[{name}] (id int NOT NULL PRIMARY KEY, name nvarchar(100) NULL, qty decimal(18,4) NULL, " +
            $"rv rowversion NOT NULL, modified_at datetime2(3) NOT NULL CONSTRAINT [DF_{name}_m] DEFAULT SYSDATETIME())");
        await db.ExecAsync($"CREATE INDEX [IX_{name}_rv] ON dbo.[{name}] (rv)");
        await db.ExecAsync($"CREATE INDEX [IX_{name}_m] ON dbo.[{name}] (modified_at, id)");
        if (method == CaptureMethod.ChangeTracking)
            await db.ExecAsync($"ALTER TABLE dbo.[{name}] ENABLE CHANGE_TRACKING");
        return new TableConfig
        {
            Table = new TableRef("dbo", name),
            Method = method,
            WatermarkColumn = method == CaptureMethod.TimestampPolling ? "modified_at" : null,
        };
    }

    public ISourceConnector CreateConnector()
    {
        var connector = db.Connector();
        _connectors.Add(connector);
        return connector;
    }

    private Task ExecAsync(string sql, params (string Name, object? Value)[] parameters) => Task.Run(async () =>
    {
        await using var connection = new SqlConnection(db.AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    });

    public Task InsertAsync(TableConfig table, int id, string? name, decimal? qty) =>
        ExecAsync($"INSERT dbo.[{table.Table.Name}] (id, name, qty) VALUES (@id, @name, @qty)", ("@id", id), ("@name", name), ("@qty", qty));

    public Task UpdateAsync(TableConfig table, int id, string? name, decimal? qty) =>
        ExecAsync($"UPDATE dbo.[{table.Table.Name}] SET name = @name, qty = @qty, modified_at = SYSDATETIME() WHERE id = @id",
            ("@id", id), ("@name", name), ("@qty", qty));

    public Task DeleteAsync(TableConfig table, int id) =>
        ExecAsync($"DELETE dbo.[{table.Table.Name}] WHERE id = @id", ("@id", id));

    public Task AddColumnAsync(TableConfig table, string column) =>
        ExecAsync($"ALTER TABLE dbo.[{table.Table.Name}] ADD [{column}] nvarchar(200) NULL");

    public Task DropColumnAsync(TableConfig table, string column) =>
        ExecAsync($"ALTER TABLE dbo.[{table.Table.Name}] DROP COLUMN [{column}]");

    public Task RetypeQtyAsync(TableConfig table) =>
        ExecAsync($"ALTER TABLE dbo.[{table.Table.Name}] ALTER COLUMN qty float NULL");

    public Task SetTextAsync(TableConfig table, int id, string column, string? value) =>
        ExecAsync($"UPDATE dbo.[{table.Table.Name}] SET [{column}] = @v, modified_at = SYSDATETIME() WHERE id = @id", ("@id", id), ("@v", value));

    public async ValueTask DisposeAsync()
    {
        foreach (var connector in _connectors) await connector.DisposeAsync();
    }
}

public class SqlServerChangeTrackingContractTests : ConnectorContractTests
{
    protected override string? SkipReason => TestDatabase.SkipReason;
    protected override async Task<IContractTestBed> CreateBedAsync() => new SqlServerTestBed(await TestDatabase.GetAsync(), CaptureMethod.ChangeTracking);
}

public class SqlServerRowVersionContractTests : ConnectorContractTests
{
    protected override string? SkipReason => TestDatabase.SkipReason;
    protected override async Task<IContractTestBed> CreateBedAsync() => new SqlServerTestBed(await TestDatabase.GetAsync(), CaptureMethod.RowVersion);
}

public class SqlServerTimestampContractTests : ConnectorContractTests
{
    protected override string? SkipReason => TestDatabase.SkipReason;
    protected override async Task<IContractTestBed> CreateBedAsync() => new SqlServerTestBed(await TestDatabase.GetAsync(), CaptureMethod.TimestampPolling);
}
