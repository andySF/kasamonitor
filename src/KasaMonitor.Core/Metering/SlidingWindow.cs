namespace KasaMonitor.Core.Metering;

/// <summary>
/// Time-ordered buffer of the last <see cref="Span"/> (and at most <see cref="Capacity"/> points): the live chart of
/// /contor (10 minutes at 1 s = 600 points) and the per-plug history kept by the meter stream broadcaster.
/// Points older than the newest timestamp minus the span are dropped; a point older than the newest one is ignored.
/// Thread-safe.
/// </summary>
public sealed class SlidingWindow<T>
{
    private readonly Queue<(DateTimeOffset Ts, T Value)> _items = new();
    private readonly object _sync = new();
    private DateTimeOffset? _newest;

    public SlidingWindow(TimeSpan span, int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        Span = span;
        Capacity = capacity;
    }

    public TimeSpan Span { get; }
    public int Capacity { get; }

    public int Count { get { lock (_sync) return _items.Count; } }

    /// <summary>Adds a point; returns false when it is older than the newest point (out of order).</summary>
    public bool Add(DateTimeOffset ts, T value)
    {
        lock (_sync)
        {
            if (_newest is { } newest && ts < newest) return false;
            _newest = ts;
            _items.Enqueue((ts, value));
            var cutoff = ts - Span;
            while (_items.Count > 0 && (_items.Count > Capacity || _items.Peek().Ts <= cutoff))
                _items.Dequeue();
            return true;
        }
    }

    public IReadOnlyList<(DateTimeOffset Ts, T Value)> Snapshot()
    {
        lock (_sync) return _items.ToList();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _items.Clear();
            _newest = null;
        }
    }
}
