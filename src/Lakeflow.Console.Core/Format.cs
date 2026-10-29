using Lakeflow.Contracts;

namespace Lakeflow.Console.Core;

/// <summary>How numbers read in the console.</summary>
public static class Format
{
    public static string Lag(double? seconds) => seconds switch
    {
        null => "—",
        < 90 => $"{seconds:F0} s",
        < 5400 => $"{seconds / 60:F0} min",
        < 172800 => $"{seconds / 3600:F1} h",
        _ => $"{seconds / 86400:F1} days",
    };

    public static string Bytes(long? bytes) => bytes switch
    {
        null => "—",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    public static string Ago(DateTimeOffset? at, DateTimeOffset now) => at is null ? "never" : $"{Lag((now - at.Value).TotalSeconds)} ago";

    public static string Health(Contracts.Health health) => health switch
    {
        Contracts.Health.Green => "Healthy",
        Contracts.Health.Amber => "Warning",
        _ => "Needs attention",
    };

    public static string Method(CaptureMethod? method) => method switch
    {
        CaptureMethod.ChangeTracking => "Change Tracking",
        CaptureMethod.RowVersion => "Rowversion polling",
        CaptureMethod.TimestampPolling => "Timestamp polling",
        CaptureMethod.Cdc => "CDC",
        null => "Not capturable yet",
        { } other => other.ToString(),
    };
}
