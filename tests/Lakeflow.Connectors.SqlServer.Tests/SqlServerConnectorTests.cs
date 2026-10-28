using System.Text.Json.Nodes;
using Lakeflow.Contracts;
using Lakeflow.Testing;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Lakeflow.Connectors.SqlServer.Tests;

/// <summary>SQL Server behaviour beyond the shared contract.</summary>
public class SqlServerConnectorTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private readonly List<SqlServerConnector> _connectors = [];

    public async Task InitializeAsync()
    {
        if (TestDatabase.SkipReason is null) _db = await TestDatabase.GetAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var connector in _connectors) await connector.DisposeAsync();
    }

    private SqlServerConnector Connect(string? password = null)
    {
        Skip.If(TestDatabase.SkipReason is not null, TestDatabase.SkipReason);
        var connector = _db.Connector(password);
        _connectors.Add(connector);
        return connector;
    }

    private static string NewName(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..28];

    private static async Task<List<RowChange>> SnapshotAsync(ISourceConnector connector, TableConfig table, int batchSize = 100)
    {
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        var rows = new List<RowChange>();
        await foreach (var batch in connector.SnapshotAsync(table, schema, null, batchSize, CancellationToken.None)) rows.AddRange(batch.Changes);
        return rows;
    }

    [SkippableFact]
    public async Task Read_only_login_cannot_write()
    {
        Connect();
        var name = NewName("ro");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (id int PRIMARY KEY)");
        var readOnly = new SqlConnectionStringBuilder(_db.AdminConnectionString) { UserID = TestDatabase.ReadOnlyUser, Password = _db.ReadOnlyPassword };
        await using var connection = new SqlConnection(readOnly.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"INSERT dbo.[{name}] VALUES (1)", connection);
        var error = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(229, error.Number); // permission denied
    }

    [SkippableFact]
    public async Task Sessions_use_the_safety_settings()
    {
        var connector = Connect();
        var name = NewName("sess");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (id int PRIMARY KEY)");
        await connector.GetSchemaAsync(new TableRef("dbo", name), CancellationToken.None);

        await using var admin = new SqlConnection(_db.AdminConnectionString);
        await admin.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT lock_timeout, deadlock_priority, transaction_isolation_level FROM sys.dm_exec_sessions " +
            "WHERE program_name = 'Lakeflow Agent' AND login_name = @login", admin);
        command.Parameters.AddWithValue("@login", TestDatabase.ReadOnlyUser);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "connector session not found");
        Assert.Equal(5000, reader.GetInt32(0));
        Assert.Equal(-5, reader.GetInt32(1)); // LOW
        Assert.Equal(2, reader.GetInt16(2)); // READ COMMITTED
    }

    [SkippableFact]
    public async Task Change_tracking_not_enabled_on_the_table_is_reported()
    {
        var connector = Connect();
        var name = NewName("noct");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (id int PRIMARY KEY)");
        var table = new TableConfig { Table = new TableRef("dbo", name), Method = CaptureMethod.ChangeTracking };

        var error = await Assert.ThrowsAsync<CaptureUnavailableException>(() => SnapshotAsync(connector, table));
        Assert.Contains("not enabled", error.Message);
    }

    [SkippableFact]
    public async Task Checkpoint_older_than_change_tracking_retention_needs_a_resnapshot()
    {
        var connector = Connect();
        var name = NewName("expire");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (id int PRIMARY KEY); ALTER TABLE dbo.[{name}] ENABLE CHANGE_TRACKING");
        await _db.ExecAsync($"INSERT dbo.[{name}] VALUES (1)");
        var table = new TableConfig { Table = new TableRef("dbo", name), Method = CaptureMethod.ChangeTracking };
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        Checkpoint checkpoint = null!;
        await foreach (var batch in connector.SnapshotAsync(table, schema, null, 10, CancellationToken.None)) checkpoint = batch.Next;

        // Re-enabling tracking discards its history, as retention cleanup would.
        await _db.ExecAsync($"INSERT dbo.[{name}] VALUES (2)");
        await _db.ExecAsync($"ALTER TABLE dbo.[{name}] DISABLE CHANGE_TRACKING; ALTER TABLE dbo.[{name}] ENABLE CHANGE_TRACKING");

        await Assert.ThrowsAsync<CheckpointExpiredException>(() =>
            connector.ReadChangesAsync(table, schema, checkpoint, 10, CancellationToken.None));
    }

    [SkippableFact]
    public async Task A_transaction_larger_than_a_page_is_returned_whole()
    {
        var connector = Connect();
        var name = NewName("bigtx");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (id int PRIMARY KEY, v int); ALTER TABLE dbo.[{name}] ENABLE CHANGE_TRACKING");
        var table = new TableConfig { Table = new TableRef("dbo", name), Method = CaptureMethod.ChangeTracking };
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        Checkpoint checkpoint = null!;
        await foreach (var batch in connector.SnapshotAsync(table, schema, null, 10, CancellationToken.None)) checkpoint = batch.Next;

        await _db.ExecAsync($"INSERT dbo.[{name}] VALUES (1, 0)");
        await _db.ExecAsync($"INSERT dbo.[{name}] SELECT value + 1, 0 FROM GENERATE_SERIES(1, 25)");
        await _db.ExecAsync($"INSERT dbo.[{name}] VALUES (100, 0)");

        var first = await connector.ReadChangesAsync(table, schema, checkpoint, 7, CancellationToken.None);
        Assert.Equal([1], first.Changes.Select(c => c.Key["id"]!.GetValue<int>())); // ends before the big transaction
        Assert.True(first.HasMore);
        var second = await connector.ReadChangesAsync(table, schema, first.Next, 7, CancellationToken.None);
        Assert.Equal(25, second.Changes.Count); // the whole 25-row transaction, though the page is 7
        Assert.True(second.HasMore);
        var third = await connector.ReadChangesAsync(table, schema, second.Next, 7, CancellationToken.None);
        Assert.Equal([100], third.Changes.Select(c => c.Key["id"]!.GetValue<int>()));
        Assert.False(third.HasMore);
    }

    [SkippableFact]
    public async Task Composite_keys_page_without_gaps_or_repeats()
    {
        var connector = Connect();
        var name = NewName("comp");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (site char(2) NOT NULL, ticket int NOT NULL, v varchar(10), PRIMARY KEY (site, ticket))");
        await _db.ExecAsync($"INSERT dbo.[{name}] SELECT s.site, t.value, 'x' FROM (VALUES ('AA'), ('ab'), ('BB')) s(site) CROSS JOIN GENERATE_SERIES(1, 4) t");
        var table = new TableConfig { Table = new TableRef("dbo", name), Method = CaptureMethod.RowVersion, KeyColumns = ["site", "ticket"] };
        await _db.ExecAsync($"ALTER TABLE dbo.[{name}] ADD rv rowversion");

        var rows = await SnapshotAsync(connector, table, batchSize: 5);
        Assert.Equal(12, rows.Count);
        Assert.Equal(12, rows.Select(r => ColumnValues.KeyText(r.Key)).Distinct().Count());
    }

    [SkippableFact]
    public async Task Tables_without_a_key_need_key_columns()
    {
        var connector = Connect();
        var name = NewName("nokey");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (code varchar(10) NOT NULL, v int, rv rowversion)");
        await _db.ExecAsync($"INSERT dbo.[{name}] (code, v) VALUES ('a', 1), ('b', 2)");

        var noKey = new TableConfig { Table = new TableRef("dbo", name), Method = CaptureMethod.RowVersion };
        var error = await Assert.ThrowsAsync<CaptureUnavailableException>(() => SnapshotAsync(connector, noKey));
        Assert.Contains("key_columns", error.Message);

        var rows = await SnapshotAsync(connector, noKey with { KeyColumns = ["code"] });
        Assert.Equal(["{\"code\":\"a\"}", "{\"code\":\"b\"}"], rows.Select(r => ColumnValues.KeyText(r.Key)));
    }

    [SkippableFact]
    public async Task Values_of_every_common_type_land_in_the_shared_encoding()
    {
        var connector = Connect();
        var name = NewName("types");
        await _db.ExecAsync(
            $"CREATE TABLE dbo.[{name}] (id bigint PRIMARY KEY, b bit, m money, f float, d date, t time(3), dt datetime, dt2 datetime2(7), " +
            "dto datetimeoffset(3), g uniqueidentifier, bin varbinary(10), x xml, h hierarchyid, geo geography, big nvarchar(max), c char(3), n numeric(10,3))");
        await _db.ExecAsync(
            $"INSERT dbo.[{name}] VALUES (9000000000, 1, 12.3456, 0.5, '2026-11-01', '13:05:01.250', '2026-11-01T01:30:00', " +
            "'2026-11-01T01:30:00.1234567', '2026-11-01T01:30:00.000-04:00', '6F9619FF-8B86-D011-B42D-00C04FC964FF', 0x010203, " +
            "'<a>1</a>', '/1/2/', geography::Point(40.5, -84.6, 4326), REPLICATE(CAST(N'é' AS nvarchar(max)), 5000), 'ab', 1234567.891)");
        var table = new TableConfig { Table = new TableRef("dbo", name), Method = CaptureMethod.TimestampPolling, WatermarkColumn = "dt2" };

        var row = Assert.Single(await SnapshotAsync(connector, table)).After!;

        Assert.Equal(9000000000, row["id"]!.GetValue<long>());
        Assert.True(row["b"]!.GetValue<bool>());
        Assert.Equal(12.3456m, row["m"]!.GetValue<decimal>());
        Assert.Equal(0.5, row["f"]!.GetValue<double>());
        Assert.Equal("2026-11-01", row["d"]!.GetValue<string>());
        Assert.Equal("13:05:01.2500000", row["t"]!.GetValue<string>());
        Assert.Equal("2026-11-01T01:30:00.0000000", row["dt"]!.GetValue<string>());
        Assert.Equal("2026-11-01T01:30:00.1234567", row["dt2"]!.GetValue<string>());
        Assert.Equal("2026-11-01T01:30:00.0000000-04:00", row["dto"]!.GetValue<string>());
        Assert.Equal("6f9619ff-8b86-d011-b42d-00c04fc964ff", row["g"]!.GetValue<string>());
        Assert.Equal("AQID", row["bin"]!.GetValue<string>());
        Assert.Equal("<a>1</a>", row["x"]!.GetValue<string>());
        Assert.Equal("/1/2/", row["h"]!.GetValue<string>());
        Assert.StartsWith("POINT (-84.6 40.5", row["geo"]!.GetValue<string>());
        Assert.Equal(5000, row["big"]!.GetValue<string>().Length);
        Assert.Equal("ab ", row["c"]!.GetValue<string>());
        Assert.Equal(1234567.891m, row["n"]!.GetValue<decimal>());
    }

    [SkippableFact]
    public async Task Rowversion_polling_never_passes_an_open_transaction()
    {
        Connect();
        _db = await TestDatabase.CreateIsolatedAsync();
        var connector = Connect();
        var name = NewName("inflight");
        await _db.ExecAsync($"CREATE TABLE dbo.[{name}] (id int PRIMARY KEY, rv rowversion)");
        var table = new TableConfig { Table = new TableRef("dbo", name), Method = CaptureMethod.RowVersion };
        var schema = await connector.GetSchemaAsync(table.Table, CancellationToken.None);
        Checkpoint checkpoint = null!;
        await foreach (var batch in connector.SnapshotAsync(table, schema, null, 10, CancellationToken.None)) checkpoint = batch.Next;

        // Row 1 is written in a transaction that stays open while row 2 commits with a higher rowversion.
        await using var open = new SqlConnection(_db.AdminConnectionString);
        await open.OpenAsync();
        await using var transaction = (SqlTransaction)await open.BeginTransactionAsync();
        await using (var insert = new SqlCommand($"INSERT dbo.[{name}] (id) VALUES (1)", open, transaction)) await insert.ExecuteNonQueryAsync();
        await _db.ExecAsync($"INSERT dbo.[{name}] (id) VALUES (2)");

        var whileOpen = await connector.ReadChangesAsync(table, schema, checkpoint, 10, CancellationToken.None);
        Assert.Empty(whileOpen.Changes); // row 2 waits: reading it would move the checkpoint past row 1
        await transaction.CommitAsync();

        var after = await connector.ReadChangesAsync(table, schema, whileOpen.Next, 10, CancellationToken.None);
        Assert.Equal([1, 2], after.Changes.Select(c => c.Key["id"]!.GetValue<int>()).Order());
    }

    [SkippableFact]
    public async Task Missing_credential_and_wrong_password_are_clear_errors()
    {
        Skip.If(TestDatabase.SkipReason is not null, TestDatabase.SkipReason);
        await using var noCredential = new SqlServerConnector(_db.Source(), new InMemoryCredentialStore(new Dictionary<string, string>()));
        var missing = await Assert.ThrowsAsync<CaptureUnavailableException>(() =>
            noCredential.GetSchemaAsync(new TableRef("dbo", "x"), CancellationToken.None));
        Assert.Contains("no stored credential", missing.Message);

        var wrong = Connect("not-the-password");
        var denied = await Assert.ThrowsAsync<SqlException>(() => wrong.GetSchemaAsync(new TableRef("dbo", "x"), CancellationToken.None));
        Assert.Equal(18456, denied.Number); // login failed
        Assert.DoesNotContain("not-the-password", denied.ToString());
    }
}
