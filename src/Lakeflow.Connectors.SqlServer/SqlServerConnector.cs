using System.Data;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lakeflow.Connectors.SqlServer;

/// <summary>
/// Reads one SQL Server database. Capture by Change Tracking (when the database owner has enabled it; this
/// connector never enables anything), rowversion polling or timestamp polling.
/// Holds at most one connection, opened read-only with a 5 s connect timeout, 15 s command timeout,
/// 5 s lock timeout and low deadlock priority.
/// </summary>
public sealed class SqlServerConnector : ISourceConnector, IKeyReader
{
    private readonly SourceConfig _source;
    private readonly ICredentialStore _credentials;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, TimestampCapture> _timestampCaptures = new(StringComparer.OrdinalIgnoreCase);
    private SqlConnection? _connection;
    private int _disposed;

    public SqlServerConnector(SourceConfig source, ICredentialStore credentials, ILogger<SqlServerConnector>? log = null)
    {
        if (source.Engine != SourceEngine.SqlServer) throw new ArgumentException($"{source.SourceId} is not a SQL Server source.");
        _source = source;
        _credentials = credentials;
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    public SourceEngine Engine => SourceEngine.SqlServer;

    private int CommandTimeout => _source.Connection.CommandTimeoutSeconds;

    public ConnectorCapabilities GetCapabilities(TableConfig table) => table.Method switch
    {
        CaptureMethod.ChangeTracking => new(NativeDeletes: true, ProvidesBefore: false),
        CaptureMethod.RowVersion or CaptureMethod.TimestampPolling => new(NativeDeletes: false, ProvidesBefore: false),
        _ => throw new CaptureUnavailableException($"{table.Method} capture is not supported by the SQL Server connector yet."),
    };

    public Task<TableSchema> GetSchemaAsync(TableRef table, CancellationToken cancellationToken) =>
        RunAsync(connection => SchemaReader.ReadAsync(connection, table, CommandTimeout, cancellationToken), cancellationToken);

    /// <summary>Schema with the configured key applied; every capture needs a key.</summary>
    private static TableSchema Keyed(TableConfig table, TableSchema schema)
    {
        if (table.KeyColumns is { Count: > 0 } configured)
        {
            var key = configured.Select(k => schema.Find(k)?.Name
                ?? throw new CaptureUnavailableException($"Key column {k} is not in {table.Table}.")).ToList();
            return schema with { KeyColumns = key };
        }
        if (schema.KeyColumns.Count == 0)
            throw new CaptureUnavailableException($"Table {table.Table} has no primary key; set key_columns for it.");
        return schema;
    }

    public async IAsyncEnumerable<ChangeBatch> SnapshotAsync(TableConfig table, TableSchema schema, Checkpoint? resumeFrom,
        int batchSize, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        schema = Keyed(table, schema);
        var resuming = resumeFrom is { Phase: CheckpointPhase.Snapshot } && resumeFrom.Method == table.Method;
        var start = resuming
            ? resumeFrom!.Position
            : await RunAsync(c => Capture(table).StartPositionAsync(c, table, schema, cancellationToken), cancellationToken);
        var afterKey = resuming ? resumeFrom!.SnapshotAfterKey : null;
        if (!resuming) _log.LogInformation("Snapshot of {Table} starting at {Position}", table.Table, start);

        while (true)
        {
            var changes = await RunAsync(
                connection => ReadSnapshotPageAsync(connection, table, schema, afterKey, batchSize, cancellationToken), cancellationToken);
            var done = changes.Count < batchSize;
            if (changes.Count > 0) afterKey = changes[^1].Key;
            yield return new ChangeBatch(changes,
                done
                    ? new Checkpoint(table.Method, CheckpointPhase.Incremental, start)
                    : new Checkpoint(table.Method, CheckpointPhase.Snapshot, start, (JsonObject)afterKey!.DeepClone()),
                HasMore: !done);
            if (done) yield break;
        }
    }

    private async Task<List<RowChange>> ReadSnapshotPageAsync(SqlConnection connection, TableConfig table, TableSchema schema,
        JsonObject? afterKey, int batchSize, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeout;
        var keyColumns = schema.KeyColumns.Select(SqlText.Quote).ToList();
        var where = "";
        if (afterKey is not null)
        {
            var values = schema.KeyColumns.Select((k, i) => Parameters.ForColumn($"@k{i}", schema.Find(k)!, afterKey[k])).ToList();
            command.Parameters.AddRange(values.ToArray());
            where = " WHERE " + SqlText.After(keyColumns, values);
        }
        command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = batchSize });
        command.CommandText = SqlText.ReadOnly(
            $"SELECT TOP (@n) {Rows.SelectList(schema)} FROM {SqlText.Table(table.Table)}{where} ORDER BY {string.Join(", ", keyColumns)}");
        var changes = new List<RowChange>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = Rows.Read(reader, schema, 0);
            changes.Add(new RowChange(ChangeOp.Snapshot, ColumnValues.KeyOf(row, schema.KeyColumns), null, row));
        }
        return changes;
    }

    public Task<ChangeBatch> ReadChangesAsync(TableConfig table, TableSchema schema, Checkpoint checkpoint, int maxRows,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        if (checkpoint.Method != table.Method)
            throw new CheckpointExpiredException($"{table.Table} checkpoint is for {checkpoint.Method} but the table now uses {table.Method}; resnapshot.");
        if (checkpoint.Phase != CheckpointPhase.Incremental)
            throw new InvalidOperationException($"{table.Table} snapshot has not finished.");
        var keyed = Keyed(table, schema);
        return RunAsync(
            connection => Capture(table).ReadAsync(connection, table, keyed, checkpoint, maxRows, CommandTimeout, cancellationToken),
            cancellationToken);
    }

    public async IAsyncEnumerable<JsonObject> ReadKeysAsync(TableConfig table, TableSchema schema,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int page = 5000;
        var keyed = Keyed(table, schema);
        JsonObject? after = null;
        while (true)
        {
            var keys = await RunAsync(async connection =>
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = CommandTimeout;
                var keyColumns = keyed.KeyColumns.Select(SqlText.Quote).ToList();
                var where = "";
                if (after is not null)
                {
                    var values = keyed.KeyColumns.Select((k, i) => Parameters.ForColumn($"@k{i}", keyed.Find(k)!, after[k])).ToList();
                    command.Parameters.AddRange(values.ToArray());
                    where = " WHERE " + SqlText.After(keyColumns, values);
                }
                command.Parameters.Add(new SqlParameter("@n", SqlDbType.Int) { Value = page });
                var keySchema = keyed with { Columns = keyed.KeyColumns.Select(k => keyed.Find(k)!).ToList() };
                command.CommandText = SqlText.ReadOnly(
                    $"SELECT TOP (@n) {Rows.SelectList(keySchema)} FROM {SqlText.Table(table.Table)}{where} ORDER BY {string.Join(", ", keyColumns)}");
                var result = new List<JsonObject>(page);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) result.Add(Rows.Read(reader, keySchema, 0));
                return result;
            }, cancellationToken);
            foreach (var key in keys) yield return key;
            if (keys.Count < page) yield break;
            after = keys[^1];
        }
    }

    private ICapture Capture(TableConfig table)
    {
        switch (table.Method)
        {
            case CaptureMethod.ChangeTracking: return ChangeTrackingCapture.Instance;
            case CaptureMethod.RowVersion: return RowVersionCapture.Instance;
            case CaptureMethod.TimestampPolling:
                lock (_timestampCaptures)
                {
                    // Per table, because it remembers recently sent rows to drop look-back repeats.
                    if (!_timestampCaptures.TryGetValue(table.Table.QualifiedName, out var capture))
                        _timestampCaptures[table.Table.QualifiedName] = capture = new TimestampCapture();
                    return capture;
                }
            default: throw new CaptureUnavailableException($"{table.Method} capture is not supported by the SQL Server connector.");
        }
    }

    /// <summary>One operation at a time on the one connection; a broken connection is dropped and reopened next time.</summary>
    private async Task<T> RunAsync<T>(Func<SqlConnection, Task<T>> operation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var connection = await OpenAsync(ct);
            try
            {
                return await operation(connection);
            }
            catch (Exception ex) when (connection.State != ConnectionState.Open || ex is SqlException { Class: >= 20 })
            {
                await DropConnectionAsync();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        if (_connection is { State: ConnectionState.Open }) return _connection;
        await DropConnectionAsync();

        var settings = _source.Connection;
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = settings.Port is { } port ? $"tcp:{settings.Host},{port}"
                : string.IsNullOrEmpty(settings.Instance) ? settings.Host
                : $"{settings.Host}\\{settings.Instance}",
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "Lakeflow Agent",
            ConnectTimeout = settings.ConnectTimeoutSeconds,
            CommandTimeout = settings.CommandTimeoutSeconds,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = settings.TrustServerCertificate,
            Pooling = false,
            MultipleActiveResultSets = false,
            IntegratedSecurity = settings.Auth == AuthMethod.Windows,
        };
        if (!string.IsNullOrEmpty(settings.Database)) builder.InitialCatalog = settings.Database;

        SqlCredential? credential = null;
        if (settings.Auth == AuthMethod.SqlLogin)
        {
            if (string.IsNullOrEmpty(settings.UserName) || string.IsNullOrEmpty(settings.CredentialName))
                throw new CaptureUnavailableException($"{_source.SourceId}: a SQL login needs user_name and credential_name.");
            var secret = await _credentials.GetSecretAsync(settings.CredentialName, ct)
                ?? throw new CaptureUnavailableException($"{_source.SourceId}: no stored credential named '{settings.CredentialName}'.");
            var password = new SecureString();
            foreach (var ch in secret) password.AppendChar(ch);
            password.MakeReadOnly();
            credential = new SqlCredential(settings.UserName, password);
        }

        var connection = new SqlConnection(builder.ConnectionString, credential);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = SqlText.SessionSettings;
            command.CommandTimeout = settings.CommandTimeoutSeconds;
            await command.ExecuteNonQueryAsync(ct);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
        _log.LogInformation("Connected to {Host} database {Database}", settings.Host, settings.Database);
        return _connection = connection;
    }

    private async Task DropConnectionAsync()
    {
        if (_connection is null) return;
        await _connection.DisposeAsync();
        _connection = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _gate.WaitAsync();
        try { await DropConnectionAsync(); }
        finally { _gate.Release(); }
        _gate.Dispose();
    }
}
