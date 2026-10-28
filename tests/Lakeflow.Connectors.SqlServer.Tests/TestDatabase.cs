using System.Security.Cryptography;
using Lakeflow.Contracts;
using Lakeflow.Testing;
using Microsoft.Data.SqlClient;

namespace Lakeflow.Connectors.SqlServer.Tests;

/// <summary>
/// One throw-away database per test run on the server named by MSSQL_TEST_CONNECTION (an admin connection
/// string; scripts/test-sqlserver.sh starts one in Docker), with Change Tracking on and a read-only login
/// that has only SELECT and VIEW CHANGE TRACKING. Connectors under test always use that login.
/// </summary>
public sealed class TestDatabase
{
    public const string Variable = "MSSQL_TEST_CONNECTION";
    public const string ReadOnlyUser = "lakeflow_ro";
    public const string CredentialName = "test-source";

    private static readonly Lazy<Task<TestDatabase>> Shared = new(CreateAsync);

    public static string? SkipReason => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 }
        ? null
        : $"Set {Variable} to run SQL Server tests (scripts/test-sqlserver.sh starts one in Docker).";

    public static Task<TestDatabase> GetAsync() => Shared.Value;

    public required string AdminConnectionString { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string ReadOnlyPassword { get; init; }

    private static async Task<TestDatabase> CreateAsync()
    {
        var password = "Ro1!" + RandomNumberGenerator.GetHexString(20);
        await using (var connection = new SqlConnection(Environment.GetEnvironmentVariable(Variable)))
        {
            await connection.OpenAsync();
            await ExecAsync(connection,
                $"IF SUSER_ID('{ReadOnlyUser}') IS NULL CREATE LOGIN [{ReadOnlyUser}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF " +
                $"ELSE ALTER LOGIN [{ReadOnlyUser}] WITH PASSWORD = '{password}'");
        }
        return await CreateDatabaseAsync(password);
    }

    /// <summary>
    /// A database of its own, for tests that would disturb others (an open transaction holds back
    /// MIN_ACTIVE_ROWVERSION() for the whole database).
    /// </summary>
    public static async Task<TestDatabase> CreateIsolatedAsync() => await CreateDatabaseAsync((await GetAsync()).ReadOnlyPassword);

    private static async Task<TestDatabase> CreateDatabaseAsync(string password)
    {
        var admin = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Variable));
        var name = $"lakeflow_test_{DateTime.UtcNow:yyyyMMddHHmmss}_{RandomNumberGenerator.GetHexString(4).ToLowerInvariant()}";
        await using (var connection = new SqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecAsync(connection, $"CREATE DATABASE [{name}]");
            await ExecAsync(connection, $"ALTER DATABASE [{name}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = ON)");
        }
        admin.InitialCatalog = name;
        await using (var connection = new SqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await ExecAsync(connection, $"CREATE USER [{ReadOnlyUser}] FOR LOGIN [{ReadOnlyUser}]");
            await ExecAsync(connection, $"GRANT SELECT, VIEW CHANGE TRACKING ON SCHEMA::dbo TO [{ReadOnlyUser}]");
        }
        var dataSource = admin.DataSource.Replace("tcp:", "");
        var comma = dataSource.LastIndexOf(',');
        return new TestDatabase
        {
            AdminConnectionString = admin.ConnectionString,
            Name = name,
            Host = comma < 0 ? dataSource : dataSource[..comma],
            Port = comma < 0 ? 1433 : int.Parse(dataSource[(comma + 1)..]),
            ReadOnlyPassword = password,
        };
    }

    public SourceConfig Source(IReadOnlyList<TableConfig>? tables = null, string? credentialName = CredentialName) => new()
    {
        SourceId = "test",
        SourceSystem = "test",
        Division = Division.Grain,
        Engine = SourceEngine.SqlServer,
        Connection = new ConnectionSettings
        {
            Host = Host,
            Port = Port,
            Database = Name,
            UserName = ReadOnlyUser,
            CredentialName = credentialName,
            TrustServerCertificate = true,
        },
        Tables = tables ?? [],
    };

    public SqlServerConnector Connector(string? password = null) =>
        new(Source(), new InMemoryCredentialStore(new Dictionary<string, string> { [CredentialName] = password ?? ReadOnlyPassword }));

    public async Task ExecAsync(string sql)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await ExecAsync(connection, sql);
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new SqlConnection(AdminConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
