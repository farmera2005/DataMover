using Lakeflow.Contracts;
using Microsoft.Data.SqlClient;

namespace Lakeflow.ControlApi.Services;

/// <summary>The latest nightly reconciliation result per site, source and table.</summary>
public interface IReconciliationSource
{
    Task<IReadOnlyList<ReconciliationResult>> LatestAsync(string? siteId, CancellationToken ct);
}

public sealed class FabricOptions
{
    /// <summary>
    /// The silver lakehouse's SQL analytics endpoint, signed in with the API's managed identity, e.g.
    /// <c>Server=xxxx.datawarehouse.fabric.microsoft.com;Database=silver;Authentication=Active Directory Default</c>.
    /// Grant the identity read access to the silver lakehouse.
    /// </summary>
    public string? SqlEndpointConnectionString { get; set; }
}

/// <summary>Reads <c>lakeflow_reconciliation</c>, which the bronze-to-silver notebook appends to, over the SQL endpoint.</summary>
public sealed class SqlEndpointReconciliation(FabricOptions options) : IReconciliationSource
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
        await using var connection = new SqlConnection(options.SqlEndpointConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new SqlCommand(Sql, connection) { CommandTimeout = 30 };
        command.Parameters.Add(new SqlParameter("@site", System.Data.SqlDbType.NVarChar, 64) { Value = (object?)siteId ?? DBNull.Value });
        var results = new List<ReconciliationResult>();
        await using var reader = await command.ExecuteReaderAsync(ct);
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

/// <summary>Until the Fabric SQL endpoint is configured there is nothing to report.</summary>
public sealed class NoReconciliation : IReconciliationSource
{
    public Task<IReadOnlyList<ReconciliationResult>> LatestAsync(string? siteId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ReconciliationResult>>([]);
}
