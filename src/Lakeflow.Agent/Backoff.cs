namespace Lakeflow.Agent;

/// <summary>Exponential back-off with ±20% jitter, so a fleet of agents does not retry in step.</summary>
public static class Backoff
{
    public static TimeSpan Delay(int failures, TimeSpan initial, TimeSpan max, Random? random = null)
    {
        if (failures <= 0) return TimeSpan.Zero;
        var exponent = Math.Min(failures - 1, 30);
        var ticks = Math.Min(initial.Ticks * Math.Pow(2, exponent), max.Ticks);
        var jitter = 0.8 + 0.4 * (random ?? Random.Shared).NextDouble();
        return TimeSpan.FromTicks((long)Math.Min(ticks * jitter, max.Ticks));
    }
}
