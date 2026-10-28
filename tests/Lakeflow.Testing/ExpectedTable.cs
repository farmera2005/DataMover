using Lakeflow.Contracts;
using Xunit;

namespace Lakeflow.Testing;

/// <summary>What the source table holds, tracked alongside the test bed's writes.</summary>
public sealed class ExpectedTable
{
    public readonly record struct Row(string? Name, decimal? Qty, Dictionary<string, string?> Extra);

    private readonly SortedDictionary<int, Row> _rows = [];
    private readonly IContractTestBed _bed;
    private readonly TableConfig _table;

    public ExpectedTable(IContractTestBed bed, TableConfig table) => (_bed, _table) = (bed, table);

    public int Count => _rows.Count;

    public async Task InsertAsync(int id, string? name, decimal? qty)
    {
        await _bed.InsertAsync(_table, id, name, qty);
        _rows[id] = new Row(name, qty, []);
    }

    public async Task UpdateAsync(int id, string? name, decimal? qty)
    {
        await _bed.UpdateAsync(_table, id, name, qty);
        _rows[id] = _rows[id] with { Name = name, Qty = qty };
    }

    public async Task DeleteAsync(int id)
    {
        await _bed.DeleteAsync(_table, id);
        _rows.Remove(id);
    }

    public async Task SetTextAsync(int id, string column, string? value)
    {
        await _bed.SetTextAsync(_table, id, column, value);
        _rows[id].Extra[column] = value;
    }

    public void AssertMatches(Replica replica)
    {
        Assert.Equal(_rows.Count, replica.Count);
        foreach (var (id, row) in _rows)
        {
            var landed = replica.Get(id);
            Assert.True(landed is not null, $"row {id} is missing from the replica");
            Assert.Equal(id, landed!["id"]!.GetValue<int>());
            Assert.Equal(row.Name, landed["name"]?.GetValue<string>());
            Assert.Equal(row.Qty, landed["qty"]?.GetValue<decimal>());
            foreach (var (column, value) in row.Extra)
                Assert.Equal(value, landed[column]?.GetValue<string>());
        }
    }
}
