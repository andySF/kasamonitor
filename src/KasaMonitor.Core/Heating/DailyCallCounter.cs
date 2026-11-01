namespace KasaMonitor.Core.Heating;

/// <summary>
/// HTTP calls to Viessmann (API and token endpoint) actually made during the current local calendar day; resets at
/// local midnight (the container's TZ, Europe/Bucharest on the QNAP). In memory: a restart starts from zero.
/// </summary>
public sealed class DailyCallCounter
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _tz;
    private readonly object _sync = new();
    private DateOnly _day;
    private int _count;

    public DailyCallCounter(Func<DateTimeOffset>? clock = null, TimeZoneInfo? tz = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tz = tz ?? TimeZoneInfo.Local;
    }

    /// <summary>Called right before an HTTP request leaves.</summary>
    public void Record()
    {
        lock (_sync)
        {
            var today = HeatingEnergy.DayOf(_clock(), _tz);
            if (today != _day) { _day = today; _count = 0; }
            _count++;
        }
    }

    public int Today
    {
        get { lock (_sync) return HeatingEnergy.DayOf(_clock(), _tz) == _day ? _count : 0; }
    }
}
