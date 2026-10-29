using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;

namespace Lakeflow.ControlApi.Services;

/// <summary>The latest nightly reconciliation result per site, source and table.</summary>
public interface IReconciliationSource
{
    Task<IReadOnlyList<ReconciliationResult>> LatestAsync(string? siteId, CancellationToken ct);
}

/// <summary>
/// The Fabric SQL endpoint as set up in the console (Configuration, then Fabric): the silver lakehouse's SQL analytics
/// endpoint, signed in as a service principal that has read access to it. Not set up: nothing to report.
/// </summary>
public sealed class SettingsReconciliation(SettingsStore settings) : IReconciliationSource
{
    public static string ConnectionString(FabricSetting fabric, string secret) => new SqlConnectionStringBuilder
    {
        DataSource = fabric.Server,
        InitialCatalog = fabric.Database,
        Authentication = SqlAuthenticationMethod.ActiveDirectoryServicePrincipal,
        UserID = fabric.ClientId,
        Password = secret,
        Encrypt = SqlConnectionEncryptOption.Mandatory,
        ConnectTimeout = 15,
    }.ConnectionString;

    public async Task<IReadOnlyList<ReconciliationResult>> LatestAsync(string? siteId, CancellationToken ct)
    {
        var (fabric, secret) = await settings.GetWithSecretAsync<FabricSetting>(SettingsStore.Fabric, ct);
        if (fabric is null || secret is null) return [];
        return await new SqlEndpointReconciliation(ConnectionString(fabric, System.Text.Encoding.UTF8.GetString(secret))).LatestAsync(siteId, ct);
    }
}

/// <summary>Reads <c>lakeflow_reconciliation</c>, which the bronze-to-silver notebook appends to, over the SQL endpoint.</summary>
public sealed class SqlEndpointReconciliation(string connectionString) : IReconciliationSource
{
    private const string Sql = """
        SELECT TOP (5000) checked_at, site_id, source_id, source_system, table_name, silver_table, as_of_seq,
               source_rows, silver_rows, source_checksum, silver_checksum, row_diff, mismatch_pct, status
        FROM (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY site_id, source_id, table_name ORDER BY checked_at DESC) AS rn
            FROM dbo.lakeflow_reconciliation
            WHERE checked_at >= DATEADD(day, -3, SYSUTCDATETIME()) AND (@site IS NULL OR site_id = @site)
        ) AS latest
        WHERE rn = 1
        ORDER BY site_id, source_id, table_name
        """;

    public async Task<IReadOnlyList<ReconciliationResult>> LatestAsync(string? siteId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add(new SqlParameter("@site", System.Data.SqlDbType.NVarChar, 64) { Value = (object?)siteId ?? DBNull.Value });
        var results = new List<ReconciliationResult>();
        SqlDataReader reader;
        try
        {
            reader = await command.ExecuteReaderAsync(ct);
        }
        catch (SqlException ex) when (ex.Number == 208)
        {
            // Invalid object name: the notebook has not written its first results yet (the first nightly run).
            return results;
        }
        await using var _ = reader;
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ReconciliationResult
            {
                CheckedUtc = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc)),
                SiteId = reader.GetString(1),
                SourceId = reader.GetString(2),
                SourceSystem = reader.GetString(3),
                Table = reader.GetString(4),
                SilverTable = reader.GetString(5),
                AsOfSeq = reader.GetInt64(6),
                SourceRows = reader.GetInt64(7),
                SilverRows = reader.GetInt64(8),
                SourceChecksum = reader.IsDBNull(9) ? null : reader.GetString(9),
                SilverChecksum = reader.IsDBNull(10) ? null : reader.GetString(10),
                RowDiff = reader.GetInt64(11),
                MismatchPercent = reader.GetDouble(12),
                Status = reader.GetString(13),
            });
        }
        return results;
    }
}
