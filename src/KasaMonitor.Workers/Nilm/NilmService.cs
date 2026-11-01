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
/// One signature on /consumatori, with its energy today and over 30 days. <see cref="PossiblePlug"/>: a plug that labelled
/// some of its events but not enough to link it (fewer than 3, or under 60 %), shown as „posibil &lt;priză&gt;”.
/// </summary>
public sealed record SignatureRow(ApplianceSignature Signature, double TodayWh, double Last30DaysWh, double? ActiveW, string? PossiblePlug = null,
    int UncertainRuns = 0);

/// <summary>One event of the last 24 h on /consumatori.</summary>
public sealed record EventRow(MeterEvent Event, string? SignatureName);

/// <summary>A plug and whether it teaches the detection.</summary>
public sealed record PlugNilmRow(Guid PlugId, string Alias, PlugNilmState State);

public sealed record NilmOverview(IReadOnlyList<SignatureRow> Detected, int DetectedTotal, int Page, int PageCount,
    IReadOnlyList<SignatureRow> PlugMeasured, IReadOnlyList<ApplianceSignature> AllSignatures,
    IReadOnlyList<EventRow> Events, int EventsInWindow, int EventsPage, int EventsPageCount,
    NilmModelInfo? Model, int LabeledEvents, int LabeledClasses, NilmSummary Summary, string? MainMeterAlias, NilmSettings Settings,
    IReadOnlyList<PlugNilmRow> Plugs, bool Busy);

/// <summary>The NILM settings in effect.</summary>
public sealed record NilmSettings(bool Enabled, int MinEventDeltaW, int ClusterWindowDays, bool MlEnabled);

/// <summary>A UI action found the detection busy (backfill or nightly job) for longer than <see cref="NilmService.ActionWait"/>.</summary>
public sealed class NilmBusyException() : Exception("Procesare în curs (istoric sau regruparea de noapte); reîncearcă peste câteva secunde.");

/// <summary>
/// Appliance detection from the main meter (NILM). Feeds every 1-second sample of the main meter (live stream, plus the
/// stored <c>LiveSamples</c> not processed yet at start: the backfill) to the <see cref="MeterEventDetector"/>. Events are
/// processed in batches, <see cref="LabelDelay"/> after they happened (so the plugs' readings around them exist):
/// <list type="number">
/// <item>label from a plug that made the same clean step (<see cref="PlugEventMatcher"/>, plug readings kept in memory for
/// the last minutes, or loaded once per backfill batch); a plug matching more than
/// <see cref="PlugNilmStates.MaxMatchesPerHour"/> events in an hour is excluded from learning automatically;</item>
/// <item>otherwise the classifier's label when it is sure (≥ 0.7) and the event is near that class (open set);</item>
/// <item>the nearest signature within eps (the predicted one for a model label), else a new "Necunoscut #n";</item>
/// <item>runs opened/closed (<see cref="ApplianceRunBuilder"/>), events and runs written in one transaction each.</item>
/// </list>
/// Every night at <see cref="NightlyAt"/> (local) the newest <see cref="SignatureClusterer.MaxEvents"/> events of the last
/// <c>ClusterWindowDays</c> are re-clustered and the classifier retrained, both on a low-priority thread outside the
/// processing lock; events and runs older than <see cref="RetentionDays"/> days are deleted.
/// </summary>
public sealed class NilmService : BackgroundService
{
    /// <summary>
    /// How long an event waits before it is labelled: the ±15 s window plus two 10-second polls, so the plug reading
    /// "steady after" a step late in the window is there.
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
    public const int RetentionDays = 365;
    public const int MaxQueuedSamples = 100_000;
    public const int PageSize = 50;

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

    private readonly MeterEventDetector _detector = new();
    private readonly ConcurrentQueue<LiveSample> _queue = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<MeterEvent> _pending = new();
    private readonly ConcurrentDictionary<long, double> _applianceWh = new();
    private readonly ConcurrentDictionary<Guid, List<PlugPowerPoint>> _plugMemory = new();
    private readonly HashSet<(Guid, DateTimeOffset)> _usedChanges = new();
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
    private NilmSettings _current = new(true, KasaSettingsDefaults.DefaultMinEventDeltaW, KasaSettingsDefaults.DefaultClusterWindowDays, true);
    private DateTimeOffset _lastMarker = DateTimeOffset.MinValue, _lastSummary = DateTimeOffset.MinValue, _lastPlugs = DateTimeOffset.MinValue;
    private DateTimeOffset _nextNightly;
    private long _events;
    private double _unidentifiedBase, _unidentifiedToday;
    private DateOnly _summaryDay;
    private int _queued;

    public NilmService(IServiceScopeFactory scopes, IKasaSettingsService settings, ILiveStreamBroadcaster live, IPlugStateBroadcaster states,
        ILogger<NilmService> log, string modelPath, Func<DateTimeOffset>? clock = null, TimeZoneInfo? tz = null)
    {
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
        if (!_acceptSamples || s.PlugId != _mainId) return;
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
        var ring = _plugMemory.GetOrAdd(s.PlugId, _ => new List<PlugPowerPoint>());
        lock (ring)
        {
            if (ring.Count > 0 && ring[^1].TsUtc >= s.LastSampleUtc) return;
            ring.Add(new PlugPowerPoint(s.LastSampleUtc, s.LastReading.PowerMw / 1000.0));
            var cutoff = s.LastSampleUtc - PlugMemory;
            var old = ring.FindIndex(p => p.TsUtc >= cutoff);
            if (old > 0) ring.RemoveRange(0, old);
        }
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
                nightly = covered >= ReclusterAfterBackfill;
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
        if (nightly) await NightlyAsync(_clock(), ct);
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
        _nextNightly = NextNightly(_clock());
        _initialized = true;
    }

    private void Detect(LiveSample sample)
    {
        foreach (var d in _detector.Add(sample))
            lock (_pending) _pending.Add(d.ToEvent());
    }

    /// <summary>
    /// Every polled plug (role plug) around an event, from the readings kept in memory; those excluded from learning come
    /// with <see cref="PlugSeries.CanLabel"/> = false (they still make an event ambiguous).
    /// </summary>
    private IEnumerable<PlugSeries> LivePlugs(MeterEvent e)
    {
        var from = e.TsUtc - PlugEventMatcher.LoadMargin;
        var to = e.TsUtc + PlugEventMatcher.LoadMargin;
        foreach (var plug in _plugs.Where(p => p.IsPolled && p.Role == PlugRole.Plug))
        {
            if (!_plugMemory.TryGetValue(plug.Id, out var ring)) continue;
            List<PlugPowerPoint> points;
            lock (ring) points = ring.Where(p => p.TsUtc >= from && p.TsUtc <= to).ToList();
            if (points.Count >= 2) yield return new PlugSeries(plug.Id, plug.Alias, points, plug.TeachesNilm);
        }
    }

    /// <summary>The readings of every polled plug in [fromUtc, toUtc] from <c>Samples</c>, loaded once (backfill batch, late events).</summary>
    private async Task<Func<MeterEvent, IEnumerable<PlugSeries>>> StoredPlugsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var plugs = new Dictionary<Guid, (string Alias, List<PlugPowerPoint> Points)>();
        using (var scope = _scopes.CreateScope())
        {
            var sampleRepo = scope.ServiceProvider.GetRequiredService<ISampleRepository>();
            foreach (var plug in _plugs.Where(p => p.IsPolled && p.Role == PlugRole.Plug))
            {
                var rows = await sampleRepo.ListByPlugAsync(plug.Id, fromUtc - PlugEventMatcher.LoadMargin, toUtc + PlugEventMatcher.LoadMargin, ct);
                plugs[plug.Id] = (plug.Alias, rows.Where(r => r.Online && r.PowerMw is not null)
                    .Select(r => new PlugPowerPoint(r.TimestampUtc, r.PowerMw!.Value / 1000.0)).OrderBy(p => p.TsUtc).ToList());
            }
        }
        return e =>
        {
            var lo = e.TsUtc - PlugEventMatcher.LoadMargin;
            var hi = e.TsUtc + PlugEventMatcher.LoadMargin;
            var list = new List<PlugSeries>();
            foreach (var (id, (alias, points)) in plugs)
            {
                var slice = points.Where(p => p.TsUtc >= lo && p.TsUtc <= hi).ToList();
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
        if (plug is null || plug.NilmState != PlugNilmState.Auto || q.Count <= PlugNilmStates.MaxMatchesPerHour) return true;
        using (var scope = _scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPlugRepository>().SetNilmStateAsync(plugId, PlugNilmState.IgnoredAuto, ct);
        _plugs = _plugs.Select(p => p.Id == plugId ? p with { NilmState = PlugNilmState.IgnoredAuto } : p).ToList();
        _log.LogInformation("NILM: plug {Alias} matched {Count} events within an hour: excluded from learning (variable load)", plug.Alias, q.Count);
        return false;
    }

    private async Task ProcessRipeAsync(DateTimeOffset nowUtc, Func<MeterEvent, IEnumerable<PlugSeries>> plugsFor, CancellationToken ct)
    {
        List<MeterEvent> ripe;
        lock (_pending)
        {
            ripe = _pending.Where(e => nowUtc - e.TsUtc >= LabelDelay).OrderBy(e => e.TsUtc).ToList();
            if (ripe.Count == 0) return;
            _pending.RemoveAll(e => nowUtc - e.TsUtc >= LabelDelay);
        }
        using var scope = _scopes.CreateScope();
        await ProcessBatchAsync(scope.ServiceProvider, ripe, plugsFor, ct);
        var newest = ripe[^1].TsUtc;
        _usedChanges.RemoveWhere(k => newest - k.Item2 > TimeSpan.FromMinutes(10));
    }

    /// <summary>Label (plug, then model), signature (predicted, nearest, or new), then one write for the events and one for the runs.</summary>
    private async Task ProcessBatchAsync(IServiceProvider sp, IReadOnlyList<MeterEvent> batch, Func<MeterEvent, IEnumerable<PlugSeries>> plugsFor,
        CancellationToken ct)
    {
        var repo = sp.GetRequiredService<INilmRepository>();
        var prepared = new List<MeterEvent>(batch.Count);
        var created = new List<long>();
        foreach (var raw in batch)
        {
            var e = raw;
            if (PlugEventMatcher.Match(e.TsUtc, e.DeltaP, plugsFor(e), _usedChanges) is { } m && await RegisterMatchAsync(m.PlugId, e.TsUtc, ct))
                e = e with { Label = m.Alias, Source = EventLabelSource.AutoPlug, LabelPlugId = m.PlugId };
            if (e.Source == EventLabelSource.None && _current.MlEnabled && Classifier.Predict(e) is { } p
                && p.Probability >= NilmClassifier.MinProbability && ModelInfo?.NearClass(e, p.Label) == true)
                e = e with { Label = p.Label, Source = EventLabelSource.Model };

            long? sigId = null;
            if (e.Source == EventLabelSource.Model)
                sigId = _signatures.Where(s => string.Equals(s.Name, e.Label, StringComparison.Ordinal))
                    .OrderByDescending(s => s.LabelSource).ThenByDescending(s => s.Count).FirstOrDefault()?.Id;
            sigId ??= SignatureClusterer.Nearest(EventFeatures.Of(e), _signatures.Select(SignatureCentre.Of));
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
        await RefreshSignaturesAsync(repo, stored.Select(e => e.SignatureId!.Value).Distinct().ToList(), ct);
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

    /// <summary>
    /// Recomputes the means of the given signatures (SQL aggregates over the window) and applies the plug rule: when
    /// ≥ 60 % of a signature's events were labelled by the same (not excluded) plug, the signature is measured by that
    /// plug (<see cref="ApplianceSignature.PlugId"/>; it also takes the plug's name unless the user named it).
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
            if (shares.TryGetValue(sig.Id, out var share) && share.TopPlugId is { } plugId
                && SignatureNaming.LinksToPlug(share.TopPlugEvents, share.Events))
            {
                var alias = _plugs.FirstOrDefault(p => p.Id == plugId)?.Alias ?? share.TopLabel ?? sig.Name;
                named = sig.LabelSource == SignatureLabelSource.User
                    ? sig with { PlugId = plugId }
                    : sig with { PlugId = plugId, Name = alias, LabelSource = SignatureLabelSource.Plug };
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

    // ------------------------------------------------------------------ nightly: re-clustering, runs, training, retention

    /// <summary>
    /// Re-clusters the newest <see cref="SignatureClusterer.MaxEvents"/> events of the window, except those in user
    /// signatures (model labels are re-evaluated); clusters keep the id (name, category) of the signature most of their
    /// events already had, noise joins the nearest signature within eps or none. The clustering and the training run on a
    /// low-priority thread outside the processing lock; reading the snapshot and applying the result take the lock.
    /// </summary>
    public async Task NightlyAsync(DateTimeOffset nowUtc, CancellationToken ct)
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

            var pinned = signatures.Values.Where(s => s.LabelSource == SignatureLabelSource.User).Select(s => s.Id).ToHashSet();
            var free = events.Where(e => !(e.SignatureId is { } sid && pinned.Contains(sid))).ToList();
            var features = free.Select(EventFeatures.Of).ToList();
            var labels = await LowPriority.Run(() => SignatureClusterer.Cluster(features), "nilm-cluster");

            await _gate.WaitAsync(ct);
            try
            {
                using var scope = _scopes.CreateScope();
                await ApplyClustersAsync(scope.ServiceProvider, free, labels, nowUtc, ct);
            }
            finally { _gate.Release(); }

            if (_current.MlEnabled) await TrainAsync(nowUtc, ct);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task ApplyClustersAsync(IServiceProvider sp, IReadOnlyList<MeterEvent> snapshot, int[] snapshotLabels, DateTimeOffset nowUtc,
        CancellationToken ct)
    {
        var repo = sp.GetRequiredService<INilmRepository>();
        var signatures = (await repo.ListSignaturesAsync(ct)).ToDictionary(s => s.Id);
        var pinned = signatures.Values.Where(s => s.LabelSource == SignatureLabelSource.User).Select(s => s.Id).ToHashSet();
        // The clustering ran without the lock: events the user labelled or merged meanwhile (now in another or a pinned
        // signature, or deleted) keep what the user did.
        var current = await repo.GetSignatureIdsAsync(snapshot.Select(e => e.Id).ToList(), ct);
        var keep = snapshot.Select((e, i) => (e, i))
            .Where(x => current.TryGetValue(x.e.Id, out var sid) && sid == x.e.SignatureId && !(sid is { } p && pinned.Contains(p)))
            .ToList();
        var free = keep.Select(x => x.e).ToList();
        var labels = keep.Select(x => snapshotLabels[x.i]).ToArray();
        var assignment = new Dictionary<long, long?>();
        var taken = new HashSet<long>();
        var centres = new List<SignatureCentre>();
        foreach (var group in free.Select((e, i) => (e, label: labels[i])).Where(x => x.label >= 0).GroupBy(x => x.label)
                     .OrderByDescending(g => g.Count()))
        {
            var members = group.Select(x => x.e).ToList();
            var reuse = members.Where(m => m.SignatureId is { } sid && signatures.ContainsKey(sid) && !pinned.Contains(sid) && !taken.Contains(sid))
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
            centres.Add(new SignatureCentre(id, mp, mq, mt, members.Any(m => m.DeltaP > 0)));
            foreach (var m in members) assignment[m.Id] = id;
        }
        centres.AddRange(signatures.Values.Where(s => pinned.Contains(s.Id)).Select(SignatureCentre.Of));
        for (var i = 0; i < free.Count; i++)
            if (labels[i] < 0) assignment[free[i].Id] = SignatureClusterer.Nearest(EventFeatures.Of(free[i]), centres);

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

        await RebuildRunsAsync(sp, null, WindowStart(nowUtc), nowUtc, ct);
        var deleted = await repo.DeleteOlderThanAsync(nowUtc.AddDays(-RetentionDays), ct);
        _log.LogInformation("NILM re-clustering: {Events} events, {Clusters} clusters, {Signatures} signatures, {Changed} events moved, {Deleted} old rows deleted",
            free.Count, taken.Count, _signatures.Count, changed.Count, deleted);
        await RefreshSummaryAsync(nowUtc, ct);
    }

    private async Task TrainAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        IReadOnlyList<MeterEvent> labeled;
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            labeled = await scope.ServiceProvider.GetRequiredService<INilmRepository>().ListLabeledEventsAsync(ExcludedPlugs, SignatureClusterer.MaxEvents, ct);
        }
        finally { _gate.Release(); }
        if (!NilmClassifier.CanTrain(labeled))
        {
            _log.LogInformation("NILM classifier: {Labeled} labelled events, not enough to train ({Min} in ≥ {Classes} classes)",
                labeled.Count, NilmClassifier.MinExamples, NilmClassifier.MinClasses);
            return;
        }
        var info = await LowPriority.Run(() => Classifier.Train(labeled, nowUtc), "nilm-train");
        if (info is null) return;
        try { Classifier.Save(_modelPath); }
        catch (Exception ex) { _log.LogWarning("NILM classifier: saving {Path} failed: {Reason}", _modelPath, ex.Message); }
        ModelInfo = info;
        using (var scope = _scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().SetAsync(SettingKeys.NilmModelInfo, JsonSerializer.Serialize(info), ct);
        _log.LogInformation("NILM classifier trained: {Examples} events, {Classes} classes, accuracy {Accuracy:P1} ({Folds}-fold)",
            info.Examples, info.Classes.Count, info.Accuracy, info.Folds);
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

    // ------------------------------------------------------------------ /consumatori

    /// <summary>
    /// The page's data, read in its own scope without waiting for the processing lock (a snapshot: the nightly job or a
    /// backfill never blocks the page). Detected signatures paged by <see cref="PageSize"/>, most events first; events of
    /// the last 24 h paged newest first.
    /// </summary>
    public async Task<NilmOverview> GetOverviewAsync(int page, int eventsPage, CancellationToken ct)
    {
        var now = _clock();
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
        var signatures = await repo.ListSignaturesAsync(ct);
        var midnight = LocalMidnightUtc(now);
        var from30 = now.AddDays(-30);
        var runs = (await repo.ListRunsAsync(from30, now, ct)).ToLookup(r => r.SignatureId);
        var open = _openSnapshot.GroupBy(r => r.SignatureId).ToDictionary(g => g.Key, g => g.Sum(r => r.PowerW));
        SignatureRow Row(ApplianceSignature s) => new(s,
            runs[s.Id].Sum(r => ApplianceEnergy.InRange(r, midnight, now, now)),
            runs[s.Id].Sum(r => ApplianceEnergy.InRange(r, from30, now, now)),
            open.TryGetValue(s.Id, out var w) ? w : null,
            UncertainRuns: runs[s.Id].Count(r => r.EndReason == RunEndReason.Timeout));

        var detected = signatures.Where(s => !s.ConfirmedByPlug).OrderByDescending(s => s.Count).ThenBy(s => s.Id).ToList();
        var pages = Math.Max(1, (detected.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 0, pages - 1);
        var shown = detected.Skip(page * PageSize).Take(PageSize).Select(Row).ToList();
        var plugsNow = _plugs.Count > 0 ? _plugs : await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var possible = (await repo.PlugSharesAsync(shown.Select(r => r.Signature.Id).ToList(), WindowStart(now),
                plugsNow.Where(p => PlugNilmStates.IsIgnored(p.NilmState)).Select(p => p.Id).ToList(), ct))
            .Where(x => x.TopPlugId is not null && x.TopPlugEvents > 0)
            .ToDictionary(x => x.SignatureId, x => plugsNow.FirstOrDefault(p => p.Id == x.TopPlugId)?.Alias ?? x.TopLabel);
        shown = shown.Select(r => possible.TryGetValue(r.Signature.Id, out var alias) ? r with { PossiblePlug = alias } : r).ToList();
        var measured = signatures.Where(s => s.ConfirmedByPlug).OrderByDescending(s => s.Count).Select(Row).ToList();

        var names = signatures.ToDictionary(s => s.Id, s => s.Name);
        var eventsTotal = await repo.CountEventsAsync(now.AddHours(-24), now, ct);
        var eventPages = Math.Max(1, (eventsTotal + PageSize - 1) / PageSize);
        eventsPage = Math.Clamp(eventsPage, 0, eventPages - 1);
        var events = (await repo.ListEventsPageAsync(now.AddHours(-24), now, eventsPage * PageSize, PageSize, ct))
            .Select(e => new EventRow(e, e.SignatureId is { } sid && names.TryGetValue(sid, out var n) ? n : null)).ToList();

        var plugs = _plugs.Count > 0 ? _plugs : await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var (labeled, classes) = await repo.CountLabeledAsync(plugs.Where(p => PlugNilmStates.IsIgnored(p.NilmState)).Select(p => p.Id).ToList(), ct);
        var main = plugs.FirstOrDefault(p => p.Role == PlugRole.MainMeter && !p.IsArchived)?.Alias;
        var plugRows = plugs.Where(p => p.IsPolled && p.Role == PlugRole.Plug).OrderBy(p => p.Alias)
            .Select(p => new PlugNilmRow(p.Id, p.Alias, p.NilmState)).ToList();
        return new NilmOverview(shown, detected.Count, page, pages, measured, signatures, events, eventsTotal, eventsPage, eventPages,
            ModelInfo, labeled, classes, Summary, main, _current, plugRows, _busy);
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
    public Task RenameAsync(long id, string name, CancellationToken ct) => UnderGateAsync<bool>(async (_, repo) =>
    {
        name = SignatureNaming.CleanName(name);
        if (name.Length == 0 || await repo.GetSignatureAsync(id, ct) is not { } s) return false;
        await repo.UpdateSignatureAsync(s with { Name = name, LabelSource = SignatureLabelSource.User }, ct);
        return true;
    }, ct);

    /// <summary>Sets the category (and its icon).</summary>
    public Task SetCategoryAsync(long id, string category, CancellationToken ct) => UnderGateAsync<bool>(async (_, repo) =>
    {
        if (await repo.GetSignatureAsync(id, ct) is not { } s) return false;
        var c = ApplianceCategories.Get(category);
        await repo.UpdateSignatureAsync(s with { Category = c.Key, Icon = c.Icon }, ct);
        return true;
    }, ct);

    /// <summary>Hides a signature: not in the cards and metrics (its energy stays "unidentified").</summary>
    public Task SetHiddenAsync(long id, bool hidden, CancellationToken ct) => UnderGateAsync<bool>(async (_, repo) =>
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

    /// <summary>
    /// Moves every event of <paramref name="fromId"/> into <paramref name="intoId"/> and deletes <paramref name="fromId"/>;
    /// the result is measured by the plug of either (target first).
    /// </summary>
    public Task<bool> MergeAsync(long fromId, long intoId, CancellationToken ct) => UnderGateAsync(async (sp, repo) =>
    {
        if (fromId == intoId || await repo.GetSignatureAsync(fromId, ct) is not { } source || await repo.GetSignatureAsync(intoId, ct) is not { } into)
            return false;
        var moved = await repo.ListEventsBySignaturesAsync(new[] { fromId }, DateTimeOffset.UnixEpoch, ct);
        await repo.SetSignaturesAsync(moved.ToDictionary(e => e.Id, _ => (long?)intoId), ct);
        await repo.DeleteSignatureAsync(fromId, ct);
        await repo.UpdateSignatureAsync(into with
        {
            LabelSource = into.LabelSource == SignatureLabelSource.Cluster ? SignatureLabelSource.User : into.LabelSource,
            PlugId = into.PlugId ?? source.PlugId,
        }, ct);
        _signatures.RemoveAll(s => s.Id == fromId);
        await RefreshSignaturesAsync(repo, new[] { intoId }, ct);
        await RebuildRunsAsync(sp, new[] { intoId }, WindowStart(_clock()), _clock(), ct);
        return true;
    }, ct);

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
    {
        label = SignatureNaming.CleanName(label);
        if (label.Length == 0 || await repo.GetEventAsync(eventId, ct) is not { } e) return false;
        var signatures = await repo.ListSignaturesAsync(ct);
        var current = e.SignatureId is { } cid ? signatures.FirstOrDefault(s => s.Id == cid) : null;
        long sigId;
        if (signatures.FirstOrDefault(s => string.Equals(s.Name, label, StringComparison.Ordinal)) is { } named)
        {
            sigId = named.Id;
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
        return true;
    }, ct);

    public override void Dispose()
    {
        _live.SampleReceived -= OnSample;
        _states.StateChanged -= OnPlugState;
        base.Dispose();
    }
}
