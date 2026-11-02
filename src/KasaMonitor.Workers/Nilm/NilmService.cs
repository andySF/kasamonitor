using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers.Nilm;

/// <summary>A detected appliance running now (an open run, at most <see cref="ApplianceRunBuilder.MaxOpen"/> old).</summary>
public sealed record ActiveAppliance(long SignatureId, string Name, string Category, string Icon, SignatureLabelSource Source, double PowerW,
    DateTimeOffset SinceUtc);

/// <summary>Energy of one signature over a period.</summary>
public sealed record ApplianceEnergyRow(long SignatureId, string Name, string Category, string Icon, double Wh);

/// <summary>
/// What the cards ("Acum pornite", "Azi") show, refreshed every <see cref="NilmService.SummaryInterval"/>; only detected
/// appliances (not hidden, not measured by a plug). <see cref="UnidentifiedTodayWh"/> = house today − Σ plugs over the
/// covered time − Σ runs today of the detected appliances.
/// </summary>
public sealed record NilmSummary(DateTimeOffset AtUtc, bool Enabled, bool HasMainMeter, IReadOnlyList<ActiveAppliance> Active,
    IReadOnlyList<ApplianceEnergyRow> Today, double HouseTodayWh, double PlugsTodayWh, double UnidentifiedTodayWh)
{
    public static readonly NilmSummary Empty = new(DateTimeOffset.MinValue, false, false, Array.Empty<ActiveAppliance>(),
        Array.Empty<ApplianceEnergyRow>(), 0, 0, 0);
}

/// <summary>One exported appliance series (signature id, raw name, category, source, power now if running, cumulative Wh).</summary>
public sealed record NilmMetricItem(long SignatureId, string Name, string Category, SignatureLabelSource Source, double? PowerW, double EnergyWh);

/// <summary>What /metrics exports: eligible appliances (see <see cref="NilmService.IsExported"/>), events, unidentified energy.</summary>
public sealed record NilmMetricSnapshot(IReadOnlyList<NilmMetricItem> Appliances, long Events, double UnidentifiedWh)
{
    public static readonly NilmMetricSnapshot Empty = new(Array.Empty<NilmMetricItem>(), 0, 0);
}

/// <summary>
/// One signature on /consumatori, with its energy today, over 7 and over 30 days. <see cref="PossiblePlug"/>: a plug that
/// labelled some of its events but not enough to link it (fewer than 3, or under 60 %), shown as „posibil &lt;priză&gt;”.
/// </summary>
public sealed record SignatureRow(ApplianceSignature Signature, double TodayWh, double Last30DaysWh, double? ActiveW, string? PossiblePlug = null,
    int UncertainRuns = 0, double Last7DaysWh = 0);

/// <summary>One event of the last 24 h on /consumatori.</summary>
public sealed record EventRow(MeterEvent Event, string? SignatureName);

/// <summary>
/// A plug and whether it teaches the detection. <see cref="SingleAppliance"/>: „un singur aparat pe priză”, its events
/// labelled <see cref="ApplianceName"/> (the name in effect: the alias when none was given) and kept out of the training.
/// </summary>
public sealed record PlugNilmRow(Guid PlugId, string Alias, PlugNilmState State, bool SingleAppliance = false, string? ApplianceName = null,
    string? ApplianceCategory = null)
{
    public static PlugNilmRow Of(Plug p) => new(p.Id, p.Alias, p.NilmState, p.SingleAppliance, p.Appliance ?? p.ApplianceName, p.ApplianceCategory);
}

/// <param name="DetectedTotal">Every detected signature (not measured by a plug), listed or not.</param>
/// <param name="RareCount">Detected signatures left out of the default list (<see cref="SignatureListing.ShownByDefault"/>).</param>
/// <param name="CleanableCount">Signatures the cleaning would delete (<see cref="SignatureListing.IsRareAndOld"/>).</param>
public sealed record NilmOverview(IReadOnlyList<SignatureRow> Detected, int DetectedTotal, int Page, int PageCount,
    IReadOnlyList<SignatureRow> PlugMeasured, IReadOnlyList<ApplianceSignature> AllSignatures,
    IReadOnlyList<EventRow> Events, int EventsInWindow, int EventsPage, int EventsPageCount,
    NilmModelInfo? Model, int LabeledEvents, int LabeledClasses, NilmSummary Summary, string? MainMeterAlias, NilmSettings Settings,
    IReadOnlyList<PlugNilmRow> Plugs, bool Busy, int RareCount = 0, int CleanableCount = 0, bool ShowRare = false);

/// <summary>The „fișa” of one signature on /consumatori: its profile and the plug it matches (alias), linked or only „posibil”.</summary>
public sealed record SignatureProfileView(ApplianceSignature Signature, SignatureProfile Profile, string? PlugAlias, bool PlugLinked);

/// <summary>
/// One signature of the HTTP API's list: <see cref="PlugAlias"/> = the plug that measures it (<see cref="PlugLinked"/>)
/// or, for a detected one, the plug of its „posibil &lt;priză&gt;” badge.
/// </summary>
public sealed record SignatureListItem(ApplianceSignature Signature, string? PlugAlias, bool PlugLinked);

/// <summary>
/// What a re-clustering did: events re-clustered, of which given to an anchor (labelled signature) before DBSCAN, clusters
/// found, signatures left, events moved, signatures merged automatically (<see cref="Merges"/>: which, and why).
/// </summary>
public sealed record NilmRegroupResult(int Events, int Anchored, int Clusters, int Signatures, int Moved, int Merged,
    IReadOnlyList<MergeStep>? Merges = null, NilmTrainResult? Training = null);

/// <summary>
/// One training of the classifier: <see cref="Info"/> when it trained (classes, cross-validated accuracy); otherwise
/// the labelled events and classes found (not enough, or the model is off). <see cref="Duration"/> = wall time.
/// </summary>
public sealed record NilmTrainResult(bool Trained, int LabeledEvents, int LabeledClasses, NilmModelInfo? Info, TimeSpan Duration);

/// <summary>What „Adaugă la aparatul…” did: merged the events (close components) or only gave the name (distant ones).</summary>
public enum AddToOutcome
{
    Failed = 0,
    Merged = 1,
    Named = 2,
}

/// <summary>
/// „Acum am pornit X” (ground truth from the HTTP API): the name (and category) for the main meter's event nearest to
/// <see cref="AtUtc"/>; kept in memory until <see cref="ExpiresUtc"/> while that event is not stored yet.
/// </summary>
public sealed record NilmMark(string Name, string? Category, DateTimeOffset AtUtc, DateTimeOffset ExpiresUtc);

/// <summary>What a mark did: labelled a stored event, waits for the next one, or found none (a time in the past).</summary>
public enum MarkOutcome
{
    Matched = 1,
    Pending = 2,

    /// <summary>No such event, and the mark's time is too old for one still to come (<see cref="NilmService.MarkAsync"/>).</summary>
    NotFound = 3,
}

/// <summary>The result of <see cref="NilmService.MarkAsync"/>: <see cref="Event"/> = the labelled event (matched).</summary>
public sealed record NilmMarkResult(MarkOutcome Outcome, NilmMark Mark, MeterEvent? Event);

/// <summary>The NILM settings in effect.</summary>
public sealed record NilmSettings(bool Enabled, int MinEventDeltaW, int ClusterWindowDays, bool MlEnabled);

/// <summary>A UI action found the detection busy (backfill or nightly job) for longer than <see cref="NilmService.ActionWait"/>.</summary>
public sealed class NilmBusyException() : Exception("Procesare în curs (istoric sau regruparea de noapte); reîncearcă peste câteva secunde.");

/// <summary>
/// Appliance detection from the main meter (NILM). Feeds every 1-second sample of the main meter (live stream, plus the
/// stored <c>LiveSamples</c> not processed yet at start: the backfill) to the <see cref="MeterEventDetector"/>. Events are
/// processed in batches, <see cref="LabelDelay"/> after they happened (so the plugs' readings around them exist):
/// <list type="number">
/// <item>label from the plug whose stabilised level changed by the same ΔP (<see cref="PlugEventMatcher"/>, plug readings kept in memory for
/// the last minutes, or loaded once per backfill batch); a plug matching more than
/// <see cref="PlugNilmStates.MaxMatchesPerHour"/> events in an hour is excluded from learning automatically;</item>
/// <item>otherwise the classifier's label when it is sure (≥ 0.7) and the event is near that class (open set);</item>
/// <item>the signature: the predicted one for a model label; else the nearest anchor (labelled by the user or linked to a
/// plug) within 1.5 × eps; else the nearest other signature within eps; else a new "Necunoscut #n";</item>
/// <item>runs opened/closed (<see cref="ApplianceRunBuilder"/>), events and runs written in one transaction each.</item>
/// </list>
/// Every night at <see cref="NightlyAt"/> (local), at startup when the last re-clustering is older than
/// <see cref="ReclusterMaxAge"/>, and on „Regrupează acum”, the newest <see cref="SignatureClusterer.MaxEvents"/> events of
/// the last <c>ClusterWindowDays</c> are re-clustered (anchors first, then DBSCAN), signatures that are the same component
/// are merged (<see cref="SignatureMerger"/>) and the classifier retrained, the heavy parts on a low-priority thread
/// outside the processing lock. Events and runs are never deleted (0.9.2: unlimited, the history of /history; the raw live
/// samples they come from keep their own retention).
/// </summary>
public sealed class NilmService : BackgroundService
{
    /// <summary>
    /// How long an event waits before it is labelled: the plug window after the event (20 s) plus two 10-second polls, so the plug readings
    /// of the whole "after" window are there.
    /// </summary>
    public static readonly TimeSpan LabelDelay = TimeSpan.FromSeconds(45);

    /// <summary>A UI action waits at most this long for the processing (backfill, nightly job) before giving up.</summary>
    public static readonly TimeSpan ActionWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan SummaryInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MarkerInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan PlugsInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BackfillBatch = TimeSpan.FromHours(6);

    /// <summary>Plug readings kept in memory per plug for the live labelling.</summary>
    public static readonly TimeSpan PlugMemory = TimeSpan.FromMinutes(3);

    /// <summary>A backfill longer than this re-clusters right after it (instead of waiting for the night).</summary>
    public static readonly TimeSpan ReclusterAfterBackfill = TimeSpan.FromHours(6);

    public static readonly TimeOnly NightlyAt = new(3, 30);

    /// <summary>At startup the re-clustering runs right away when the last one is older than this (or never ran on stored events).</summary>
    public static readonly TimeSpan ReclusterMaxAge = TimeSpan.FromHours(24);
    public const int MaxQueuedSamples = 100_000;
    public const int PageSize = 50;

    /// <summary>A mark („acum am pornit X”) labels only an event of at least this |ΔP|…</summary>
    public const double MarkMinDeltaW = 100;

    /// <summary>…within this much of the mark's time (a stored event), or after the mark's time minus this (a pending mark)…</summary>
    public static readonly TimeSpan MarkWindow = TimeSpan.FromSeconds(90);

    /// <summary>…and a mark without such an event waits in memory this long for the detector.</summary>
    public static readonly TimeSpan MarkPendingFor = TimeSpan.FromMinutes(10);

    /// <summary>A detected appliance is exported to /metrics when the user named it or it has at least this many events in the window.</summary>
    public const int MinEventsForMetrics = 10;

    private readonly IServiceScopeFactory _scopes;
    private readonly IKasaSettingsService _settings;
    private readonly ILiveStreamBroadcaster _live;
    private readonly IPlugStateBroadcaster _states;
    private readonly ILogger<NilmService> _log;
    private readonly string _modelPath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _tz;
    private readonly INilmGroundTruth? _groundTruth;

    private readonly MeterEventDetector _detector = new();
    private readonly ConcurrentQueue<LiveSample> _queue = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _nightlyGate = new(1, 1);   // one re-clustering at a time (nightly, startup, button)
    private readonly List<MeterEvent> _pending = new();
    private readonly ConcurrentDictionary<long, double> _applianceWh = new();
    private readonly ConcurrentDictionary<Guid, List<PlugPowerPoint>> _plugMemory = new();
    private readonly ConcurrentDictionary<Guid, List<PlugPowerPoint>> _plugStream = new();   // plugs' live stream (≈ 3 s)
    private readonly HashSet<UsedPlugStep> _usedChanges = new();
    private readonly Dictionary<Guid, Queue<DateTimeOffset>> _plugMatches = new();
    private ApplianceRunBuilder _runs = new();
    private IReadOnlyList<ApplianceRun> _openSnapshot = Array.Empty<ApplianceRun>();
    private List<ApplianceSignature> _signatures = new();
    private IReadOnlyList<Plug> _plugs = Array.Empty<Plug>();
    private Guid? _mainId;
    private volatile bool _acceptSamples;
    private volatile bool _busy;
    private bool _backfilled;
    private bool _initialized;
    private bool _reclusterDue;
    private bool _trainDue;
    private readonly List<NilmMark> _marks = new();   // pending marks, consumed by the next batch (under the lock)
    private readonly List<MeterEvent> _recentLabelled = new();   // the last minute of labelled events (neighbours of the next batch)
    private NilmSettings _current = new(true, KasaSettingsDefaults.DefaultMinEventDeltaW, KasaSettingsDefaults.DefaultClusterWindowDays, true);
    private DateTimeOffset _lastMarker = DateTimeOffset.MinValue, _lastSummary = DateTimeOffset.MinValue, _lastPlugs = DateTimeOffset.MinValue;
    private DateTimeOffset _nextNightly;
    private long _events;
    private double _unidentifiedBase, _unidentifiedToday;
    private DateOnly _summaryDay;
    private int _queued;

    public NilmService(IServiceScopeFactory scopes, IKasaSettingsService settings, ILiveStreamBroadcaster live, IPlugStateBroadcaster states,
        ILogger<NilmService> log, string modelPath, Func<DateTimeOffset>? clock = null, TimeZoneInfo? tz = null,
        INilmGroundTruth? groundTruth = null)
    {
        _groundTruth = groundTruth;
        _scopes = scopes;
        _settings = settings;
        _live = live;
        _states = states;
        _log = log;
        _modelPath = modelPath;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tz = tz ?? TimeZoneInfo.Local;
        Classifier = new NilmClassifier(_tz);
        _live.SampleReceived += OnSample;
        _states.StateChanged += OnPlugState;
    }

    public NilmClassifier Classifier { get; }
    public NilmModelInfo? ModelInfo { get; private set; }
    public NilmSummary Summary { get; private set; } = NilmSummary.Empty;
    public NilmMetricSnapshot Metrics { get; private set; } = NilmMetricSnapshot.Empty;

    /// <summary>The main meter whose samples are processed (null without one).</summary>
    public Guid? MainMeterId => _mainId;

    /// <summary>Events detected but not yet stored (waiting <see cref="LabelDelay"/> for the plug readings).</summary>
    public int PendingEvents { get { lock (_pending) return _pending.Count; } }

    private void OnSample(LiveSample s)
    {
        if (!_acceptSamples) return;
        if (s.PlugId != _mainId)
        {
            if (_plugs.Any(p => p.Id == s.PlugId && p.Role == PlugRole.Plug)) Remember(_plugStream, s.PlugId, s.TsUtc, s.PowerW);
            return;
        }
        if (Interlocked.Increment(ref _queued) > MaxQueuedSamples)
        {
            Interlocked.Decrement(ref _queued);
            return;
        }
        _queue.Enqueue(s);
    }

    /// <summary>Keeps the last <see cref="PlugMemory"/> of every plug's 10-second readings for the live labelling.</summary>
    private void OnPlugState(PlugLiveState s)
    {
        if (s.Role != PlugRole.Plug || !s.IsOnline || s.LastReading is null) return;
        Remember(_plugMemory, s.PlugId, s.LastSampleUtc, s.LastReading.PowerMw / 1000.0);
    }

    /// <summary>Appends a reading to a plug's ring, keeping the last <see cref="PlugMemory"/>.</summary>
    private static void Remember(ConcurrentDictionary<Guid, List<PlugPowerPoint>> rings, Guid plugId, DateTimeOffset ts, double watts)
    {
        var ring = rings.GetOrAdd(plugId, _ => new List<PlugPowerPoint>());
        lock (ring)
        {
            if (ring.Count > 0 && ring[^1].TsUtc >= ts) return;
            ring.Add(new PlugPowerPoint(ts, watts));
            var cutoff = ts - PlugMemory;
            var old = ring.FindIndex(p => p.TsUtc >= cutoff);
            if (old > 0) ring.RemoveRange(0, old);
        }
    }

    private static List<PlugPowerPoint> Slice(ConcurrentDictionary<Guid, List<PlugPowerPoint>> rings, Guid plugId, DateTimeOffset from, DateTimeOffset to)
    {
        if (!rings.TryGetValue(plugId, out var ring)) return new List<PlugPowerPoint>();
        lock (ring) return ring.Where(p => p.TsUtc >= from && p.TsUtc <= to).ToList();
    }

    private DateTimeOffset LocalMidnightUtc(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, _tz);
        var midnight = local.Date;
        return new DateTimeOffset(midnight, _tz.GetUtcOffset(midnight)).ToUniversalTime();
    }

    private DateTimeOffset NextNightly(DateTimeOffset nowUtc)
    {
        var local = TimeZoneInfo.ConvertTime(nowUtc, _tz);
        var at = local.Date + NightlyAt.ToTimeSpan();
        if (at <= local.DateTime) at = at.AddDays(1);
        return new DateTimeOffset(at, _tz.GetUtcOffset(at)).ToUniversalTime();
    }

    private DateTimeOffset WindowStart(DateTimeOffset nowUtc) => nowUtc.AddDays(-_current.ClusterWindowDays);

    private IReadOnlyCollection<Guid> ExcludedPlugs => _plugs.Where(p => PlugNilmStates.IsIgnored(p.NilmState)).Select(p => p.Id).ToList();

    /// <summary>
    /// The plugs whose labels never train the classifier (<see cref="Plug.ExcludedFromTraining"/>): those excluded from
    /// learning and the single-appliance ones. Only the training uses this list; the plug rule and the profiles keep
    /// <see cref="ExcludedPlugs"/>, so a single-appliance plug still labels and links its events.
    /// </summary>
    public static IReadOnlyCollection<Guid> TrainingExcluded(IEnumerable<Plug> plugs) => plugs.Where(p => p.ExcludedFromTraining).Select(p => p.Id).ToList();

    /// <summary>The single-appliance plug with this id (<see cref="Plug.Appliance"/> set), or null.</summary>
    private Plug? SingleAppliancePlug(Guid? plugId) =>
        plugId is { } id ? _plugs.FirstOrDefault(p => p.Id == id && p.Appliance is not null) : null;

    /// <summary>A detected appliance (not hidden, not measured by a plug) that the user named or that has ≥ 10 events in the window.</summary>
    public static bool IsExported(ApplianceSignature s) =>
        !s.Hidden && !s.ConfirmedByPlug && (s.LabelSource == SignatureLabelSource.User || s.Count >= MinEventsForMetrics);

    // ------------------------------------------------------------------ processing loop

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _log.LogError(ex, "NILM processing failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        try { await SaveMarkerAsync(CancellationToken.None); }
        catch (Exception ex) { _log.LogDebug("NILM: final marker not saved: {Reason}", ex.Message); }
    }

    /// <summary>One pass: settings and main meter, backfill (once), queued live samples, ripe events, summary, nightly job.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        var nightly = false;
        await _gate.WaitAsync(ct);
        try
        {
            var now = _clock();
            var s = await _settings.GetAsync(ct);
            _current = new NilmSettings(s.NilmEnabled, s.MinEventDeltaW, s.ClusterWindowDays, s.MlEnabled);
            _detector.MinEventDeltaW = s.MinEventDeltaW;
            if (!_initialized) await InitializeAsync(ct);

            if (!s.NilmEnabled)
            {
                _acceptSamples = false;
                _detector.Reset();
                _backfilled = false;
                while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
                Summary = Summary with { Enabled = false, AtUtc = now };
                return;
            }

            if (now - _lastPlugs >= PlugsInterval || _mainId is null)
            {
                await LoadPlugsAsync(ct);
                _lastPlugs = now;
                var main = _plugs.FirstOrDefault(p => p.Role == PlugRole.MainMeter && !p.IsArchived)?.Id;
                if (main != _mainId)
                {
                    _log.LogInformation("NILM: main meter {Main}", main?.ToString() ?? "none");
                    _mainId = main;
                    _detector.Reset();
                    _backfilled = false;
                    while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
                }
            }
            if (_mainId is not { } mainId)
            {
                _acceptSamples = false;
                if (now - _lastSummary >= SummaryInterval) await RefreshSummaryAsync(now, ct);
                return;
            }

            _acceptSamples = true;   // from now on live samples queue up; the backfill covers what is already stored
            if (!_backfilled)
            {
                var covered = await BackfillAsync(mainId, now, ct);
                _backfilled = true;
                nightly = covered >= ReclusterAfterBackfill || _reclusterDue;
                _reclusterDue = false;
            }

            while (_queue.TryDequeue(out var sample))
            {
                Interlocked.Decrement(ref _queued);
                Detect(sample);
            }
            now = _clock();
            await ProcessRipeAsync(now, await PlugsForRipeAsync(now, ct), ct);
            await SaveRunsAsync(_runs.Expire(now), ct);

            if (now - _lastMarker >= MarkerInterval) await SaveMarkerAsync(ct);
            if (now >= _nextNightly)
            {
                _nextNightly = NextNightly(now);
                nightly = true;
            }
            if (now - _lastSummary >= SummaryInterval) await RefreshSummaryAsync(now, ct);
        }
        finally
        {
            _gate.Release();
        }
        if (nightly)
        {
            _trainDue = false;   // the re-clustering trains too
            await NightlyAsync(_clock(), ct);
        }
        else if (_trainDue && _mainId is not null && _current.Enabled && await _nightlyGate.WaitAsync(TimeSpan.Zero, ct))
        {
            // One training at a time („Antrenează acum” takes the same gate); busy now: the next pass tries again.
            try
            {
                _trainDue = false;
                await TrainAsync(_clock(), ct);
            }
            finally { _nightlyGate.Release(); }
        }
    }

    private async Task LoadPlugsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        _plugs = await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        _runs = new ApplianceRunBuilder(await repo.ListOpenRunsAsync(ct));
        _openSnapshot = _runs.Open();
        _signatures = (await repo.ListSignaturesAsync(ct)).ToList();
        _events = await repo.CountEventsAsync(ct);
        var info = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAsync(SettingKeys.NilmModelInfo, ct);
        if (!string.IsNullOrEmpty(info))
        {
            try { ModelInfo = JsonSerializer.Deserialize<NilmModelInfo>(info); }
            catch (JsonException) { ModelInfo = null; }
        }
        if (_current.MlEnabled && Classifier.Load(_modelPath))
            _log.LogInformation("NILM: classifier loaded from {Path}", _modelPath);
        var now = _clock();
        _nextNightly = NextNightly(now);
        var last = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAsync(SettingKeys.NilmLastReclusterTs, ct);
        // A night missed (the service was down at 03:30) or an upgrade from a version without the marker: re-cluster now.
        _reclusterDue = long.TryParse(last, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix)
            ? now - DateTimeOffset.FromUnixTimeSeconds(unix) > ReclusterMaxAge
            : _events > 0;
        // No saved model: train at startup when the labels allow it (not only at 03:30).
        _trainDue = _current.MlEnabled && !Classifier.HasModel;
        _initialized = true;
    }

    private void Detect(LiveSample sample)
    {
        foreach (var d in _detector.Add(sample))
            lock (_pending) _pending.Add(d.ToEvent());
    }

    /// <summary>
    /// Every polled plug (role plug) around an event, from the readings kept in memory: its live stream when it covers
    /// both windows, else its 10-second polls (<see cref="PlugEventMatcher.Readings"/>); those excluded from learning come
    /// with <see cref="PlugSeries.CanLabel"/> = false (they still make an event ambiguous).
    /// </summary>
    private IEnumerable<PlugSeries> LivePlugs(MeterEvent e)
    {
        var from = e.TsUtc - PlugEventMatcher.LoadMargin;
        var to = e.TsUtc + PlugEventMatcher.LoadMargin;
        foreach (var plug in _plugs.Where(p => p.IsPolled && p.Role == PlugRole.Plug))
        {
            var points = PlugEventMatcher.Readings(e.TsUtc, Slice(_plugStream, plug.Id, from, to), Slice(_plugMemory, plug.Id, from, to));
            if (points.Count >= 2) yield return new PlugSeries(plug.Id, plug.Alias, points, plug.TeachesNilm);
        }
    }

    /// <summary>
    /// The readings of every polled plug in [fromUtc, toUtc], loaded once (backfill batch, late events): the 10-second
    /// polls from <c>Samples</c> and the live stream from <c>LiveSamples</c> (used when it covers both windows).
    /// </summary>
    private async Task<Func<MeterEvent, IEnumerable<PlugSeries>>> StoredPlugsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var plugs = new Dictionary<Guid, (string Alias, List<PlugPowerPoint> Polled, List<PlugPowerPoint> Stream)>();
        using (var scope = _scopes.CreateScope())
        {
            var sampleRepo = scope.ServiceProvider.GetRequiredService<ISampleRepository>();
            var liveRepo = scope.ServiceProvider.GetRequiredService<ILiveRepository>();
            foreach (var plug in _plugs.Where(p => p.IsPolled && p.Role == PlugRole.Plug))
            {
                var rows = await sampleRepo.ListByPlugAsync(plug.Id, fromUtc - PlugEventMatcher.LoadMargin, toUtc + PlugEventMatcher.LoadMargin, ct);
                var stream = await liveRepo.ListSamplesAsync(plug.Id, fromUtc - PlugEventMatcher.LoadMargin, toUtc + PlugEventMatcher.LoadMargin, ct);
                plugs[plug.Id] = (plug.Alias,
                    rows.Where(r => r.Online && r.PowerMw is not null)
                        .Select(r => new PlugPowerPoint(r.TimestampUtc, r.PowerMw!.Value / 1000.0)).OrderBy(p => p.TsUtc).ToList(),
                    stream.Select(x => new PlugPowerPoint(x.TsUtc, x.PowerW)).OrderBy(p => p.TsUtc).ToList());
            }
        }
        return e =>
        {
            var lo = e.TsUtc - PlugEventMatcher.LoadMargin;
            var hi = e.TsUtc + PlugEventMatcher.LoadMargin;
            var list = new List<PlugSeries>();
            foreach (var (id, (alias, polled, stream)) in plugs)
            {
                var slice = PlugEventMatcher.Readings(e.TsUtc, stream.Where(p => p.TsUtc >= lo && p.TsUtc <= hi).ToList(),
                    polled.Where(p => p.TsUtc >= lo && p.TsUtc <= hi).ToList());
                // Excluded meanwhile (noisy): still counts as a plug that moved, cannot label.
                if (slice.Count >= 2) list.Add(new PlugSeries(id, alias, slice, _plugs.Any(p => p.Id == id && p.TeachesNilm)));
            }
            return list;
        };
    }

    /// <summary>
    /// Runs the detector over the stored samples of the main meter after the marker (in batches of
    /// <see cref="BackfillBatch"/>, the plugs' readings of a batch loaded once); returns the time covered.
    /// </summary>
    public async Task<TimeSpan> BackfillAsync(Guid mainId, DateTimeOffset nowUtc, CancellationToken ct)
    {
        DateTimeOffset? marker;
        using (var scope = _scopes.CreateScope())
        {
            var text = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAsync(SettingKeys.NilmLastProcessedTs, ct);
            marker = long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix) ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        }

        DateTimeOffset from;
        using (var scope = _scopes.CreateScope())
        {
            if (marker is { } m)
                from = m.AddSeconds(1);
            else if (await scope.ServiceProvider.GetRequiredService<ILiveRepository>().FirstSampleTsAsync(mainId, ct) is { } first)
                from = first;   // no marker: everything stored is unprocessed (the raw table keeps LiveRawRetentionDays)
            else
                return TimeSpan.Zero;
        }
        if (from >= nowUtc) return TimeSpan.Zero;

        _busy = true;
        try
        {
            var samples = 0;
            for (var t = from; t < nowUtc; t += BackfillBatch)
            {
                var to = t + BackfillBatch < nowUtc ? t + BackfillBatch : nowUtc;
                IReadOnlyList<LiveSample> batch;
                using (var scope = _scopes.CreateScope())
                    batch = await scope.ServiceProvider.GetRequiredService<ILiveRepository>().ListSamplesAsync(mainId, t, to.AddSeconds(-1), ct);
                // Pending events of the previous batch (its last LabelDelay) are processed with this batch: load from there.
                var stored = await StoredPlugsAsync(t - LabelDelay, to, ct);
                foreach (var s in batch) Detect(s);
                samples += batch.Count;
                await ProcessRipeAsync(nowUtc, stored, ct);
                await SaveMarkerAsync(ct);
            }
            _log.LogInformation("NILM backfill: {Samples} stored samples from {From:u} processed, {Events} events in total",
                samples, from, Interlocked.Read(ref _events));
            return nowUtc - from;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// The plug readings for the events ripe now: from memory, or, when some are older than the memory holds (detected
    /// after a long backfill or nightly job, from the queued live samples), from <c>Samples</c> loaded once.
    /// </summary>
    private async Task<Func<MeterEvent, IEnumerable<PlugSeries>>> PlugsForRipeAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        DateTimeOffset? oldest;
        lock (_pending) oldest = _pending.Where(e => nowUtc - e.TsUtc >= LabelDelay).Select(e => (DateTimeOffset?)e.TsUtc).Min();
        if (oldest is not { } o || nowUtc - o <= PlugMemory - PlugEventMatcher.LoadMargin) return LivePlugs;
        var stored = await StoredPlugsAsync(o, nowUtc, ct);
        return e => nowUtc - e.TsUtc > PlugMemory - PlugEventMatcher.LoadMargin ? stored(e) : LivePlugs(e);
    }

    private async Task SaveMarkerAsync(CancellationToken ct)
    {
        _lastMarker = _clock();
        if (_detector.LastTs is not { } last) return;
        DateTimeOffset? oldestPending;
        lock (_pending) oldestPending = _pending.Count > 0 ? _pending.Min(e => e.TsUtc) : null;
        // A pending event is not stored yet: after a restart the detector must see it again (with a minute to warm up).
        var marker = oldestPending is { } p && p.AddSeconds(-60) < last ? p.AddSeconds(-60) : last;
        using var scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ISettingsRepository>()
            .SetAsync(SettingKeys.NilmLastProcessedTs, marker.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ct);
    }

    /// <summary>Counts a plug's matches over the last hour (event time); more than the limit excludes the plug automatically.</summary>
    private async Task<bool> RegisterMatchAsync(Guid plugId, DateTimeOffset ts, CancellationToken ct)
    {
        if (!_plugMatches.TryGetValue(plugId, out var q)) _plugMatches[plugId] = q = new Queue<DateTimeOffset>();
        q.Enqueue(ts);
        while (q.Count > 0 && ts - q.Peek() > TimeSpan.FromHours(1)) q.Dequeue();
        var plug = _plugs.FirstOrDefault(p => p.Id == plugId);
        // A single-appliance plug is never excluded automatically: its matches are that appliance, and it does not train.
        if (plug is null || plug.SingleAppliance || plug.NilmState != PlugNilmState.Auto || q.Count <= PlugNilmStates.MaxMatchesPerHour) return true;
        using (var scope = _scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPlugRepository>().SetNilmStateAsync(plugId, PlugNilmState.IgnoredAuto, ct);
        _plugs = _plugs.Select(p => p.Id == plugId ? p with { NilmState = PlugNilmState.IgnoredAuto } : p).ToList();
        _log.LogInformation("NILM: plug {Alias} matched {Count} events within an hour: excluded from learning (variable load)", plug.Alias, q.Count);
        return false;
    }

    private async Task ProcessRipeAsync(DateTimeOffset nowUtc, Func<MeterEvent, IEnumerable<PlugSeries>> plugsFor, CancellationToken ct)
    {
        List<MeterEvent> ripe;
        List<MeterEvent> later;
        lock (_pending)
        {
            ripe = _pending.Where(e => nowUtc - e.TsUtc >= LabelDelay).OrderBy(e => e.TsUtc).ToList();
            if (ripe.Count == 0) return;
            _pending.RemoveAll(e => nowUtc - e.TsUtc >= LabelDelay);
            later = _pending.ToList();
        }
        using var scope = _scopes.CreateScope();
        await ProcessBatchAsync(scope.ServiceProvider, ripe, plugsFor, later, ct);
        var newest = ripe[^1].TsUtc;
        _usedChanges.RemoveWhere(k => newest - k.ToUtc > TimeSpan.FromMinutes(10));
    }

    /// <summary>Label (plug, then model), signature (predicted, nearest, or new), then one write for the events and one for the runs.</summary>
    /// <param name="later">The events still waiting after this batch (neighbours of its last events).</param>
    private async Task ProcessBatchAsync(IServiceProvider sp, IReadOnlyList<MeterEvent> batch, Func<MeterEvent, IEnumerable<PlugSeries>> plugsFor,
        IReadOnlyList<MeterEvent> later, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<INilmRepository>();
        var prepared = new List<MeterEvent>(batch.Count);
        var created = new List<long>();
        var truths = new Dictionary<(Guid PlugId, DateTimeOffset Ts), string>();
        for (var i = 0; i < batch.Count; i++)
        {
            var e = batch[i];
            var neighbours = NeighboursOf(e, batch, later);
            if (PlugEventMatcher.Match(e.TsUtc, e.DeltaP, plugsFor(e), _usedChanges, neighbours) is { } m && await RegisterMatchAsync(m.PlugId, e.TsUtc, ct))
            {
                // A single-appliance plug labels with its appliance's name („Frigider”), any other with its alias.
                e = e with { Label = SingleAppliancePlug(m.PlugId)?.Appliance ?? m.Alias, Source = EventLabelSource.AutoPlug, LabelPlugId = m.PlugId };
                // Ground truth (e.g. a Miele appliance in its main wash): only for an event the plug already confirmed.
                if (GroundTruthLabel(m.PlugId, e.TsUtc) is { } truth)
                {
                    e = e with { Label = truth };
                    truths[(e.PlugId, e.TsUtc)] = truth;
                }
            }
            if (e.Source == EventLabelSource.None && _current.MlEnabled && Classifier.Predict(e) is { } p
                && p.Probability >= NilmClassifier.MinProbability && ModelInfo?.NearClass(e, p.Label) == true)
                e = e with { Label = p.Label, Source = EventLabelSource.Model };

            long? sigId = null;
            // Confirmed by a single-appliance plug: that plug's signature at once (no 60 % / 3 events), never an anonymous one.
            if (e.Source == EventLabelSource.AutoPlug && SingleAppliancePlug(e.LabelPlugId) is { } single)
                sigId = await SingleApplianceSignatureAsync(repo, e, single, created, ct);
            if (e.Source == EventLabelSource.Model)
                sigId = _signatures.Where(s => string.Equals(s.Name, e.Label, StringComparison.Ordinal))
                    .OrderByDescending(s => s.LabelSource).ThenByDescending(s => s.Count).FirstOrDefault()?.Id;
            // Anchors (named or linked, not hidden) first with 1.5 × eps — never one linked to another plug than the event's
            // label —, then the nearest other signature within eps.
            sigId ??= SignatureClusterer.Assign(EventFeatures.Of(e), e.LabelPlugId, _signatures.Where(s => s.IsAnchor).Select(AnchorCentre.Of),
                _signatures.Where(s => !s.IsAnchor).Select(SignatureCentre.Of));
            if (sigId is null)
            {
                var (mp, mq, mt) = SignatureClusterer.Means(new[] { EventFeatures.Of(e) });
                var cat = ApplianceCategories.Get(ApplianceCategories.Other);
                var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, "", cat.Icon, cat.Key, mp, mq, mt, 0, e.TsUtc,
                    SignatureLabelSource.Cluster, null, false), ct);
                _signatures.Add(sig);
                created.Add(sig.Id);
                sigId = sig.Id;
            }
            prepared.Add(e with { SignatureId = sigId });
        }

        _recentLabelled.AddRange(batch);
        var newestTs = batch[^1].TsUtc;
        _recentLabelled.RemoveAll(x => newestTs - x.TsUtc > TimeSpan.FromMinutes(1));
        var stored = await repo.AddEventsAsync(prepared, ct);
        Interlocked.Add(ref _events, stored.Count);
        // A signature created for an event that turned out to be a duplicate stays empty: remove it.
        foreach (var id in created.Where(id => stored.All(s => s.SignatureId != id)))
        {
            await repo.DeleteSignatureAsync(id, ct);
            _signatures.RemoveAll(s => s.Id == id);
        }

        var runs = new Dictionary<long, ApplianceRun>();
        foreach (var e in stored)
        {
            var change = _runs.Add(e);
            if (change.Opened is { } opened) runs[opened.StartEventId] = opened;
            foreach (var c in change.Closed) runs[c.StartEventId] = c;
        }
        await SaveRunsAsync(runs.Values.ToList(), ct, sp);
        // Before the plug rule: an anonymous signature named by the ground truth keeps that name when it gets linked.
        await NameFromGroundTruthAsync(repo, stored, truths, ct);
        await RefreshSignaturesAsync(repo, stored.Select(e => e.SignatureId!.Value).Distinct().ToList(), ct);
        await ConsumeMarksAsync(sp, repo, stored, ct);
    }

    /// <summary>
    /// The signature of an event confirmed by a single-appliance plug: the nearest signature linked to that plug within
    /// the anchor radius (<see cref="SignatureClusterer.AnchorScale"/> × eps; one per component, e.g. a compressor), else a
    /// new one, linked to the plug right away with the appliance's name and category (<see cref="SignatureLabelSource.Plug"/>).
    /// </summary>
    private async Task<long> SingleApplianceSignatureAsync(INilmRepository repo, MeterEvent e, Plug plug, List<long> created, CancellationToken ct)
    {
        var features = EventFeatures.Of(e);
        if (SignatureClusterer.Nearest(features, _signatures.Where(s => s.IsAnchor && s.PlugId == plug.Id).Select(SignatureCentre.Of),
                SignatureClusterer.AnchorScale) is { } own)
            return own;
        var (mp, mq, mt) = SignatureClusterer.Means(new[] { features });
        var cat = ApplianceCategories.Get(plug.ApplianceCategory);
        var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, plug.Appliance!, cat.Icon, cat.Key, mp, mq, mt, 0, e.TsUtc,
            SignatureLabelSource.Plug, plug.Id, false), ct);
        _signatures.Add(sig);
        created.Add(sig.Id);
        _log.LogInformation("NILM: signature #{Id} created for the single appliance {Name} of plug {Alias}", sig.Id, sig.Name, plug.Alias);
        return sig.Id;
    }

    /// <summary>
    /// Pending marks (oldest first) take the first stored event of the batch with |ΔP| ≥ <see cref="MarkMinDeltaW"/> from
    /// <see cref="MarkWindow"/> before the mark's time until it expires, one event each, labelled like a matched mark;
    /// expired marks are dropped.
    /// </summary>
    private async Task ConsumeMarksAsync(IServiceProvider sp, INilmRepository repo, IReadOnlyList<MeterEvent> stored, CancellationToken ct)
    {
        List<NilmMark> marks;
        lock (_marks)
        {
            if (_marks.Count == 0) return;
            marks = _marks.OrderBy(m => m.AtUtc).ToList();
        }
        var used = new HashSet<long>();
        var done = new List<NilmMark>();
        foreach (var m in marks)
        {
            var e = stored.Where(x => IsMarkable(x) && !used.Contains(x.Id) && x.TsUtc >= m.AtUtc - MarkWindow && x.TsUtc <= m.ExpiresUtc)
                .OrderBy(x => x.TsUtc).FirstOrDefault();
            if (e is null) continue;
            used.Add(e.Id);
            done.Add(m);
            await ApplyMarkAsync(sp, repo, m, e.Id, ct);
            _log.LogInformation("NILM: pending mark {Name} labelled the event {Id} at {Ts:o} (ΔP {DeltaP:0} W)", m.Name, e.Id, e.TsUtc, e.DeltaP);
        }
        var now = _clock();
        lock (_marks)
        {
            foreach (var m in _marks.Where(m => m.ExpiresUtc <= now && !done.Any(d => ReferenceEquals(d, m))))
                _log.LogInformation("NILM: mark {Name} of {At:o} expired without an event of at least {Min} W", m.Name, m.AtUtc, MarkMinDeltaW);
            _marks.RemoveAll(m => m.ExpiresUtc <= now || done.Any(d => ReferenceEquals(d, m)));
        }
        if (done.Count > 0) _signatures = (await repo.ListSignaturesAsync(ct)).ToList();
    }

    /// <summary>
    /// The main meter's events around <paramref name="e"/> that could move a plug's level beyond the tolerance of
    /// <paramref name="e"/> (|ΔP| ≥ <see cref="PlugEventMatcher.Tolerance"/>): the plug windows stop before them. Smaller
    /// steps (a UPS's 30 W next to a 2 kW heater) cannot spoil the median and do not shorten the windows.
    /// </summary>
    private Neighbours NeighboursOf(MeterEvent e, IReadOnlyList<MeterEvent> batch, IReadOnlyList<MeterEvent> later)
    {
        var tol = PlugEventMatcher.Tolerance(e.DeltaP);
        var around = _recentLabelled.Concat(batch).Concat(later)
            .Where(x => x.TsUtc != e.TsUtc && Math.Abs(x.DeltaP) >= tol && (x.TsUtc - e.TsUtc).Duration() <= PlugEventMatcher.Outer + PlugEventMatcher.Inner)
            .ToList();
        return new Neighbours(around.Where(x => x.TsUtc < e.TsUtc).Select(x => (DateTimeOffset?)x.TsUtc).Max(),
            around.Where(x => x.TsUtc > e.TsUtc).Select(x => (DateTimeOffset?)x.TsUtc).Min());
    }

    /// <summary>Stores opened and closed runs (closed ones corrected, counted in the energy metric) in one transaction.</summary>
    private async Task SaveRunsAsync(IReadOnlyList<ApplianceRun> runs, CancellationToken ct, IServiceProvider? sp = null)
    {
        if (runs.Count > 0)
        {
            using var scope = sp is null ? _scopes.CreateScope() : null;
            var provider = sp ?? scope!.ServiceProvider;
            var corrected = new List<ApplianceRun>(runs.Count);
            foreach (var r in runs)
                corrected.Add(r.EndReason == RunEndReason.Paired && r.EndUtc is not null ? await CorrectAsync(provider, r, ct) : r);
            var saved = await provider.GetRequiredService<INilmRepository>().SaveRunsAsync(corrected, ct);
            foreach (var r in saved)
            {
                if (r.IsOpen) _runs.Track(r);
                else if (r.EndReason != RunEndReason.Timeout && r.EstimatedWh is { } wh and > 0)
                    _applianceWh.AddOrUpdate(r.SignatureId, wh, (_, v) => v + wh);
            }
        }
        _openSnapshot = _runs.Open();
    }

    /// <summary>The run's energy from the house power when no other event happened during it (<see cref="ApplianceEnergy.Corrected"/>).</summary>
    private async Task<ApplianceRun> CorrectAsync(IServiceProvider sp, ApplianceRun run, CancellationToken ct)
    {
        if (run.EndUtc is not { } end || run.EstimatedWh is not { } nominal || _mainId is not { } main) return run;
        var repo = sp.GetRequiredService<INilmRepository>();
        if (await repo.CountEventsAsync(run.StartUtc.AddSeconds(1), end.AddSeconds(-1), ct) > 0) return run;
        var start = await repo.GetEventAsync(run.StartEventId, ct);
        if (start is null) return run;
        var house = await sp.GetRequiredService<ILiveRepository>()
            .ListSamplesAsync(main, run.StartUtc.AddSeconds(-MeterEventDetector.MaxFillSeconds), end, ct);
        // Skip the switch-on transient (rise) so the inrush does not count as consumption of the steady state.
        var from = run.StartUtc.AddSeconds(Math.Max(0, start.RiseSeconds));
        if (SignatureNaming.MeanPower(house, from, end) is not { } mean) return run;
        return run with { EstimatedWh = ApplianceEnergy.Corrected(nominal, mean, start.PowerBeforeW, end - run.StartUtc) };
    }

    private string? GroundTruthLabel(Guid plugId, DateTimeOffset tsUtc)
    {
        if (_groundTruth is null) return null;
        try
        {
            var label = _groundTruth.LabelFor(plugId, tsUtc);
            return string.IsNullOrWhiteSpace(label) ? null : SignatureNaming.CleanName(label);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "NILM: ground truth failed");
            return null;
        }
    }

    /// <summary>
    /// An anonymous signature (found by clustering, never named) of an event labelled by the ground truth takes that
    /// label as its name, with <see cref="SignatureLabelSource.Plug"/> (the plug confirmed the event); the plug link
    /// itself still follows the plug rule.
    /// </summary>
    private async Task NameFromGroundTruthAsync(INilmRepository repo, IReadOnlyList<MeterEvent> stored,
        IReadOnlyDictionary<(Guid PlugId, DateTimeOffset Ts), string> truths, CancellationToken ct)
    {
        if (truths.Count == 0) return;
        foreach (var e in stored)
        {
            if (e.SignatureId is not { } sid || !truths.TryGetValue((e.PlugId, e.TsUtc), out var name)) continue;
            var i = _signatures.FindIndex(s => s.Id == sid);
            if (i < 0) continue;
            var sig = _signatures[i];
            if (sig.LabelSource != SignatureLabelSource.Cluster
                || sig.Name.Length > 0 && !string.Equals(sig.Name, SignatureNaming.Unknown(sig.Id), StringComparison.Ordinal))
                continue;
            var named = sig with { Name = name, LabelSource = SignatureLabelSource.Plug };
            await repo.UpdateSignatureAsync(named, ct);
            _signatures[i] = named;
            _log.LogInformation("NILM: signature #{Id} named from the ground truth: {Name}", sig.Id, name);
        }
    }

    /// <summary>
    /// Recomputes the means of the given signatures (SQL aggregates over the window) and applies the plug rule: when
    /// ≥ 60 % of a signature's events were labelled by the same (not excluded) plug, the signature is measured by that
    /// plug (<see cref="ApplianceSignature.PlugId"/>; it also takes the plug's name unless the user named it). A signature
    /// linked to a single-appliance plug stays linked while any of its events comes from it
    /// (<see cref="SignatureNaming.LinksToPlug(int, int, bool, bool)"/>) and takes the appliance's name and category.
    /// </summary>
    private async Task RefreshSignaturesAsync(INilmRepository repo, IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var from = WindowStart(_clock());
        var updated = await repo.RecomputeSignaturesAsync(ids, from, ct);
        var excluded = ExcludedPlugs;
        var shares = (await repo.PlugSharesAsync(ids, from, excluded, ct)).ToDictionary(x => x.SignatureId);
        foreach (var sig in updated)
        {
            var named = sig;
            var plug = shares.TryGetValue(sig.Id, out var share) && share.TopPlugId is { } top ? _plugs.FirstOrDefault(p => p.Id == top) : null;
            if (share?.TopPlugId is { } plugId
                && SignatureNaming.LinksToPlug(share.TopPlugEvents, share.Events, plug?.Appliance is not null, sig.PlugId == plugId))
            {
                var alias = plug?.DisplayName ?? share.TopLabel ?? sig.Name;
                // A name from the ground truth („<appliance> · <phase>”) is more precise than the plug's alias: kept.
                named = sig.LabelSource == SignatureLabelSource.User
                        || sig.LabelSource == SignatureLabelSource.Plug && KasaMonitor.Core.Miele.MieleGroundTruthRule.IsGroundTruthName(sig.Name)
                    ? sig with { PlugId = plugId }
                    : sig with { PlugId = plugId, Name = alias, LabelSource = SignatureLabelSource.Plug };
                if (plug?.Appliance is not null && named.LabelSource == SignatureLabelSource.Plug)
                {
                    var cat = ApplianceCategories.Get(plug.ApplianceCategory);
                    named = named with { Category = cat.Key, Icon = cat.Icon };
                }
            }
            else if (sig.PlugId is { } linked && sig.Count > 0 && !excluded.Contains(linked))
            {
                // Unlinked only when the plug no longer explains it; excluding the plug from learning keeps the link.
                named = sig.LabelSource == SignatureLabelSource.Plug
                    ? sig with { PlugId = null, Name = SignatureNaming.Unknown(sig.Id), LabelSource = SignatureLabelSource.Cluster }
                    : sig with { PlugId = null };
            }
            if (named != sig) await repo.UpdateSignatureAsync(named, ct);
            var i = _signatures.FindIndex(s => s.Id == sig.Id);
            if (i >= 0) _signatures[i] = named; else _signatures.Add(named);
        }
    }

    // ------------------------------------------------------------------ nightly: re-clustering, runs, training

    /// <summary>
    /// Re-clusters the newest <see cref="SignatureClusterer.MaxEvents"/> events of the window, except those in user
    /// signatures (a user label is never lost; model labels are re-evaluated):
    /// <list type="number">
    /// <item>anchors first: an event within <see cref="SignatureClusterer.AnchorScale"/> × eps of a signature labelled by
    /// the user or linked to a plug goes to the nearest such signature;</item>
    /// <item>DBSCAN on the rest: clusters keep the id (name, category) of the anonymous signature most of their events
    /// already had, noise joins the nearest new cluster within eps or none;</item>
    /// <item>signatures that are the same component are merged (<see cref="SignatureMerger"/>, from the same profiles as
    /// the „fișa”), then the runs of the window are rebuilt.</item>
    /// </list>
    /// The clustering and the training run on a low-priority thread outside the processing lock; reading the snapshot and
    /// applying the result take the lock. One re-clustering at a time.
    /// </summary>
    public async Task<NilmRegroupResult> NightlyAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _nightlyGate.WaitAsync(ct);
        try { return await ReclusterAsync(nowUtc, ct); }
        finally { _nightlyGate.Release(); }
    }

    /// <summary>
    /// „Regrupează acum” on /consumatori: the nightly job on request (re-clustering, automatic merge, training). Throws
    /// <see cref="NilmBusyException"/> when a re-clustering is already running.
    /// </summary>
    public async Task<NilmRegroupResult> RegroupNowAsync(CancellationToken ct)
    {
        if (!await _nightlyGate.WaitAsync(TimeSpan.Zero, ct)) throw new NilmBusyException();
        try { return await ReclusterAsync(_clock(), ct); }
        finally { _nightlyGate.Release(); }
    }

    private async Task<NilmRegroupResult> ReclusterAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        _busy = true;
        try
        {
            IReadOnlyList<MeterEvent> events;
            Dictionary<long, ApplianceSignature> signatures;
            await _gate.WaitAsync(ct);
            try
            {
                using var scope = _scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
                events = await repo.ListNewestEventsAsync(WindowStart(nowUtc), nowUtc, SignatureClusterer.MaxEvents, ct);
                signatures = (await repo.ListSignaturesAsync(ct)).ToDictionary(s => s.Id);
            }
            finally { _gate.Release(); }

            var free = events.Where(e => !IsPinned(e, signatures)).ToList();
            var anchors = signatures.Values.Where(s => s.IsAnchor).Select(AnchorCentre.Of).ToList();
            var anchorOf = free.Select(e => SignatureClusterer.NearestAnchor(EventFeatures.Of(e), e.LabelPlugId, anchors)).ToArray();
            var rest = Enumerable.Range(0, free.Count).Where(i => anchorOf[i] is null).ToList();
            var features = rest.Select(i => EventFeatures.Of(free[i])).ToList();
            var restLabels = await LowPriority.Run(() => SignatureClusterer.Cluster(features), "nilm-cluster");
            var labels = Enumerable.Repeat(-1, free.Count).ToArray();
            for (var k = 0; k < rest.Count; k++) labels[rest[k]] = restLabels[k];

            NilmRegroupResult result;
            await _gate.WaitAsync(ct);
            try
            {
                using var scope = _scopes.CreateScope();
                result = await ApplyClustersAsync(scope.ServiceProvider, free, anchorOf, labels, nowUtc, ct);
                await scope.ServiceProvider.GetRequiredService<ISettingsRepository>()
                    .SetAsync(SettingKeys.NilmLastReclusterTs, nowUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ct);
            }
            finally { _gate.Release(); }

            // The labels may have moved with the events: retrain (nightly, startup, „Regrupează acum”).
            return _current.MlEnabled ? result with { Training = await TrainAsync(nowUtc, ct) } : result;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <param name="sp">Services of the caller's scope.</param>
    /// <param name="snapshot">The re-clustered events (not in user signatures) as read before the clustering.</param>
    /// <param name="anchorOf">Per event: the anchor it went to (within 1.5 × eps), or null.</param>
    /// <param name="snapshotLabels">Per event: its DBSCAN cluster (-1 = noise or anchored).</param>
    /// <param name="nowUtc">Time of the job.</param>
    /// <param name="ct">Cancellation.</param>
    private async Task<NilmRegroupResult> ApplyClustersAsync(IServiceProvider sp, IReadOnlyList<MeterEvent> snapshot, long?[] anchorOf,
        int[] snapshotLabels, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<INilmRepository>();
        var signatures = (await repo.ListSignaturesAsync(ct)).ToDictionary(s => s.Id);
        var labelledIds = signatures.Values.Where(s => s.IsLabelled).Select(s => s.Id).ToHashSet();
        // The clustering ran without the lock: events the user labelled or merged meanwhile (now in another signature,
        // pinned, or deleted) keep what the user did.
        var current = (await repo.ListNewestEventsAsync(WindowStart(nowUtc), nowUtc, SignatureClusterer.MaxEvents, ct)).ToDictionary(e => e.Id);
        var keep = snapshot.Select((e, i) => (e, i))
            .Where(x => current.TryGetValue(x.e.Id, out var now) && now.SignatureId == x.e.SignatureId && !IsPinned(now, signatures))
            .ToList();
        var free = keep.Select(x => x.e).ToList();
        var labels = keep.Select(x => snapshotLabels[x.i]).ToArray();
        // An anchor deleted meanwhile: the event is treated as noise.
        var anchored = keep.Select(x => anchorOf[x.i] is { } a && signatures.ContainsKey(a) ? a : (long?)null).ToArray();
        var assignment = new Dictionary<long, long?>();
        for (var i = 0; i < free.Count; i++)
            if (anchored[i] is { } a) assignment[free[i].Id] = a;

        var taken = new HashSet<long>();
        var centres = new List<SignatureCentre>();
        var clustered = free.Select((e, i) => (e, i, label: labels[i])).Where(x => anchored[x.i] is null && x.label >= 0);
        foreach (var group in clustered.GroupBy(x => x.label).OrderByDescending(g => g.Count()))
        {
            var members = group.Select(x => x.e).ToList();
            // Reuse an anonymous signature only: a labelled one keeps just the events within its radius.
            var reuse = members.Where(m => m.SignatureId is { } sid && signatures.ContainsKey(sid) && !labelledIds.Contains(sid) && !taken.Contains(sid))
                .GroupBy(m => m.SignatureId!.Value).OrderByDescending(g => g.Count()).Select(g => (long?)g.Key).FirstOrDefault();
            var (mp, mq, mt) = SignatureClusterer.Means(members.Select(EventFeatures.Of).ToList());
            long id;
            if (reuse is { } r)
            {
                id = r;
            }
            else
            {
                var cat = ApplianceCategories.Get(ApplianceCategories.Other);
                var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, "", cat.Icon, cat.Key, mp, mq, mt, members.Count,
                    members.Max(m => m.TsUtc), SignatureLabelSource.Cluster, null, false), ct);
                signatures[sig.Id] = sig;
                id = sig.Id;
            }
            taken.Add(id);
            centres.Add(new SignatureCentre(id, mp, mq, mt, mt > 0));
            foreach (var m in members) assignment[m.Id] = id;
        }
        for (var i = 0; i < free.Count; i++)
            if (anchored[i] is null && labels[i] < 0) assignment[free[i].Id] = SignatureClusterer.Nearest(EventFeatures.Of(free[i]), centres);

        var before = free.ToDictionary(e => e.Id, e => e.SignatureId);
        var changed = assignment.Where(kv => before[kv.Key] != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        await repo.SetSignaturesAsync(changed, ct);

        _signatures = (await repo.ListSignaturesAsync(ct)).ToList();
        await RefreshSignaturesAsync(repo, _signatures.Select(s => s.Id).ToList(), ct);
        foreach (var s in _signatures.Where(s => s.Count == 0 && s.LabelSource != SignatureLabelSource.User).ToList())
        {
            await repo.DeleteSignatureAsync(s.Id, ct);
            _signatures.Remove(s);
        }

        var merges = await AutoMergeAsync(repo, nowUtc, ct);
        var merged = merges.Count;

        await RebuildRunsAsync(sp, null, WindowStart(nowUtc), nowUtc, ct);
        // No retention: events and runs stay (unlimited since 0.9.2, the detected appliances' history on /history).
        var anchoredCount = anchored.Count(a => a is not null);
        _log.LogInformation("NILM re-clustering: {Events} events ({Anchored} to labelled signatures), {Clusters} clusters, {Signatures} signatures, {Changed} events moved, {Merged} signatures merged",
            free.Count, anchoredCount, taken.Count, _signatures.Count, changed.Count, merged);
        await RefreshSummaryAsync(nowUtc, ct);
        return new NilmRegroupResult(free.Count, anchoredCount, taken.Count, _signatures.Count, changed.Count, merged, merges);
    }

    /// <summary>
    /// An event the re-clustering never moves: labelled by the user, or labelled by the plug its signature is linked to.
    /// The other events of a labelled signature go through the anchors again (they stay when within its radius), so a
    /// signature's centre cannot ratchet away by keeping everything it once caught.
    /// </summary>
    private static bool IsPinned(MeterEvent e, IReadOnlyDictionary<long, ApplianceSignature> signatures) =>
        e.Source == EventLabelSource.User
        || (e.LabelPlugId is { } plug && e.SignatureId is { } sid && signatures.TryGetValue(sid, out var s) && s.PlugId == plug);

    /// <summary>
    /// The automatic merge (<see cref="SignatureMerger"/>) over the profiles of every signature in the window (the same
    /// profiles as the „fișa”). Returns the merges applied. Runs are rebuilt by the caller.
    /// </summary>
    private async Task<IReadOnlyList<MergeStep>> AutoMergeAsync(INilmRepository repo, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var from = WindowStart(nowUtc);
        var profiles = SignatureProfiles.Build(await repo.ListNewestEventsAsync(from, nowUtc, SignatureClusterer.MaxEvents, ct), await repo.ListRunsAsync(from, nowUtc, ct),
            ExcludedPlugs, nowUtc, _tz);
        var steps = SignatureMerger.Plan(_signatures.Select(s => new MergeCandidate(s, profiles.GetValueOrDefault(s.Id))).ToList());
        var survivors = new HashSet<long>();
        var merged = new List<MergeStep>();
        foreach (var step in steps)
        {
            if (!await MergeCoreAsync(repo, step.SurvivorId, step.AbsorbedId, byUser: false, ct)) continue;
            merged.Add(step);
            survivors.Remove(step.AbsorbedId);
            survivors.Add(step.SurvivorId);
            _log.LogInformation("NILM: signature #{Absorbed} merged into #{Survivor} ({Reason})", step.AbsorbedId, step.SurvivorId, step.Reason);
        }
        if (survivors.Count > 0) await RefreshSignaturesAsync(repo, survivors.ToList(), ct);
        return merged;
    }

    /// <summary>
    /// Moves every event and run of <paramref name="absorbedId"/> into <paramref name="survivorId"/> and deletes it: the
    /// single merge routine (automatic merge, „Unește cu…”, „Adaugă la aparatul…”, the selector above the table). The
    /// survivor keeps its name; it takes the absorbed one's plug and category when it has none; a merge by the user marks an
    /// automatic survivor as the user's. The absorbed signature's metric series disappears, the survivor's continues.
    /// Means, the plug rule and the runs are refreshed by the caller.
    /// </summary>
    private async Task<bool> MergeCoreAsync(INilmRepository repo, long survivorId, long absorbedId, bool byUser, CancellationToken ct)
    {
        if (survivorId == absorbedId || await repo.GetSignatureAsync(absorbedId, ct) is not { } source
            || await repo.GetSignatureAsync(survivorId, ct) is not { } into)
            return false;
        await repo.MoveSignatureAsync(absorbedId, survivorId, ct);
        var takeCategory = into.Category == ApplianceCategories.Other && source.Category != ApplianceCategories.Other;
        await repo.UpdateSignatureAsync(into with
        {
            LabelSource = byUser && into.LabelSource == SignatureLabelSource.Cluster ? SignatureLabelSource.User : into.LabelSource,
            PlugId = into.PlugId ?? source.PlugId,
            Category = takeCategory ? source.Category : into.Category,
            Icon = takeCategory ? source.Icon : into.Icon,
        }, ct);
        _signatures.RemoveAll(s => s.Id == absorbedId);
        _applianceWh.TryRemove(absorbedId, out _);
        return true;
    }

    /// <summary>
    /// „Antrenează acum” on /consumatori: the nightly training on request (also when the model is off in Settings, so
    /// the result can be seen). Throws <see cref="NilmBusyException"/> while a re-clustering or another training runs.
    /// </summary>
    public async Task<NilmTrainResult> TrainNowAsync(CancellationToken ct)
    {
        if (!await _nightlyGate.WaitAsync(TimeSpan.Zero, ct)) throw new NilmBusyException();
        _busy = true;
        try { return await TrainAsync(_clock(), ct); }
        finally
        {
            _busy = false;
            _nightlyGate.Release();
        }
    }

    private async Task<NilmTrainResult> TrainAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IReadOnlyList<MeterEvent> labeled;
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            // Single-appliance plugs' events are explained by the plug: neither a class nor examples of the model.
            labeled = await scope.ServiceProvider.GetRequiredService<INilmRepository>().ListLabeledEventsAsync(TrainingExcluded(_plugs), SignatureClusterer.MaxEvents, ct);
        }
        finally { _gate.Release(); }
        var classes = labeled.Where(e => !string.IsNullOrWhiteSpace(e.Label)).Select(e => e.Label).Distinct(StringComparer.Ordinal).Count();
        if (!NilmClassifier.CanTrain(labeled))
        {
            _log.LogInformation("NILM classifier: {Labeled} labelled events, not enough to train ({Min} in ≥ {Classes} classes)",
                labeled.Count, NilmClassifier.MinExamples, NilmClassifier.MinClasses);
            return new NilmTrainResult(false, labeled.Count, classes, null, watch.Elapsed);
        }
        var info = await LowPriority.Run(() => Classifier.Train(labeled, nowUtc), "nilm-train");
        if (info is null) return new NilmTrainResult(false, labeled.Count, classes, null, watch.Elapsed);
        try { Classifier.Save(_modelPath); }
        catch (Exception ex) { _log.LogWarning("NILM classifier: saving {Path} failed: {Reason}", _modelPath, ex.Message); }
        ModelInfo = info;
        using (var scope = _scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().SetAsync(SettingKeys.NilmModelInfo, JsonSerializer.Serialize(info), ct);
        _log.LogInformation("NILM classifier trained: {Examples} events, {Classes} classes, accuracy {Accuracy:P1} ({Folds}-fold) in {Seconds:0.0} s",
            info.Examples, info.Classes.Count, info.Accuracy, info.Folds, watch.Elapsed.TotalSeconds);
        return new NilmTrainResult(true, labeled.Count, classes, info, watch.Elapsed);
    }

    /// <summary>
    /// Deletes and rebuilds the runs of the given signatures (all when null) that started at or after <paramref name="fromUtc"/>
    /// from their stored events (SQL-filtered by signature); the live run state continues from the result.
    /// </summary>
    private async Task RebuildRunsAsync(IServiceProvider sp, IReadOnlyCollection<long>? signatureIds, DateTimeOffset fromUtc, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var repo = sp.GetRequiredService<INilmRepository>();
        await repo.DeleteRunsAsync(signatureIds, fromUtc, ct);
        _runs = new ApplianceRunBuilder(await repo.ListOpenRunsAsync(ct));
        var events = signatureIds is null
            ? await repo.ListEventsAsync(fromUtc, nowUtc, ct)
            : await repo.ListEventsBySignaturesAsync(signatureIds, fromUtc, ct);
        var runs = new Dictionary<long, ApplianceRun>();
        foreach (var e in events.Where(e => e.SignatureId is not null))
        {
            var change = _runs.Add(e);
            if (change.Opened is { } opened) runs[opened.StartEventId] = opened;
            foreach (var c in change.Closed) runs[c.StartEventId] = c;
        }
        foreach (var c in _runs.Expire(nowUtc)) runs[c.StartEventId] = c;
        // Rebuilt runs are not new energy: keep the cumulative metric as it is.
        var saved = await repo.SaveRunsAsync(await CorrectAllAsync(sp, runs.Values.ToList(), nowUtc - CorrectRecent, ct), ct);
        foreach (var r in saved.Where(r => r.IsOpen)) _runs.Track(r);
        _openSnapshot = _runs.Open();
    }

    /// <summary>A rebuild corrects (house-power check, 3 queries per run) only the runs that ended this recently; older ones keep the nominal energy.</summary>
    public static readonly TimeSpan CorrectRecent = TimeSpan.FromHours(24);

    private async Task<List<ApplianceRun>> CorrectAllAsync(IServiceProvider sp, IReadOnlyList<ApplianceRun> runs, DateTimeOffset correctFromUtc,
        CancellationToken ct)
    {
        var result = new List<ApplianceRun>(runs.Count);
        foreach (var r in runs)
            result.Add(r.EndReason == RunEndReason.Paired && r.EndUtc is { } end && end >= correctFromUtc ? await CorrectAsync(sp, r, ct) : r);
        return result;
    }

    // ------------------------------------------------------------------ summary (cards, metrics)

    private async Task RefreshSummaryAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        _lastSummary = nowUtc;
        var midnight = LocalMidnightUtc(nowUtc);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, _tz).Date);
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        var detected = _signatures.Where(s => !s.Hidden && !s.ConfirmedByPlug).ToDictionary(s => s.Id);
        var open = _openSnapshot;

        var active = open.Where(r => detected.ContainsKey(r.SignatureId))
            .Select(r => { var s = detected[r.SignatureId]; return new ActiveAppliance(s.Id, s.Name, s.Category, s.Icon, s.LabelSource, r.PowerW, r.StartUtc); })
            .OrderByDescending(a => a.PowerW).ToList();

        var runs = await repo.ListRunsAsync(midnight, nowUtc, ct);
        var today = runs.Where(r => detected.ContainsKey(r.SignatureId))
            .GroupBy(r => r.SignatureId)
            .Select(g => { var s = detected[g.Key]; return new ApplianceEnergyRow(s.Id, s.Name, s.Category, s.Icon, g.Sum(r => ApplianceEnergy.InRange(r, midnight, nowUtc, nowUtc))); })
            .Where(x => x.Wh > 0).OrderByDescending(x => x.Wh).ToList();

        double houseWh = 0, covered = 0;
        if (_mainId is { } main)
        {
            var minutes = await scope.ServiceProvider.GetRequiredService<ILiveRepository>().ListMinutesAsync(main, midnight, nowUtc, ct);
            houseWh = minutes.Sum(m => m.EnergyWh);
            covered = minutes.Sum(m => m.CoveredSeconds);
        }
        var plugsDayWh = _states.Snapshot().Values.Where(st => PlugRoles.CountsInTotals(st.Role)).Sum(st => st.LastReading?.TodayWh ?? 0);
        var plugsWh = ApplianceEnergy.PlugsOverCoveredTime(plugsDayWh, covered, (nowUtc - midnight).TotalSeconds);
        var unidentified = _mainId is null ? 0 : ApplianceEnergy.Unidentified(houseWh, plugsWh, today.Sum(x => x.Wh));

        if (day != _summaryDay)
        {
            _unidentifiedBase += _unidentifiedToday;   // the previous day's final value stays in the cumulative counter
            _unidentifiedToday = 0;
            _summaryDay = day;
        }
        _unidentifiedToday = Math.Max(_unidentifiedToday, unidentified);
        Summary = new NilmSummary(nowUtc, _current.Enabled, _mainId is not null, active, today, houseWh, plugsWh, unidentified);

        var power = active.GroupBy(a => a.SignatureId).ToDictionary(g => g.Key, g => g.Sum(a => a.PowerW));
        Metrics = new NilmMetricSnapshot(_signatures.Where(IsExported)
            .Select(s => new NilmMetricItem(s.Id, s.Name, s.Category, s.LabelSource, power.TryGetValue(s.Id, out var w) ? w : null,
                _applianceWh.GetValueOrDefault(s.Id)))
            .ToList(), Interlocked.Read(ref _events), _unidentifiedBase + _unidentifiedToday);
    }

    /// <summary>
    /// The detected appliances (not hidden, not measured by a plug: those are already in Σ plugs) and their runs overlapping
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>) (up to now), with the time they were read: the home page's
    /// energy and the „Consumatori” section of /history. Read in its own scope without the processing lock.
    /// </summary>
    public async Task<(IReadOnlyDictionary<long, ApplianceSignature> Detected, IReadOnlyList<ApplianceRun> Runs, DateTimeOffset NowUtc)> GetDetectedRunsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var now = _clock();
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        var detected = (await repo.ListSignaturesAsync(ct)).Where(s => !s.Hidden && !s.ConfirmedByPlug).ToDictionary(s => s.Id);
        if (detected.Count == 0) return (detected, Array.Empty<ApplianceRun>(), now);
        var runs = await repo.ListRunsAsync(fromUtc, toUtc < now ? toUtc : now, ct);
        return (detected, runs.Where(r => detected.ContainsKey(r.SignatureId)).ToList(), now);
    }

    // ------------------------------------------------------------------ /consumatori

    /// <summary>
    /// The page's data, read in its own scope without waiting for the processing lock (a snapshot: the nightly job or a
    /// backfill never blocks the page). Detected signatures: by default only those of <see cref="SignatureListing.ShownByDefault"/>
    /// (every one with <paramref name="showRare"/>), largest energy over 7 days first, paged by <see cref="PageSize"/>;
    /// events of the last 24 h paged newest first.
    /// </summary>
    public async Task<NilmOverview> GetOverviewAsync(int page, int eventsPage, CancellationToken ct, bool showRare = false)
    {
        var now = _clock();
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        var signatures = await repo.ListSignaturesAsync(ct);
        var midnight = LocalMidnightUtc(now);
        var from30 = now.AddDays(-30);
        var from7 = now.AddDays(-7);
        var runs = (await repo.ListRunsAsync(from30, now, ct)).ToLookup(r => r.SignatureId);
        var open = _openSnapshot.GroupBy(r => r.SignatureId).ToDictionary(g => g.Key, g => g.Sum(r => r.PowerW));
        SignatureRow Row(ApplianceSignature s) => new(s,
            runs[s.Id].Sum(r => ApplianceEnergy.InRange(r, midnight, now, now)),
            runs[s.Id].Sum(r => ApplianceEnergy.InRange(r, from30, now, now)),
            open.TryGetValue(s.Id, out var w) ? w : null,
            UncertainRuns: runs[s.Id].Count(r => r.EndReason == RunEndReason.Timeout),
            Last7DaysWh: runs[s.Id].Sum(r => ApplianceEnergy.InRange(r, from7, now, now)));

        // The "posibil <priză>" badge of every detected signature (one grouped query): it keeps a rare one in the list.
        var detectedAll = signatures.Where(s => !s.ConfirmedByPlug).ToList();
        var possible = await PossiblePlugsAsync(scope.ServiceProvider, repo, detectedAll.Select(s => s.Id).ToList(), now, ct);
        var rows = detectedAll.Select(s => Row(s) with { PossiblePlug = possible.GetValueOrDefault(s.Id) }).ToList();
        var listed = SignatureListing.Ordered(rows.Where(r => showRare || SignatureListing.ShownByDefault(r.Signature, r.PossiblePlug is not null)),
            r => r.Signature, r => r.Last7DaysWh).ToList();
        var pages = Math.Max(1, (listed.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var shown = listed.Skip(page * PageSize).Take(PageSize).ToList();
        var rare = rows.Count(r => !SignatureListing.ShownByDefault(r.Signature, r.PossiblePlug is not null));
        var cleanable = rows.Count(r => SignatureListing.IsRareAndOld(r.Signature, now, r.PossiblePlug is not null));
        var measured = signatures.Where(s => s.ConfirmedByPlug).OrderByDescending(s => s.Count).Select(Row).ToList();

        var names = signatures.ToDictionary(s => s.Id, s => s.Name);
        var eventsTotal = await repo.CountEventsAsync(now.AddHours(-24), now, ct);
        var eventPages = Math.Max(1, (eventsTotal + PageSize - 1) / PageSize);
        eventsPage = Math.Clamp(eventsPage, 0, eventPages - 1);
        var events = (await repo.ListEventsPageAsync(now.AddHours(-24), now, eventsPage * PageSize, PageSize, ct))
            .Select(e => new EventRow(e, e.SignatureId is { } sid && names.TryGetValue(sid, out var n) ? n : null)).ToList();

        var plugs = _plugs.Count > 0 ? _plugs : await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var (labeled, classes) = await repo.CountLabeledAsync(TrainingExcluded(plugs), ct);
        var main = plugs.FirstOrDefault(p => p.Role == PlugRole.MainMeter && !p.IsArchived)?.Alias;
        var plugRows = PlugRows(plugs);
        return new NilmOverview(shown, detectedAll.Count, page, pages, measured, signatures, events, eventsTotal, eventsPage, eventPages,
            ModelInfo, labeled, classes, Summary, main, _current, plugRows, _busy, rare, cleanable, showRare);
    }

    /// <summary>Runs a UI action under the processing lock; throws <see cref="NilmBusyException"/> after <see cref="ActionWait"/>.</summary>
    private async Task<T> UnderGateAsync<T>(Func<IServiceProvider, INilmRepository, Task<T>> action, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(ActionWait, ct)) throw new NilmBusyException();
        try
        {
            using var scope = _scopes.CreateScope();
            var result = await action(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<INilmRepository>());
            _signatures = (await scope.ServiceProvider.GetRequiredService<INilmRepository>().ListSignaturesAsync(ct)).ToList();
            await RefreshSummaryAsync(_clock(), ct);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Renames a signature (kept by the re-clustering; a signature measured by a plug stays measured by it).</summary>
    public Task<bool> RenameAsync(long id, string name, CancellationToken ct) => UnderGateAsync(async (_, repo) =>
    {
        name = SignatureNaming.CleanName(name);
        if (name.Length == 0 || await repo.GetSignatureAsync(id, ct) is not { } s) return false;
        await repo.UpdateSignatureAsync(s with { Name = name, LabelSource = SignatureLabelSource.User }, ct);
        return true;
    }, ct);

    /// <summary>Sets the category (and its icon).</summary>
    public Task<bool> SetCategoryAsync(long id, string category, CancellationToken ct) =>
        UnderGateAsync((_, repo) => SetCategoryCoreAsync(repo, id, category, ct), ct);

    private static async Task<bool> SetCategoryCoreAsync(INilmRepository repo, long id, string category, CancellationToken ct)
    {
        if (await repo.GetSignatureAsync(id, ct) is not { } s) return false;
        var c = ApplianceCategories.Get(category);
        await repo.UpdateSignatureAsync(s with { Category = c.Key, Icon = c.Icon }, ct);
        return true;
    }

    /// <summary>Hides a signature: not in the cards and metrics (its energy stays "unidentified").</summary>
    public Task<bool> SetHiddenAsync(long id, bool hidden, CancellationToken ct) => UnderGateAsync(async (_, repo) =>
    {
        if (await repo.GetSignatureAsync(id, ct) is not { } s) return false;
        await repo.UpdateSignatureAsync(s with { Hidden = hidden }, ct);
        return true;
    }, ct);

    /// <summary>Whether a plug teaches the detection (the user can undo an automatic exclusion or exclude a plug).</summary>
    public Task SetPlugStateAsync(Guid plugId, PlugNilmState state, CancellationToken ct) => UnderGateAsync<bool>(async (sp, _) =>
    {
        await sp.GetRequiredService<IPlugRepository>().SetNilmStateAsync(plugId, state, ct);
        _plugMatches.Remove(plugId);
        await LoadPlugsAsync(ct);
        return true;
    }, ct);

    /// <summary>The polled plugs (role plug) of /consumatori and of <c>GET /api/nilm/plugs</c>, by alias.</summary>
    private static List<PlugNilmRow> PlugRows(IEnumerable<Plug> plugs) =>
        plugs.Where(p => p.IsPolled && p.Role == PlugRole.Plug).OrderBy(p => p.Alias).Select(PlugNilmRow.Of).ToList();

    /// <summary>The plugs that can label events (HTTP API), read without the processing lock.</summary>
    public async Task<IReadOnlyList<PlugNilmRow>> ListPlugsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        return PlugRows(await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct));
    }

    /// <summary>
    /// „Un singur aparat pe priză” (/plugs and <c>POST /api/nilm/plugs/{id}/appliance</c>): stores the flag, the name (empty
    /// = the alias) and the category (null keeps the current one), then the signatures linked to the plug take the
    /// appliance's name and category at once (or the alias again when the flag is cleared). A change of the flag retrains
    /// the classifier on the next pass (its labels leave or rejoin the training set) when the model is on. Null when the
    /// plug does not exist or is not a plug (a meter measures more than one appliance).
    /// </summary>
    public Task<PlugNilmRow?> SetPlugApplianceAsync(Guid plugId, bool single, string? name, string? category, CancellationToken ct) =>
        UnderGateAsync<PlugNilmRow?>(async (sp, repo) =>
        {
            var plugRepo = sp.GetRequiredService<IPlugRepository>();
            if ((await plugRepo.ListAllAsync(ct)).FirstOrDefault(p => p.Id == plugId && p.Role == PlugRole.Plug) is not { } before) return null;
            if (!await plugRepo.SetApplianceAsync(plugId, single, name, category ?? before.ApplianceCategory, ct)) return null;
            _plugMatches.Remove(plugId);
            await LoadPlugsAsync(ct);
            var linked = (await repo.ListSignaturesAsync(ct)).Where(s => s.PlugId == plugId).Select(s => s.Id).ToList();
            await RefreshSignaturesAsync(repo, linked, ct);
            if (before.SingleAppliance != single && _current.MlEnabled) _trainDue = true;
            var after = _plugs.First(p => p.Id == plugId);
            _log.LogInformation("NILM: plug {Alias}: single appliance {Single} ({Name}, {Category})", after.Alias, after.SingleAppliance,
                after.Appliance ?? after.ApplianceName ?? "-", after.ApplianceCategory ?? "-");
            return PlugNilmRow.Of(after);
        }, ct);

    /// <summary>The „posibil &lt;priză&gt;” badge of the given signatures (one grouped query): signature → plug alias.</summary>
    private async Task<Dictionary<long, string>> PossiblePlugsAsync(IServiceProvider sp, INilmRepository repo, IReadOnlyCollection<long> ids,
        DateTimeOffset now, CancellationToken ct)
    {
        if (ids.Count == 0) return new Dictionary<long, string>();
        var plugs = _plugs.Count > 0 ? _plugs : await sp.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        return (await repo.PlugSharesAsync(ids, WindowStart(now), plugs.Where(p => PlugNilmStates.IsIgnored(p.NilmState)).Select(p => p.Id).ToList(), ct))
            .Where(x => x.TopPlugId is not null && x.TopPlugEvents > 0)
            // Never a null value: the badge, the default list and the cleaning all test the same key.
            .ToDictionary(x => x.SignatureId, x => plugs.FirstOrDefault(p => p.Id == x.TopPlugId)?.Alias ?? x.TopLabel ?? "priză");
    }

    /// <summary>
    /// „Curăță semnăturile rare”: deletes the signatures of <see cref="SignatureListing.IsRareAndOld"/> (at most 2 events,
    /// the newest older than 7 days, no plug, no name of their own, no „posibil &lt;priză&gt;” badge) with their runs;
    /// their events stay, without a signature.
    /// Returns how many were deleted.
    /// </summary>
    public Task<int> CleanRareSignaturesAsync(CancellationToken ct) => UnderGateAsync(async (sp, repo) =>
    {
        var now = _clock();
        var candidates = (await repo.ListSignaturesAsync(ct)).Where(s => SignatureListing.IsRareAndOld(s, now, possiblePlug: false)).ToList();
        var possible = await PossiblePlugsAsync(sp, repo, candidates.Select(s => s.Id).ToList(), now, ct);
        var rare = candidates.Where(s => !possible.ContainsKey(s.Id)).Select(s => s.Id).ToList();
        foreach (var id in rare) await repo.DeleteSignatureAsync(id, ct);
        _signatures.RemoveAll(s => rare.Contains(s.Id));
        if (rare.Count > 0)
            _log.LogInformation("NILM: deleted {Count} rare signatures (at most {Events} events, last seen more than {Days} days ago); their events stay unlinked",
                rare.Count, SignatureListing.RareMaxEvents, SignatureListing.RareMinAge.TotalDays);
        return rare.Count;
    }, ct);

    /// <summary>
    /// Moves every event (and run) of <paramref name="fromId"/> into <paramref name="intoId"/> and deletes <paramref name="fromId"/>
    /// (the selector above the table); the result is measured by the plug of either (target first).
    /// </summary>
    public Task<bool> MergeAsync(long fromId, long intoId, CancellationToken ct) => UnderGateAsync(async (sp, repo) =>
    {
        if (!await MergeCoreAsync(repo, intoId, fromId, byUser: true, ct)) return false;
        await RefreshSignaturesAsync(repo, new[] { intoId }, ct);
        await RebuildRunsAsync(sp, new[] { intoId }, WindowStart(_clock()), _clock(), ct);
        return true;
    }, ct);

    /// <summary>
    /// Whether „Adaugă la aparatul…” merges <paramref name="source"/> into <paramref name="target"/>: only when their
    /// centres are within the anchor radius (<see cref="SignatureClusterer.AnchorScale"/>); a distant component (a washing
    /// machine's motor added to its heater) only takes the name, a signature is one component.
    /// </summary>
    public static bool AddToMerges(ApplianceSignature source, ApplianceSignature target) =>
        SignatureClusterer.Distance(SignatureCentre.Of(source), SignatureCentre.Of(target), SignatureClusterer.AnchorScale) <= 1;

    /// <summary>
    /// „Adaugă la aparatul…”: when the centres are close (<see cref="AddToMerges"/>) the events of <paramref name="id"/>
    /// move into <paramref name="intoId"/> (the same routine as every merge); otherwise <paramref name="id"/> keeps its
    /// events and takes the name and category of <paramref name="intoId"/> (labelled by the user: two rows with one name,
    /// never merged automatically).
    /// </summary>
    public Task<AddToOutcome> AddToAsync(long id, long intoId, CancellationToken ct) => UnderGateAsync(async (sp, repo) =>
    {
        if (id == intoId || await repo.GetSignatureAsync(id, ct) is not { } source || await repo.GetSignatureAsync(intoId, ct) is not { } target)
            return AddToOutcome.Failed;
        if (!AddToMerges(source, target))
        {
            await repo.UpdateSignatureAsync(source with
            {
                Name = target.Name,
                Category = target.Category,
                Icon = target.Icon,
                LabelSource = SignatureLabelSource.User,
            }, ct);
            return AddToOutcome.Named;
        }
        if (!await MergeCoreAsync(repo, intoId, id, byUser: true, ct)) return AddToOutcome.Failed;
        await RefreshSignaturesAsync(repo, new[] { intoId }, ct);
        await RebuildRunsAsync(sp, new[] { intoId }, WindowStart(_clock()), _clock(), ct);
        return AddToOutcome.Merged;
    }, ct);

    /// <summary>
    /// „Unește cu…”: merges two signatures with the rule of the automatic merge for the survivor (user label, then linked
    /// to a plug, then more events). Returns the survivor's id, or null when one of them no longer exists.
    /// </summary>
    public Task<long?> MergeWithAsync(long id, long otherId, CancellationToken ct) => UnderGateAsync<long?>(async (sp, repo) =>
    {
        if (id == otherId || await repo.GetSignatureAsync(id, ct) is not { } a || await repo.GetSignatureAsync(otherId, ct) is not { } b) return null;
        var survivor = SignatureMerger.Survivor(a, b);
        var absorbed = survivor.Id == a.Id ? b : a;
        if (!await MergeCoreAsync(repo, survivor.Id, absorbed.Id, byUser: true, ct)) return null;
        await RefreshSignaturesAsync(repo, new[] { survivor.Id }, ct);
        await RebuildRunsAsync(sp, new[] { survivor.Id }, WindowStart(_clock()), _clock(), ct);
        return survivor.Id;
    }, ct);

    /// <summary>
    /// The „fișa” of a signature (read without the processing lock): its profile over the window (<see cref="SignatureProfiles"/>,
    /// the same calculation as the automatic merge) and the alias of the plug it matches.
    /// </summary>
    public async Task<SignatureProfileView?> GetProfileAsync(long id, CancellationToken ct)
    {
        var now = _clock();
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        if (await repo.GetSignatureAsync(id, ct) is not { } sig) return null;
        var from = WindowStart(now);
        var events = await repo.ListEventsBySignaturesAsync(new[] { id }, from, ct);
        var runs = await repo.ListRunsBySignatureAsync(id, from, ct);
        var plugs = _plugs.Count > 0 ? _plugs : await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var excluded = plugs.Where(p => PlugNilmStates.IsIgnored(p.NilmState)).Select(p => p.Id).ToList();
        var profile = SignatureProfiles.Of(id, events, runs, excluded, now, _tz);
        var plugId = profile.TopPlugId ?? sig.PlugId;
        var alias = plugId is { } pid ? plugs.FirstOrDefault(p => p.Id == pid)?.Alias ?? profile.TopPlugLabel : null;
        return new SignatureProfileView(sig, profile, alias, sig.PlugId is not null && sig.PlugId == plugId);
    }

    /// <summary>
    /// The signatures for the HTTP API (read without the processing lock). By default the filter of /consumatori: every
    /// signature measured by a plug, and the detected ones of <see cref="SignatureListing.ShownByDefault"/> that are not
    /// hidden; with <paramref name="all"/> every signature (rare and hidden ones too). Most events first, then id.
    /// </summary>
    public async Task<IReadOnlyList<SignatureListItem>> ListSignaturesAsync(bool all, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        var items = await ListItemsAsync(scope.ServiceProvider, repo, await repo.ListSignaturesAsync(ct), ct);
        return items.Where(i => all || i.Signature.ConfirmedByPlug
                || (!i.Signature.Hidden && SignatureListing.ShownByDefault(i.Signature, i.PlugAlias is not null)))
            .OrderByDescending(i => i.Signature.Count).ThenBy(i => i.Signature.Id).ToList();
    }

    /// <summary>One signature as in <see cref="ListSignaturesAsync"/>, or null when it does not exist.</summary>
    public async Task<SignatureListItem?> GetSignatureAsync(long id, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        if (await repo.GetSignatureAsync(id, ct) is not { } sig) return null;
        return (await ListItemsAsync(scope.ServiceProvider, repo, new[] { sig }, ct))[0];
    }

    private async Task<IReadOnlyList<SignatureListItem>> ListItemsAsync(IServiceProvider sp, INilmRepository repo,
        IReadOnlyList<ApplianceSignature> signatures, CancellationToken ct)
    {
        var plugs = _plugs.Count > 0 ? _plugs : await sp.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var possible = await PossiblePlugsAsync(sp, repo, signatures.Where(s => !s.ConfirmedByPlug).Select(s => s.Id).ToList(), _clock(), ct);
        return signatures.Select(s => s.PlugId is { } pid
                ? new SignatureListItem(s, plugs.FirstOrDefault(p => p.Id == pid)?.Alias, true)
                : new SignatureListItem(s, possible.GetValueOrDefault(s.Id), false))
            .ToList();
    }

    /// <summary>The newest <paramref name="max"/> events of a signature (all stored ones, not only the window), newest first.</summary>
    public async Task<IReadOnlyList<MeterEvent>> ListSignatureEventsAsync(long id, int max, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<INilmRepository>()
            .ListEventsBySignaturesAsync(new[] { id }, DateTimeOffset.UnixEpoch, ct);
        return events.Reverse().Take(max).ToList();
    }

    /// <summary>The stored events of a range with |ΔP| ≥ <paramref name="minAbsDeltaP"/>, oldest first, with their signature's name.</summary>
    public async Task<IReadOnlyList<EventRow>> ListEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, double minAbsDeltaP, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        var names = (await repo.ListSignaturesAsync(ct)).ToDictionary(s => s.Id, s => s.Name);
        return (await repo.ListEventsAsync(fromUtc, toUtc, ct)).Where(e => Math.Abs(e.DeltaP) >= minAbsDeltaP)
            .Select(e => new EventRow(e, e.SignatureId is { } sid && names.TryGetValue(sid, out var n) ? n : null)).ToList();
    }

    /// <summary>
    /// Splits a signature by clustering its events of the window again with half the eps; the largest part keeps the
    /// signature, every other part becomes a new one. Returns the number of parts (1 = could not be split).
    /// </summary>
    public Task<int> SplitAsync(long id, CancellationToken ct) => UnderGateAsync(async (sp, repo) =>
    {
        var events = await repo.ListEventsBySignaturesAsync(new[] { id }, WindowStart(_clock()), ct);
        var labels = SignatureClusterer.Cluster(events.Select(EventFeatures.Of).ToList(), scale: 0.5);
        var parts = events.Select((e, i) => (e, label: labels[i])).Where(x => x.label >= 0).GroupBy(x => x.label)
            .OrderByDescending(g => g.Count()).ToList();
        if (parts.Count < 2) return 1;
        var assignment = new Dictionary<long, long?>();
        var ids = new List<long> { id };
        foreach (var part in parts.Skip(1))
        {
            var cat = ApplianceCategories.Get(ApplianceCategories.Other);
            var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, "", cat.Icon, cat.Key, 0, 0, 0, 0, null,
                SignatureLabelSource.Cluster, null, false), ct);
            _signatures.Add(sig);
            ids.Add(sig.Id);
            foreach (var (e, _) in part) assignment[e.Id] = sig.Id;
        }
        await repo.SetSignaturesAsync(assignment, ct);
        await RefreshSignaturesAsync(repo, ids, ct);
        await RebuildRunsAsync(sp, ids, WindowStart(_clock()), _clock(), ct);
        return parts.Count;
    }, ct);

    /// <summary>
    /// The user's label of one event ("a fost boilerul"): the event gets the label and moves to the signature with that
    /// name if there is one; otherwise its own signature takes the name when it is an automatic one (not named by the
    /// user, not measured by a plug), else a new signature with that name is created for it.
    /// </summary>
    public Task<bool> LabelEventAsync(long eventId, string label, CancellationToken ct) => UnderGateAsync(async (sp, repo) =>
        await LabelEventCoreAsync(sp, repo, eventId, label, ct) is not null, ct);

    /// <summary>
    /// The body of <see cref="LabelEventAsync"/> (caller holds the processing lock): the event's signature afterwards and
    /// whether that signature took the name here (an automatic one renamed, or a new one), or null (empty label, no event).
    /// </summary>
    private async Task<(long SignatureId, bool Named)?> LabelEventCoreAsync(IServiceProvider sp, INilmRepository repo, long eventId, string label,
        CancellationToken ct)
    {
        label = SignatureNaming.CleanName(label);
        if (label.Length == 0 || await repo.GetEventAsync(eventId, ct) is not { } e) return null;
        var signatures = await repo.ListSignaturesAsync(ct);
        var current = e.SignatureId is { } cid ? signatures.FirstOrDefault(s => s.Id == cid) : null;
        long sigId;
        var named = true;
        if (signatures.FirstOrDefault(s => string.Equals(s.Name, label, StringComparison.Ordinal)) is { } existing)
        {
            sigId = existing.Id;
            named = false;
        }
        else if (current is { LabelSource: SignatureLabelSource.Cluster, PlugId: null })
        {
            sigId = current.Id;
            await repo.UpdateSignatureAsync(current with { Name = label, LabelSource = SignatureLabelSource.User }, ct);
        }
        else
        {
            var cat = ApplianceCategories.Get(ApplianceCategories.Other);
            var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, label, cat.Icon, cat.Key, 0, 0, 0, 0, null,
                SignatureLabelSource.User, null, false), ct);
            _signatures.Add(sig);
            sigId = sig.Id;
        }
        await repo.UpdateEventAsync(e with { Label = label, Source = EventLabelSource.User, LabelPlugId = null, SignatureId = sigId }, ct);
        var ids = current is not null && current.Id != sigId ? new[] { current.Id, sigId } : new[] { sigId };
        await RefreshSignaturesAsync(repo, ids, ct);
        if (current?.Id != sigId) await RebuildRunsAsync(sp, ids, WindowStart(_clock()), _clock(), ct);
        return (sigId, named);
    }

    /// <summary>
    /// „Acum am pornit X” (HTTP API, ground truth): the stored event of the main meter with |ΔP| ≥ <see cref="MarkMinDeltaW"/>
    /// nearest to <paramref name="atUtc"/> (default now) within ±<see cref="MarkWindow"/> is labelled by the user like an
    /// event label of /consumatori (<see cref="LabelEventAsync"/>); the signature that takes the name (an automatic one, or
    /// a new one) also takes <paramref name="category"/>. Without such an event (the appliance starts in a few seconds,
    /// or the event waits <see cref="LabelDelay"/>) the mark waits in memory for <see cref="MarkPendingFor"/> and the
    /// detector gives it the first such event it stores. A time older than <see cref="LabelDelay"/> + <see cref="MarkWindow"/>
    /// (every event of its window is stored already) without such an event is <see cref="MarkOutcome.NotFound"/>, never
    /// pending. Null when the name is empty.
    /// </summary>
    public Task<NilmMarkResult?> MarkAsync(string name, string? category, DateTimeOffset? atUtc, CancellationToken ct) =>
        UnderGateAsync<NilmMarkResult?>(async (sp, repo) =>
        {
            name = SignatureNaming.CleanName(name);
            if (name.Length == 0) return null;
            var now = _clock();
            var at = atUtc ?? now;
            var mark = new NilmMark(name, category is null ? null : ApplianceCategories.Normalize(category), at, (at > now ? at : now) + MarkPendingFor);
            var near = (await repo.ListEventsAsync(at - MarkWindow, at + MarkWindow, ct)).Where(IsMarkable)
                .OrderBy(e => (e.TsUtc - at).Duration()).FirstOrDefault();
            if (near is null && at < now - LabelDelay - MarkWindow) return new NilmMarkResult(MarkOutcome.NotFound, mark, null);
            if (near is null)
            {
                lock (_marks)
                {
                    _marks.RemoveAll(m => m.ExpiresUtc <= now);
                    _marks.Add(mark);
                }
                return new NilmMarkResult(MarkOutcome.Pending, mark, null);
            }
            return new NilmMarkResult(MarkOutcome.Matched, mark, await ApplyMarkAsync(sp, repo, mark, near.Id, ct));
        }, ct);

    private static bool IsMarkable(MeterEvent e) => Math.Abs(e.DeltaP) >= MarkMinDeltaW;

    /// <summary>Labels the event with the mark (caller holds the processing lock); returns the event as stored afterwards.</summary>
    private async Task<MeterEvent?> ApplyMarkAsync(IServiceProvider sp, INilmRepository repo, NilmMark mark, long eventId, CancellationToken ct)
    {
        if (await LabelEventCoreAsync(sp, repo, eventId, mark.Name, ct) is not { } r) return null;
        if (mark.Category is { } c && r.Named) await SetCategoryCoreAsync(repo, r.SignatureId, c, ct);
        return await repo.GetEventAsync(eventId, ct);
    }

    public override void Dispose()
    {
        _live.SampleReceived -= OnSample;
        _states.StateChanged -= OnPlugState;
        base.Dispose();
    }
}
