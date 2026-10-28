using Lakeflow.Contracts;

namespace Lakeflow.Testing.InMemory;

/// <summary>
/// A simulated source database: tables with a primary key <c>id</c>, a global change version (like Change
/// Tracking) and a per-row version (like a rowversion column). Thread-safe so tests can write while a
/// connector reads.
/// </summary>
public sealed class InMemorySource
{
    internal sealed class Table
    {
        public List<ColumnSchema> Columns { get; } = [];
        public SortedDictionary<int, (Dictionary<string, object?> Values, long RowVersion)> Rows { get; } = [];
        public List<(long Version, int Id)> Log { get; } = [];
    }

    private readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);
    private long _version;

    internal object Gate { get; } = new();
    public long CurrentVersion { get { lock (Gate) return _version; } }

    /// <summary>Oldest change version still kept, like Change Tracking retention; older checkpoints have expired.</summary>
    public long MinValidVersion { get; set; }

    public void CreateTable(string name)
    {
        lock (Gate)
        {
            var table = new Table();
            table.Columns.Add(new ColumnSchema("id", "int", false, 1));
            table.Columns.Add(new ColumnSchema("name", "nvarchar", true, 2, MaxLength: 100));
            table.Columns.Add(new ColumnSchema("qty", "decimal", true, 3, Precision: 18, Scale: 4));
            _tables.Add(name, table);
        }
    }

    internal Table Get(string name) =>
        _tables.TryGetValue(name, out var table) ? table : throw new InvalidOperationException($"No table {name}.");

    /// <summary>Ids currently in the table.</summary>
    public IReadOnlyList<int> Ids(string table)
    {
        lock (Gate) return Get(table).Rows.Keys.ToList();
    }

    public void Upsert(string table, int id, IReadOnlyDictionary<string, object?> values)
    {
        lock (Gate)
        {
            var t = Get(table);
            var row = t.Rows.TryGetValue(id, out var existing) ? existing.Values : t.Columns.ToDictionary(c => c.Name, _ => (object?)null);
            row["id"] = id;
            foreach (var (column, value) in values)
            {
                if (t.Columns.All(c => c.Name != column)) throw new InvalidOperationException($"No column {column}.");
                row[column] = value;
            }
            var version = ++_version;
            t.Rows[id] = (row, version);
            t.Log.Add((version, id));
        }
    }

    public void Delete(string table, int id)
    {
        lock (Gate)
        {
            var t = Get(table);
            if (!t.Rows.Remove(id)) throw new InvalidOperationException($"No row {id}.");
            t.Log.Add((++_version, id));
        }
    }

    public void AddColumn(string table, ColumnSchema column)
    {
        lock (Gate)
        {
            var t = Get(table);
            t.Columns.Add(column with { Ordinal = t.Columns.Max(c => c.Ordinal) + 1 });
            foreach (var row in t.Rows.Values) row.Values[column.Name] = null;
        }
    }

    public void DropColumn(string table, string column)
    {
        lock (Gate)
        {
            var t = Get(table);
            t.Columns.RemoveAll(c => c.Name == column);
            foreach (var row in t.Rows.Values) row.Values.Remove(column);
        }
    }

    public void RetypeColumn(string table, string column, string dataType)
    {
        lock (Gate)
        {
            var t = Get(table);
            var index = t.Columns.FindIndex(c => c.Name == column);
            t.Columns[index] = t.Columns[index] with { DataType = dataType, Precision = null, Scale = null };
        }
    }
}
