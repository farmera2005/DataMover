using Lakeflow.Contracts;

namespace Lakeflow.Testing;

/// <summary>
/// A real (or simulated) source the contract suite can change underneath a connector.
/// Every table it creates has the standard columns: <c>id int</c> (primary key), <c>name</c> (text, nullable),
/// <c>qty</c> (decimal(18,4), nullable). The bed writes with its own admin access; the connector it hands out
/// must use read-only access.
/// </summary>
public interface IContractTestBed : IAsyncDisposable
{
    /// <summary>A fresh, empty standard table with capture already set up for the method under test.</summary>
    Task<TableConfig> CreateTableAsync(string name);

    ISourceConnector CreateConnector();

    Task InsertAsync(TableConfig table, int id, string? name, decimal? qty);
    Task UpdateAsync(TableConfig table, int id, string? name, decimal? qty);
    Task DeleteAsync(TableConfig table, int id);

    /// <summary>Adds a nullable text column.</summary>
    Task AddColumnAsync(TableConfig table, string column);
    Task DropColumnAsync(TableConfig table, string column);
    /// <summary>Changes <c>qty</c> to a different type.</summary>
    Task RetypeQtyAsync(TableConfig table);
    /// <summary>Sets a column added by <see cref="AddColumnAsync"/>.</summary>
    Task SetTextAsync(TableConfig table, int id, string column, string? value);

    /// <summary>
    /// Waits until changes written so far are visible to the connector's capture method
    /// (e.g. past a timestamp-polling look-back, or a CDC capture job). Most methods return at once.
    /// </summary>
    Task SettleAsync() => Task.CompletedTask;
}
