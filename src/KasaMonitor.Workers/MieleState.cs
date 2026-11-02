using KasaMonitor.Core.Miele;

namespace KasaMonitor.Workers;

/// <summary>What the UI and /metrics show about one appliance.</summary>
public sealed record MieleApplianceLive(
    MieleDevice Device,
    MieleStateValues? Values,
    DateTimeOffset? StateUtc,
    MieleCycle? OpenCycle,
    MieleCycle? LastCycle,
    int CyclesTotal);

/// <summary>How the appliances are read right now.</summary>
public enum MieleStreamMode
{
    /// <summary>Nothing is read (not configured, not connected, waiting).</summary>
    Off,
    /// <summary>SSE stream open.</summary>
    Sse,
    /// <summary>SSE failed 3 times in a row: GET /v1/devices every 60 s until it works again.</summary>
    Polling,
}

/// <summary>
/// In-memory state of the Miele integration (singleton): the appliances, the stream (open, mode, last event, last
/// error), a status text when nothing is read, and a short timeline of status/phase per appliance (6 hours) for the
/// NILM ground truth (<see cref="MieleGroundTruth"/>).
/// </summary>
public sealed class MieleState
{
    public static readonly TimeSpan TimelineSpan = TimeSpan.FromHours(6);
    private const int MaxTimelinePoints = 2000;

    private readonly object _sync = new();
    private readonly Dictionary<string, MieleApplianceLive> _appliances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<MieleStatusPoint>> _timelines = new(StringComparer.Ordinal);
    private string? _status;

    public event Action? Changed;

    /// <summary>Not archived appliances, by alias.</summary>
    public IReadOnlyList<MieleApplianceLive> Appliances
    {
        get
        {
            lock (_sync)
                return _appliances.Values.Where(a => !a.Device.IsArchived)
                    .OrderBy(a => a.Device.Alias, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Device.Id, StringComparer.Ordinal).ToList();
        }
    }

    public MieleApplianceLive? Get(string deviceId)
    {
        lock (_sync) return _appliances.GetValueOrDefault(deviceId);
    }

    public bool StreamConnected { get; private set; }
    public MieleStreamMode Mode { get; private set; }
    public DateTimeOffset? ConnectedSinceUtc { get; private set; }
    public DateTimeOffset? LastEventUtc { get; private set; }
    public string? LastEventName { get; private set; }
    public DateTimeOffset? LastReadUtc { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorUtc { get; private set; }

    /// <summary>Why nothing is read right now (null while reading normally).</summary>
    public string? Status { get { lock (_sync) return _status; } }

    /// <summary>The last finished read of the program history (<c>lastUsedPrograms</c>).</summary>
    public DateTimeOffset? HistorySyncUtc { get; private set; }
    /// <summary>What went wrong at the last history read (consent missing, limit reached …), null when it worked.</summary>
    public string? HistoryMessage { get; private set; }

    public void SetHistory(DateTimeOffset? lastSyncUtc, string? message)
    {
        HistorySyncUtc = lastSyncUtc;
        HistoryMessage = message;
        Raise();
    }

    public void Publish(MieleApplianceLive live)
    {
        lock (_sync) _appliances[live.Device.Id] = live;
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

    public void SetStream(bool connected, MieleStreamMode mode, DateTimeOffset nowUtc)
    {
        if (StreamConnected == connected && Mode == mode) return;
        if (connected && !StreamConnected) ConnectedSinceUtc = nowUtc;
        if (!connected) ConnectedSinceUtc = null;
        StreamConnected = connected;
        Mode = mode;
        Raise();
    }

    /// <summary>An SSE event arrived (any kind, pings included).</summary>
    public void MarkEvent(string name, DateTimeOffset nowUtc)
    {
        LastEventUtc = nowUtc;
        LastEventName = name;
    }

    /// <summary>The appliances were read (SSE devices event or GET /v1/devices).</summary>
    public void MarkRead(DateTimeOffset nowUtc) => LastReadUtc = nowUtc;

    public void SetError(string? error, DateTimeOffset nowUtc)
    {
        LastError = error;
        LastErrorUtc = error is null ? null : nowUtc;
        Raise();
    }

    /// <summary>Appends a point when status or phase changed (and trims to <see cref="TimelineSpan"/>).</summary>
    public void RecordStatus(string deviceId, DateTimeOffset tsUtc, int? statusRaw, string? phaseText)
    {
        lock (_sync)
        {
            if (!_timelines.TryGetValue(deviceId, out var list)) _timelines[deviceId] = list = new List<MieleStatusPoint>();
            if (list.Count > 0 && list[^1].StatusRaw == statusRaw && list[^1].PhaseText == phaseText) return;
            if (list.Count > 0 && tsUtc < list[^1].FromUtc) return;
            list.Add(new MieleStatusPoint(tsUtc, statusRaw, phaseText));
            // Keep the newest point older than the span: it tells the state at the start of the span.
            while (list.Count > 1 && (list[1].FromUtc < tsUtc - TimelineSpan || list.Count > MaxTimelinePoints)) list.RemoveAt(0);
        }
    }

    /// <summary>The appliances fed by a plug (not archived), with their timelines, for the NILM ground truth.</summary>
    public IReadOnlyList<MieleLinkedAppliance> Linked()
    {
        lock (_sync)
            return _appliances.Values.Where(a => a.Device is { PlugId: not null, IsArchived: false })
                .Select(a => new MieleLinkedAppliance(a.Device.PlugId!.Value, a.Device.Alias,
                    _timelines.TryGetValue(a.Device.Id, out var t) ? t.ToList() : new List<MieleStatusPoint>()))
                .ToList();
    }

    /// <summary>Online = the appliance is connected to Miele (status ≠ 255) and the stream (or polling) delivered within 3 minutes.</summary>
    public bool IsOnline(MieleApplianceLive a, DateTimeOffset nowUtc) =>
        a.Values is { } v && v.StatusRaw is not null && v.StatusRaw != MieleTexts.StatusNotConnected
        && (StreamConnected || LastReadUtc is { } r && nowUtc - r <= TimeSpan.FromMinutes(3));

    public void Raise()
    {
        try { Changed?.Invoke(); } catch { /* a UI handler must never break the worker */ }
    }
}

/// <summary>The NILM ground truth from the Miele appliances (see <see cref="MieleGroundTruthRule"/>).</summary>
public sealed class MieleGroundTruth : KasaMonitor.Core.Nilm.INilmGroundTruth
{
    private readonly MieleState _state;

    public MieleGroundTruth(MieleState state) => _state = state;

    public string? LabelFor(Guid plugId, DateTimeOffset tsUtc) => MieleGroundTruthRule.Label(plugId, tsUtc, _state.Linked());
}
