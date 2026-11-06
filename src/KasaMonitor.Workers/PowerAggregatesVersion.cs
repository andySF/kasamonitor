namespace KasaMonitor.Workers;

/// <summary>
/// The version of the stored power aggregates (0.14.0): <see cref="RollupWorker"/> bumps it when it wrote rows of a period
/// that is no longer recent (the catch-up after a stop, the one-time backfill). It is part of the cache key of
/// <see cref="PowerHistoryService"/>, so a chart read before those rows existed is not served until the day changes.
/// </summary>
public sealed class PowerAggregatesVersion
{
    private int _value;

    public int Value => Volatile.Read(ref _value);

    public void Bump() => Interlocked.Increment(ref _value);
}
