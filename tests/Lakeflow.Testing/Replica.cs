using System.Text.Json.Nodes;
using Lakeflow.Contracts;

namespace Lakeflow.Testing;

/// <summary>
/// Applies changes the way the silver MERGE does (upsert snapshot/insert/update, apply deletes), so the contract
/// suite can compare end state rather than exact change sequences, which differ legitimately between
/// methods (Change Tracking returns net changes, CDC every change).
/// </summary>
public sealed class Replica
{
    private readonly Dictionary<string, JsonObject> _rows = new(StringComparer.Ordinal);

    public int Count => _rows.Count;
    public IEnumerable<JsonObject> Rows => _rows.Values;
    public IEnumerable<JsonObject> Keys => _rows.Values.Select(r => ColumnValues.KeyOf(r, ["id"]));

    public void Apply(IEnumerable<RowChange> changes)
    {
        foreach (var change in changes) Apply(change);
    }

    public void Apply(RowChange change)
    {
        var key = ColumnValues.KeyText(change.Key);
        if (change.Op == ChangeOp.Delete)
        {
            _rows.Remove(key);
            return;
        }
        if (change.After is null) throw new InvalidOperationException($"{change.Op} for {key} has no after image.");
        _rows[key] = (JsonObject)change.After.DeepClone();
    }

    public JsonObject? Get(int id) => _rows.GetValueOrDefault(ColumnValues.KeyText(new JsonObject { ["id"] = id }));
}
