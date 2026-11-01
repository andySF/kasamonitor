using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Metering;

/// <summary>
/// Builds one <see cref="LiveMinuteAggregate"/> per plug and completed UTC minute from every live sample received
/// (weighted by <see cref="LiveSample.Seconds"/>). A minute is complete as soon as a sample of a later minute arrives
/// (<see cref="Add"/>) or the clock has moved past it (<see cref="TakeCompleted"/>). A sample that arrives for a minute
/// already closed opens a small partial aggregate of that minute; the repository merges partial aggregates of the
/// same minute (<see cref="LiveMinuteAggregate.Merge"/>), so nothing is overwritten. Not thread-safe.
/// </summary>
public sealed class LiveMinuteAggregator
{
    private sealed class Bucket
    {
        public DateTimeOffset Minute;
        public double Min = double.MaxValue, Max = double.MinValue, WattSeconds, Seconds;
        public double VoltageSum;
        public int VoltageCount, Count;
    }

    private readonly Dictionary<Guid, Bucket> _open = new();

    public static DateTimeOffset MinuteOf(DateTimeOffset ts)
    {
        var utc = ts.ToUniversalTime();
        return new DateTimeOffset(utc.UtcTicks - utc.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    }

    /// <summary>Adds a sample; returns the aggregate of the bucket it closes (the plug's other minute), if any.</summary>
    public LiveMinuteAggregate? Add(LiveSample s)
    {
        var minute = MinuteOf(s.TsUtc);
        LiveMinuteAggregate? completed = null;
        if (_open.TryGetValue(s.PlugId, out var b) && b.Minute != minute)
        {
            // A later minute closes the open one; an earlier (late) sample also closes it and gets its own partial bucket.
            completed = Build(s.PlugId, b);
            b = null;
        }
        if (b is null)
        {
            b = new Bucket { Minute = minute };
            _open[s.PlugId] = b;
        }
        var seconds = s.Seconds > 0 ? s.Seconds : 1;
        b.Count++;
        b.Seconds += seconds;
        b.WattSeconds += s.PowerW * seconds;
        if (s.PowerW < b.Min) b.Min = s.PowerW;
        if (s.PowerW > b.Max) b.Max = s.PowerW;
        if (s.VoltageV is { } v) { b.VoltageSum += v; b.VoltageCount++; }
        return completed;
    }

    /// <summary>Closes every open minute that ended before <paramref name="nowUtc"/>'s minute (a channel that stopped answering).</summary>
    public IReadOnlyList<LiveMinuteAggregate> TakeCompleted(DateTimeOffset nowUtc)
    {
        var current = MinuteOf(nowUtc);
        var done = new List<LiveMinuteAggregate>();
        foreach (var (plugId, b) in _open.Where(kv => kv.Value.Minute < current).ToList())
        {
            done.Add(Build(plugId, b));
            _open.Remove(plugId);
        }
        return done;
    }

    /// <summary>Everything still open (shutdown): partial minutes are stored (and merged later) rather than lost.</summary>
    public IReadOnlyList<LiveMinuteAggregate> TakeAll()
    {
        var all = _open.Select(kv => Build(kv.Key, kv.Value)).ToList();
        _open.Clear();
        return all;
    }

    private static LiveMinuteAggregate Build(Guid plugId, Bucket b) => new(
        plugId, b.Minute, b.Min, b.WattSeconds / b.Seconds, b.Max,
        b.VoltageCount > 0 ? b.VoltageSum / b.VoltageCount : null,
        b.WattSeconds / 3600.0, b.Count, b.Seconds);
}
