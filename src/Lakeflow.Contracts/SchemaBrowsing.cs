namespace Lakeflow.Contracts;

/// <summary>What a connection test found; sent back as the details of a <see cref="CommandType.TestConnection"/> result.</summary>
public sealed record ConnectionTestResult
{
    public required bool Succeeded { get; init; }
    public string? Error { get; init; }
    public string? ServerVersion { get; init; }
    public string? Database { get; init; }
    public string? Login { get; init; }
    /// <summary>Change Tracking is enabled on the database (it may still be off for individual tables).</summary>
    public bool ChangeTrackingEnabled { get; init; }
    public double ElapsedMilliseconds { get; init; }
}

/// <summary>A table the login can read, and how it could be captured.</summary>
public sealed record SourceTableInfo
{
    public required string Table { get; init; }
    public long RowCount { get; init; }
    public IReadOnlyList<string> KeyColumns { get; init; } = [];
    public bool ChangeTrackingEnabled { get; init; }
    public string? RowVersionColumn { get; init; }
    /// <summary>Date/time columns that lead an index, usable for timestamp polling without scanning.</summary>
    public IReadOnlyList<string> WatermarkCandidates { get; init; } = [];
    /// <summary>The best method available without changing the source; null when none (no key, or nothing to poll by).</summary>
    public CaptureMethod? SuggestedMethod { get; init; }
    public string? SuggestedWatermark { get; init; }
    /// <summary>Why the suggestion is what it is, for the wizard to show.</summary>
    public required string Note { get; init; }

    /// <summary>
    /// Preference: Change Tracking when the owner already enabled it (needs a primary key), else rowversion polling,
    /// else timestamp polling on an indexed date/time column. Never anything that changes the source.
    /// </summary>
    public static (CaptureMethod? Method, string? Watermark, string Note) Suggest(
        bool hasKey, bool changeTracking, string? rowVersion, IReadOnlyList<string> watermarks)
    {
        if (!hasKey) return (null, null, "No primary key: choose key columns before it can be captured.");
        if (changeTracking) return (CaptureMethod.ChangeTracking, null, "Change Tracking is enabled on this table.");
        if (rowVersion is not null) return (CaptureMethod.RowVersion, rowVersion, $"Polls the rowversion column {rowVersion}; deletes found nightly.");
        if (watermarks.Count > 0) return (CaptureMethod.TimestampPolling, watermarks[0], $"Polls {watermarks[0]} with a look-back; deletes found nightly.");
        return (null, null, "No Change Tracking, rowversion or indexed date/time column: polling would scan the table.");
    }
}

/// <summary>Details of a <see cref="CommandType.BrowseSchema"/> result.</summary>
public sealed record SchemaBrowseResult(IReadOnlyList<SourceTableInfo> Tables, bool Truncated);

/// <summary>Optional connector ability used by the setup wizard. Bounded, read-only catalog queries only.</summary>
public interface ISchemaBrowser
{
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken);

    Task<SchemaBrowseResult> BrowseAsync(int maxTables, CancellationToken cancellationToken);
}
