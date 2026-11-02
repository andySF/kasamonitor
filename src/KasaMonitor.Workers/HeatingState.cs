using KasaMonitor.Core.Heating;

namespace KasaMonitor.Workers;

/// <summary>What the UI and /metrics show about the boiler: device, last snapshot, cumulative totals and the poll state.</summary>
public sealed record HeatingLive(
    HeatingDevice Device,
    HeatingSnapshot? Snapshot,
    HeatingEnergyTotals? Totals,
    DateTimeOffset? LastSuccessUtc,
    string? LastError,
    bool LastPollOk,
    IReadOnlyList<HeatingEnergyPeriod>? Periods = null);

/// <summary>
/// In-memory state of the heating integration (singleton): the latest <see cref="HeatingLive"/>, the call budget and a
/// short status text when nothing is polled (disabled, no Client ID, disconnected, paused by 429).
/// </summary>
public sealed class HeatingState
{
    private readonly object _sync = new();
    private HeatingLive? _current;
    private string? _status;

    public HeatingState(ApiCallBudget? budget = null, DailyCallCounter? calls = null)
    {
        Budget = budget ?? new ApiCallBudget();
        Calls = calls ?? new DailyCallCounter();
    }

    /// <summary>Sliding-window quota of the API calls (1450 / 24 h, 120 / 10 min).</summary>
    public ApiCallBudget Budget { get; }

    /// <summary>HTTP calls to Viessmann (API and token endpoint) actually made today, local calendar day.</summary>
    public DailyCallCounter Calls { get; }

    /// <summary>Current poll interval (set by the worker), used for "online" (last success within three intervals).</summary>
    public int PollIntervalSeconds { get; set; } = KasaMonitor.Core.Heating.ViessmannApi.DefaultPollIntervalSeconds;

    /// <summary>
    /// The zone of the boiler's day (setting <c>BoilerTimeZone</c>, set by the worker at every cycle): the boiler's gas,
    /// electricity and heat are attributed to days in this zone (reports, the 30-day chart, <c>_total</c>).
    /// </summary>
    public TimeZoneInfo BoilerTimeZone { get; set; } = BoilerClock.DefaultTimeZone;

    /// <summary>Today in the boiler's zone.</summary>
    public DateOnly BoilerToday(DateTimeOffset nowUtc) => BoilerClock.DayOf(nowUtc, BoilerTimeZone);

    public event Action? Changed;

    public HeatingLive? Current { get { lock (_sync) return _current; } }

    /// <summary>Why nothing is polled right now (null while polling normally).</summary>
    public string? Status { get { lock (_sync) return _status; } }

    public void Publish(HeatingLive live)
    {
        lock (_sync) _current = live;
        Raise();
    }

    public void SetStatus(string? status)
    {
        lock (_sync)
        {
            if (_status == status) return;
            _status = status;
        }
        Raise();
    }

    /// <summary>Online = the last poll succeeded and is not older than three intervals.</summary>
    public bool IsOnline(DateTimeOffset now)
    {
        var c = Current;
        return c is { LastPollOk: true, LastSuccessUtc: { } ok } && now - ok <= TimeSpan.FromSeconds(3 * Math.Max(PollIntervalSeconds, 1));
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { /* a UI handler must never break the worker */ }
    }
}
