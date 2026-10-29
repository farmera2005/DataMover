using Lakeflow.Contracts;
using Lakeflow.Testing;
using Xunit;

namespace Lakeflow.Connectors.SqlServer.Tests;

public class SchemaBrowserTests
{
    [SkippableFact]
    public async Task Connection_test_reports_the_server_and_login()
    {
        Skip.If(TestDatabase.SkipReason is not null, TestDatabase.SkipReason);
        var db = await TestDatabase.GetAsync();
        await using var connector = db.Connector();

        var result = await connector.TestConnectionAsync(CancellationToken.None);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(db.Name, result.Database);
        Assert.Equal(TestDatabase.ReadOnlyUser, result.Login);
        Assert.True(result.ChangeTrackingEnabled);
        Assert.StartsWith("16.", result.ServerVersion);
    }

    [SkippableFact]
    public async Task Failed_connections_say_what_to_fix()
    {
        Skip.If(TestDatabase.SkipReason is not null, TestDatabase.SkipReason);
        var db = await TestDatabase.GetAsync();
        await using var wrongPassword = db.Connector("nope");
        var denied = await wrongPassword.TestConnectionAsync(CancellationToken.None);
        Assert.False(denied.Succeeded);
        Assert.StartsWith("Login failed", denied.Error);

        var unreachable = db.Source() with { Connection = db.Source().Connection with { Port = 1, ConnectTimeoutSeconds = 2 } };
        await using var nowhere = new SqlServerConnector(unreachable,
            new InMemoryCredentialStore(new Dictionary<string, string> { [TestDatabase.CredentialName] = db.ReadOnlyPassword }));
        var lost = await nowhere.TestConnectionAsync(CancellationToken.None);
        Assert.False(lost.Succeeded);
        Assert.Contains("could not be reached", lost.Error);
    }

    [SkippableFact]
    public async Task Browse_lists_readable_tables_and_suggests_how_to_capture_each()
    {
        Skip.If(TestDatabase.SkipReason is not null, TestDatabase.SkipReason);
        var db = await TestDatabase.CreateIsolatedAsync();
        await db.ExecAsync("CREATE TABLE dbo.Tracked (id int PRIMARY KEY, v int); ALTER TABLE dbo.Tracked ENABLE CHANGE_TRACKING");
        await db.ExecAsync("INSERT dbo.Tracked SELECT value, 0 FROM GENERATE_SERIES(1, 500)");
        await db.ExecAsync("CREATE TABLE dbo.Versioned (a char(2) NOT NULL, b int NOT NULL, rv rowversion, PRIMARY KEY (a, b))");
        await db.ExecAsync("CREATE TABLE dbo.Stamped (id int PRIMARY KEY, modified datetime2 NOT NULL); CREATE INDEX IX_m ON dbo.Stamped (modified)");
        await db.ExecAsync("CREATE TABLE dbo.Heap (code varchar(5), v int)");
        await db.ExecAsync("CREATE TABLE dbo.Plain (id int PRIMARY KEY, modified datetime2)");
        await db.ExecAsync("CREATE SCHEMA hidden");
        await db.ExecAsync("CREATE TABLE hidden.Secret (id int PRIMARY KEY)"); // no grant on this schema
        await using var connector = db.Connector();

        var result = await connector.BrowseAsync(100, CancellationToken.None);

        var tables = result.Tables.ToDictionary(t => t.Table);
        Assert.False(result.Truncated);
        Assert.DoesNotContain("hidden.Secret", tables.Keys);
        Assert.Equal("dbo.Tracked", result.Tables[0].Table); // largest first
        Assert.Equal(500, tables["dbo.Tracked"].RowCount);
        Assert.Equal(CaptureMethod.ChangeTracking, tables["dbo.Tracked"].SuggestedMethod);
        Assert.Equal(CaptureMethod.RowVersion, tables["dbo.Versioned"].SuggestedMethod);
        Assert.Equal(["a", "b"], tables["dbo.Versioned"].KeyColumns);
        Assert.Equal("rv", tables["dbo.Versioned"].RowVersionColumn);
        Assert.Equal((CaptureMethod.TimestampPolling, "modified"), (tables["dbo.Stamped"].SuggestedMethod, tables["dbo.Stamped"].SuggestedWatermark));
        Assert.Null(tables["dbo.Heap"].SuggestedMethod);
        Assert.Contains("No primary key", tables["dbo.Heap"].Note);
        Assert.Null(tables["dbo.Plain"].SuggestedMethod); // its date column has no index
        Assert.Contains("would scan", tables["dbo.Plain"].Note);

        var limited = await connector.BrowseAsync(2, CancellationToken.None);
        Assert.Equal(2, limited.Tables.Count);
        Assert.True(limited.Truncated);
    }
}
