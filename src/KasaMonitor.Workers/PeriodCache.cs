using KasaMonitor.Core.Home;
using KasaMonitor.Core.Reports;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// The cache of period computations shared by every circuit (the home page, /history): each value is computed once on the
/// thread pool (never on a circuit's thread: SQLite runs the "async" queries synchronously) and concurrent callers share
/// the computation in progress. A value is fresh for <see cref="Ttl"/> (a period holding today: 1 minute for a day, 10
/// minutes otherwise; a past period until the local day changes); an expired value is still served while one
/// recomputation runs (stale-while-revalidate). At most <c>maxEntries</c> values are kept (the oldest go first); the values
/// of another local day are dropped.
/// </summary>
public sealed class PeriodCache<TKey, TValue> where TKey : notnull
{
    /// <summary>One cached value: when its computation started, the local day it belongs to, the value, a refresh in progress.</summary>
    private sealed class Entry
    {
        public required DateTimeOffset At { get; init; }
        public required DateOnly Day { get; init; }
        public required Task<TValue> Task { get; init; }
        /// <summary>The refresh in progress, including its swap into the cache.</summary>
        public System.Threading.Tasks.Task? Refresh { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<TKey, Entry> _entries = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeZoneInfo> _tz;
    private readonly int _maxEntries;
    private readonly ILogger _log;
    private readonly string _what;

    public PeriodCache(Func<DateTimeOffset> clock, Func<TimeZoneInfo> timeZone, int maxEntries, ILogger log, string what)
    {
        _clock = clock;
        _tz = timeZone;
        _maxEntries = maxEntries;
        _log = log;
        _what = what;
    }

    public static readonly TimeSpan CurrentDayFor = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan InProgressFor = TimeSpan.FromMinutes(10);

    /// <summary>How long a computed period is kept: current day 1 min, other period in progress 10 min, past: until the day changes.</summary>
    public static TimeSpan Ttl(ReportPeriod period, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var today = LocalClock.Day(nowUtc, tz);
        if (!period.Contains(today)) return TimeSpan.MaxValue;
        return period.Kind == ReportGranularity.Day ? CurrentDayFor : InProgressFor;
    }

    /// <summary>
    /// The value of a key: the cached one while fresh (<see cref="Ttl"/> of <paramref name="period"/>, same local day); an
    /// expired one while a single refresh runs in the background; otherwise one computation shared by every caller.
    /// <paramref name="compute"/> gets the time the computation started.
    /// </summary>
    public Task<TValue> GetAsync(TKey key, ReportPeriod period, Func<DateTimeOffset, Task<TValue>> compute, CancellationToken ct)
    {
        Task<TValue> result;
        lock (_lock)
        {
            var now = _clock();
            var tz = _tz();
            var today = LocalClock.Day(now, tz);
            if (_entries.TryGetValue(key, out var e) && e.Day == today && !e.Task.IsFaulted && !e.Task.IsCanceled)
            {
                if (now - e.At >= Ttl(period, now, tz) && e.Task.IsCompletedSuccessfully && e.Refresh is null)
                {
                    var refresh = Start(compute, now);
                    e.Refresh = refresh.ContinueWith(t => Refreshed(key, e, t, now, today), TaskScheduler.Default);
                }
                result = e.Task;
            }
            else
            {
                result = Start(compute, now);
                _entries[key] = new Entry { At = now, Day = today, Task = result };
                Evict(today);
            }
        }
        return ct.CanBeCanceled ? result.WaitAsync(ct) : result;
    }

    /// <summary>The refresh in progress of a key (completed when none): tests and diagnostics.</summary>
    public Task RefreshingAsync(TKey key)
    {
        lock (_lock)
            return _entries.TryGetValue(key, out var e) && e.Refresh is { } r ? r : Task.CompletedTask;
    }

    private static Task<TValue> Start(Func<DateTimeOffset, Task<TValue>> compute, DateTimeOffset now) => Task.Run(() => compute(now));

    private void Refreshed(TKey key, Entry old, Task<TValue> refresh, DateTimeOffset startedAt, DateOnly day)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var cur) || !ReferenceEquals(cur, old)) return;
            if (refresh.IsCompletedSuccessfully) _entries[key] = new Entry { At = startedAt, Day = day, Task = refresh };
            else
            {
                old.Refresh = null;   // the old value stays; the next call tries again
                _log.LogWarning(refresh.Exception?.GetBaseException(), "{What} of {Key} could not be refreshed", _what, key);
            }
        }
    }

    /// <summary>Under the lock: drops the values of another day, then the oldest beyond the maximum.</summary>
    private void Evict(DateOnly today)
    {
        foreach (var key in _entries.Where(kv => kv.Value.Day != today).Select(kv => kv.Key).ToList()) _entries.Remove(key);
        foreach (var key in _entries.OrderBy(kv => kv.Value.At).Take(Math.Max(0, _entries.Count - _maxEntries)).Select(kv => kv.Key).ToList())
            _entries.Remove(key);
    }
}
