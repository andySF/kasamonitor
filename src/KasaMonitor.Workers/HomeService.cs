using System.Diagnostics;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Costing;
using KasaMonitor.Core.Home;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Workers.Nilm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KasaMonitor.Workers;

/// <summary>
/// Gas of a period on the boiler's days (<see cref="Period"/>: for the current local day, the boiler's current day, as on
/// /centrala since 0.8.1): total, split by purpose, previous period, and per bucket (both purposes).
/// </summary>
public sealed record HomeGas(double? TotalM3, double? HeatingM3, double? DhwM3, double? PreviousTotalM3, double? DeltaPct,
    IReadOnlyList<double?> BucketsM3, IReadOnlyList<double?> PreviousBucketsM3, ReportPeriod? Period = null);

/// <summary>
/// "Azi vs ieri la aceeași oră", both from the SAME source: Σ of the main meter's live minutes of today up to now and of
/// yesterday up to <see cref="SameTimeUtc"/>. <see cref="Partial"/>: one of the two windows has live minutes covering less
/// than <see cref="HouseReportBuilder.MinCoverage"/> of it (a restart, a gap): no Δ then. <see cref="YesterdayKwh"/> =
/// yesterday's whole day (day report rule), for the tooltip; gas from <see cref="GasDayComparison"/> (boiler days).
/// </summary>
public sealed record HomeSameTime(DateTimeOffset SameTimeUtc, double? TodayKwh, double? YesterdaySameTimeKwh, double? YesterdayKwh,
    GasDayComparison? Gas, bool Partial = false)
{
    public double? DeltaPct => Partial ? null : ReportMath.DeltaPct(TodayKwh, YesterdaySameTimeKwh);
}

/// <summary>
/// Everything the home page shows for one period: the house report (bars of the period and of the previous one, totals),
/// the energy flows (plugs, detected, undetected), gas, the "same time yesterday" comparison (current day), the same share
/// of the previous period (other periods in progress) and the typical day profile (day periods).
/// </summary>
/// <param name="PreviousSameFractionKwh">A period in progress (not a day): the previous period up to the same point (<see cref="SameFraction"/>).</param>
/// <param name="PreviousSameFractionGasM3">The same for gas.</param>
/// <param name="MeterFrom">A week / month / year that started before the main meter's first day: that day (the meter, the plugs
/// and the undetected rest then cover only the days from it on).</param>
public sealed record HomePeriod(ReportPeriod Period, DateTimeOffset ComputedUtc, HouseReport? House,
    EnergyFlows? Flows, HomeGas? Gas, HomeSameTime? SameTime, IReadOnlyList<double?>? Profile,
    double? PreviousSameFractionKwh = null, double? PreviousSameFractionGasM3 = null, DateOnly? MeterFrom = null)
{
    public bool InProgress => House?.IsCurrent ?? false;
}

/// <summary>What /metrics exports from the home page: the standby floor and the bill estimate of the current month.</summary>
public sealed record HomeMetricSnapshot(double? StandbyW, BillEstimate? Bill)
{
    public static readonly HomeMetricSnapshot Empty = new(null, null);
}

/// <summary>
/// The data of the home page „Acasă” (/): period aggregates from the existing reports (live minute aggregates grouped by
/// hour, daily rows, the boiler's daily history; never <c>LiveSamples</c>), the detected appliances' energy, the standby floor
/// of the rest of the house (<see cref="StandbyRule"/>, hourly) and the typical day (<see cref="TypicalProfile"/>, hourly).
/// Each period is computed once on the thread pool (never on a circuit's thread: SQLite runs the "async" queries
/// synchronously) and shared by every circuit: concurrent callers share the computation in progress. A value is fresh for
/// <see cref="Ttl"/> (the current day 1 minute, another period in progress 10 minutes, a past period until the local day
/// changes); an expired value is still served while one recomputation runs (stale-while-revalidate). At most
/// <see cref="MaxCachedPeriods"/> periods are kept (the oldest go first). The background loop keeps the current day (and
/// every 10 minutes the month) warm and feeds <see cref="Metrics"/>; each computation is logged at Debug with its duration.
/// </summary>
public sealed class HomeService : BackgroundService
{
    public static readonly TimeSpan CurrentDayFor = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan InProgressFor = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan StandbyEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan ProfileEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    /// <summary>The first computation waits this long after start, so the workers have published their first states.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(20);
    /// <summary>A NILM failure is logged as a warning at most this often (the detected energy then lands in „Nedetectat”).</summary>
    public static readonly TimeSpan NilmWarningEvery = TimeSpan.FromHours(1);
    public const int MaxCachedPeriods = 32;

    /// <summary>One cached period: when its computation started, the local day it belongs to, the value, a refresh in progress.</summary>
    private sealed class Entry
    {
        public required DateTimeOffset At { get; init; }
        public required DateOnly Day { get; init; }
        public required Task<HomePeriod> Task { get; init; }
        /// <summary>The refresh in progress, including its swap into the cache.</summary>
        public Task? Refresh { get; set; }
    }

    /// <summary>An hourly value (standby, profile) published as one reference, so a reader never sees half of an update.</summary>
    private sealed record Cached<T>(DateTimeOffset At, DateOnly Day, T Value);

    private readonly IServiceScopeFactory _scopes;
    private readonly EnergyReportService _reports;
    private readonly HeatingState? _heating;
    private readonly IHeatingSettingsService? _heatingSettings;
    private readonly IKasaSettingsService _kasaSettings;
    private readonly Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<ApplianceEnergyRow>>>? _detected;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;
    private readonly object _cacheLock = new();
    private readonly Dictionary<ReportPeriod, Entry> _cache = new();
    private readonly SemaphoreSlim _standbyGate = new(1, 1);
    private readonly SemaphoreSlim _profileGate = new(1, 1);
    private Cached<StandbyFloor?>? _standby;
    private Cached<IReadOnlyList<double?>>? _profile;
    private DateTimeOffset _lastNilmWarning = DateTimeOffset.MinValue;

    public HomeService(IServiceScopeFactory scopes, EnergyReportService reports, IKasaSettingsService kasaSettings,
        HeatingState? heating = null, IHeatingSettingsService? heatingSettings = null, NilmService? nilm = null,
        ILogger<HomeService>? log = null, Func<DateTimeOffset>? clock = null,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<ApplianceEnergyRow>>>? detectedEnergy = null)
    {
        _scopes = scopes;
        _reports = reports;
        _kasaSettings = kasaSettings;
        _heating = heating;
        _heatingSettings = heatingSettings;
        _detected = detectedEnergy ?? (nilm is null ? null : nilm.GetDetectedEnergyAsync);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public TimeZoneInfo TimeZone => _reports.TimeZone;

    public DateOnly Today() => _reports.Today();

    /// <summary>The standby floor last computed (null before the first computation or without data).</summary>
    public StandbyFloor? Standby => Volatile.Read(ref _standby)?.Value;

    public HomeMetricSnapshot Metrics { get; private set; } = HomeMetricSnapshot.Empty;

    /// <summary>How long a computed period is kept: current day 1 min, other period in progress 10 min, past: until the day changes.</summary>
    public TimeSpan Ttl(ReportPeriod period, DateTimeOffset nowUtc)
    {
        var today = LocalClock.Day(nowUtc, TimeZone);
        if (!period.Contains(today)) return TimeSpan.MaxValue;
        return period.Kind == ReportGranularity.Day ? CurrentDayFor : InProgressFor;
    }

    /// <summary>
    /// The data of a period: the cached value while fresh (<see cref="Ttl"/>, same local day); an expired one while a single
    /// refresh runs in the background; otherwise one computation shared by every caller.
    /// </summary>
    public Task<HomePeriod> GetAsync(ReportPeriod period, CancellationToken ct)
    {
        Task<HomePeriod> result;
        lock (_cacheLock)
        {
            var now = _clock();
            var today = LocalClock.Day(now, TimeZone);
            if (_cache.TryGetValue(period, out var e) && e.Day == today && !e.Task.IsFaulted && !e.Task.IsCanceled)
            {
                if (now - e.At >= Ttl(period, now) && e.Task.IsCompletedSuccessfully && e.Refresh is null)
                {
                    var refresh = Start(period, now);
                    e.Refresh = refresh.ContinueWith(t => Refreshed(period, e, t, now, today), TaskScheduler.Default);
                }
                result = e.Task;
            }
            else
            {
                result = Start(period, now);
                _cache[period] = new Entry { At = now, Day = today, Task = result };
                Evict(today);
            }
        }
        return ct.CanBeCanceled ? result.WaitAsync(ct) : result;
    }

    /// <summary>The refresh in progress of a period (completed when none): tests and diagnostics.</summary>
    public Task RefreshingAsync(ReportPeriod period)
    {
        lock (_cacheLock)
            return _cache.TryGetValue(period, out var e) && e.Refresh is { } r ? r : Task.CompletedTask;
    }

    private Task<HomePeriod> Start(ReportPeriod period, DateTimeOffset now) =>
        Task.Run(() => ComputeTimedAsync(period, now, CancellationToken.None));

    private void Refreshed(ReportPeriod period, Entry old, Task<HomePeriod> refresh, DateTimeOffset startedAt, DateOnly day)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(period, out var cur) || !ReferenceEquals(cur, old)) return;
            if (refresh.IsCompletedSuccessfully) _cache[period] = new Entry { At = startedAt, Day = day, Task = refresh };
            else
            {
                old.Refresh = null;   // the old value stays; the next call tries again
                _log.LogWarning(refresh.Exception?.GetBaseException(), "Home page data of {Kind} {Start} could not be refreshed", period.Kind, period.Start);
            }
        }
    }

    /// <summary>Under the cache lock: drops the periods of another day, then the oldest beyond <see cref="MaxCachedPeriods"/>.</summary>
    private void Evict(DateOnly today)
    {
        foreach (var key in _cache.Where(kv => kv.Value.Day != today).Select(kv => kv.Key).ToList()) _cache.Remove(key);
        foreach (var key in _cache.OrderBy(kv => kv.Value.At).Take(Math.Max(0, _cache.Count - MaxCachedPeriods)).Select(kv => kv.Key).ToList())
            _cache.Remove(key);
    }

    /// <summary>The standby floor: recomputed at most once per <see cref="StandbyEvery"/>.</summary>
    public async Task<StandbyFloor?> GetStandbyAsync(CancellationToken ct)
    {
        var now = _clock();
        if (Volatile.Read(ref _standby) is { } s && now - s.At < StandbyEvery) return s.Value;
        await _standbyGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _standby) is { } s2 && now - s2.At < StandbyEvery) return s2.Value;
            var value = await Task.Run(() => ComputeStandbyAsync(now, ct), ct);
            Volatile.Write(ref _standby, new Cached<StandbyFloor?>(now, LocalClock.Day(now, TimeZone), value));
            return value;
        }
        finally
        {
            _standbyGate.Release();
        }
    }

    /// <summary>
    /// The value of <c>kasa_house_standby_w</c>: the floor, or 0 when it is at most <see cref="NowBreakdownRule.MinStandbyW"/>
    /// (not measurable: at night the plugs cover the meter); null before the first night.
    /// </summary>
    public static double? StandbyMetric(StandbyFloor? floor) =>
        floor is null ? null : floor.Watts > NowBreakdownRule.MinStandbyW ? floor.Watts : 0;

    /// <summary>The bill estimate of the current month (electricity at the tariff of /settings, gas at RON/kWh × kWh/m³).</summary>
    public async Task<BillEstimate?> GetBillAsync(CancellationToken ct)
    {
        var month = _reports.Current(ReportGranularity.Month);
        var data = await GetAsync(month, ct);
        var now = _clock();
        var el = BillEstimateRule.Project(data.House?.HouseTotalKwh, month, now, TimeZone);
        var gas = BillEstimateRule.Project(data.Gas?.TotalM3, month, now, TimeZone);
        if (el is null && gas is null) return null;
        var tariff = (await _kasaSettings.GetAsync(ct)).TariffRonPerKwh;
        decimal? gasRon = null;
        if (gas is not null && _heatingSettings is not null)
        {
            var hs = await _heatingSettings.GetAsync(ct);
            gasRon = GasCost.Ron(gas.Projected, hs.GasKwhPerM3, hs.GasPricePerKwh);
        }
        return new BillEstimate(el, el is null ? null : ElectricityCost.Ron(el.Projected, tariff), gas, gasRon,
            GasExpected: _heating?.Current is not null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the workers publish their first states before the first computation.
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Home page data could not be computed; next try in {Interval}", Interval);
            }
            try { await Task.Delay(Interval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One refresh of the background loop: standby, the current day, the month (within its TTL), the metrics.</summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        var standby = await GetStandbyAsync(ct);
        await GetAsync(_reports.Current(ReportGranularity.Day), ct);
        var bill = await GetBillAsync(ct);
        Metrics = new HomeMetricSnapshot(StandbyMetric(standby), bill);
    }

    private async Task<HomePeriod> ComputeTimedAsync(ReportPeriod period, DateTimeOffset now, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await ComputeAsync(period, now, ct);
        }
        finally
        {
            _log.LogDebug("Home page data of {Kind} {Start} computed in {Ms} ms", period.Kind, period.Start, sw.ElapsedMilliseconds);
        }
    }

    private async Task<HomePeriod> ComputeAsync(ReportPeriod period, DateTimeOffset now, CancellationToken ct)
    {
        var tz = TimeZone;
        var localToday = LocalClock.Day(now, tz);
        var house = await _reports.GetHouseReportAsync(period, ct);

        EnergyFlows? flows = null;
        if (house?.HouseTotalKwh is { } meterKwh)
        {
            var plugs = (house.PlugTotals ?? Array.Empty<SubMeterTotal>())
                .Select(p => new FlowItem(p.PlugId.ToString(), p.Alias, FlowKind.Plug, p.Kwh ?? 0, null, "/history?plugId=" + p.PlugId))
                .ToList();
            IReadOnlyList<ApplianceEnergyRow> detected = Array.Empty<ApplianceEnergyRow>();
            if (_detected is not null)
            {
                try { detected = await _detected(period.StartUtc(tz), period.EndUtc(tz), ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (now - _lastNilmWarning >= NilmWarningEvery)
                    {
                        _lastNilmWarning = now;
                        _log.LogWarning(ex, "Detected appliances' energy not available: it is counted in „Nedetectat” (logged at most once an hour)");
                    }
                }
            }
            flows = EnergyFlowRule.Build(meterKwh, plugs,
                detected.Select(d => new FlowItem("sig-" + d.SignatureId, d.Name, FlowKind.Detected, d.Wh / 1000.0, "detectat, fără priză", "/consumatori")));
        }

        // Gas follows the boiler's days (BoilerClock, 0.8.1): the current local day is the boiler's current day (which starts
        // at 01:00 Romanian time), so between 00:00 and 01:00 „Azi” still shows the boiler day that has not ended.
        HomeGas? gas = null;
        GasDayComparison? gasDay = null;
        if (_heating?.Current?.Device is { } device)
        {
            var boilerToday = _reports.BoilerToday();
            var gasPeriod = period.Kind == ReportGranularity.Day && period.Start == localToday
                ? ReportPeriod.Containing(ReportGranularity.Day, boilerToday)
                : period;
            var hr = await _reports.GetHeatingReportAsync(device.Id, gasPeriod, ct);
            var hp = await _reports.GetHeatingReportAsync(device.Id, gasPeriod.Previous(), ct);
            gas = new HomeGas(hr.Gas.Total, SeriesSum(hr.Gas, "heating"), SeriesSum(hr.Gas, "dhw"), hr.Gas.PreviousTotal, hr.Gas.DeltaPct,
                Buckets(hr.Gas, hr.Buckets.Count), Buckets(hp.Gas, hp.Buckets.Count), gasPeriod);
            if (gasPeriod.Kind == ReportGranularity.Day && gasPeriod.Start == boilerToday)
                gasDay = await _reports.GetGasTodayVsYesterdayAsync(device.Id, ct);
        }

        HomeSameTime? same = null;
        IReadOnlyList<double?>? profile = null;
        double? prevFractionKwh = null, prevFractionGas = null;
        if (period.Kind == ReportGranularity.Day)
        {
            profile = await GetProfileAsync(ct);
            if (period.Start == localToday && house is not null)
                same = await SameTimeAsync(period, now, house, gasDay, ct);
        }
        else if (house is { IsCurrent: true, PreviousHouseKwh: { } previous } && SameFraction.Position(house.Buckets, now) is { } pos)
        {
            prevFractionKwh = SameFraction.Previous(previous, pos);
            if (gas is not null) prevFractionGas = SameFraction.Previous(gas.PreviousBucketsM3, pos);
        }
        DateOnly? meterFrom = period.Kind != ReportGranularity.Day && house?.FirstHouseDay is { } first && first > period.Start && first <= period.LastDay
            ? first : null;
        return new HomePeriod(period, now, house, flows, gas, same, profile, prevFractionKwh, prevFractionGas, meterFrom);
    }

    /// <summary>
    /// Today and yesterday up to the same local time, both as Σ of the main meter's live minutes, with their coverage. Both
    /// windows end at the start of the current minute (today's minute in progress has no aggregate yet: cutting yesterday at
    /// the exact second would give yesterday one minute more).
    /// </summary>
    private async Task<HomeSameTime> SameTimeAsync(ReportPeriod today, DateTimeOffset nowUtc, HouseReport house, GasDayComparison? gasDay, CancellationToken ct)
    {
        var tz = TimeZone;
        var now = KasaMonitor.Core.Metering.LiveMinuteAggregator.MinuteOf(nowUtc);
        var sameUtc = LocalClock.YesterdaySameTimeUtc(now, tz);
        var yesterday = today.Previous();
        var todayStart = today.StartUtc(tz);
        var yesterdayStart = yesterday.StartUtc(tz);
        double? todayKwh = null, yKwh = null;
        var partial = true;
        if (await MainIdAsync(ct) is { } mainId)
        {
            IReadOnlyList<LiveMinuteAggregate> minutes;
            using (var scope = _scopes.CreateScope())
                minutes = await scope.ServiceProvider.GetRequiredService<ILiveRepository>().ListMinutesAsync(mainId, yesterdayStart, now, ct);
            // Only the minutes that started before the end of each window (the repository's range includes its end).
            var y = minutes.Where(m => m.MinuteUtc >= yesterdayStart && m.MinuteUtc < sameUtc).ToList();
            var t = minutes.Where(m => m.MinuteUtc >= todayStart && m.MinuteUtc < now).ToList();
            if (t.Count > 0) todayKwh = t.Sum(m => m.EnergyWh) / 1000.0;
            if (y.Count > 0) yKwh = y.Sum(m => m.EnergyWh) / 1000.0;
            partial = !Covered(t, todayStart, now) || !Covered(y, yesterdayStart, sameUtc);
        }
        return new HomeSameTime(sameUtc, todayKwh, yKwh, house.PreviousHouseTotalKwh, gasDay, partial);
    }

    /// <summary>A window shorter than one minute (just after midnight) has nothing to cover.</summary>
    private const double MinCoverageWindowSeconds = 60;

    /// <summary>The live minutes cover at least <see cref="HouseReportBuilder.MinCoverage"/> of [from, to) (by covered seconds).</summary>
    private static bool Covered(IReadOnlyList<LiveMinuteAggregate> minutes, DateTimeOffset from, DateTimeOffset to)
    {
        var expected = (to - from).TotalSeconds;
        if (expected < MinCoverageWindowSeconds) return true;
        return minutes.Sum(m => Math.Min(60, m.CoveredSeconds)) >= HouseReportBuilder.MinCoverage * expected;
    }

    private static double? SeriesSum(HeatingKindReport r, string key) =>
        r.Series.FirstOrDefault(s => s.Key == key) is { } s ? ReportMath.SumOrNull(s.Values) : null;

    private static IReadOnlyList<double?> Buckets(HeatingKindReport r, int count) =>
        Enumerable.Range(0, count).Select(i => ReportMath.SumOrNull(r.Series.Select(s => i < s.Values.Count ? s.Values[i] : null))).ToList();

    private async Task<Guid?> MainIdAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct))
            .FirstOrDefault(p => p.IsPolled && p.Role == PlugRole.MainMeter)?.Id;
    }

    private async Task<IReadOnlyList<double?>?> GetProfileAsync(CancellationToken ct)
    {
        var now = _clock();
        var today = LocalClock.Day(now, TimeZone);
        if (Volatile.Read(ref _profile) is { } p && p.Day == today && now - p.At < ProfileEvery) return p.Value;
        await _profileGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _profile) is { } p2 && p2.Day == today && now - p2.At < ProfileEvery) return p2.Value;
            if (await MainIdAsync(ct) is not { } mainId) return null;
            var to = ReportPeriod.Containing(ReportGranularity.Day, today).StartUtc(TimeZone);
            var from = ReportPeriod.Containing(ReportGranularity.Day, today.AddDays(-TypicalProfile.Days)).StartUtc(TimeZone);
            IReadOnlyList<PlugHourWh> hours;
            using (var scope = _scopes.CreateScope())
                hours = await scope.ServiceProvider.GetRequiredService<ILiveRepository>().SumEnergyByHourAsync(new[] { mainId }, from, to, ct);
            var value = TypicalProfile.Build(hours, TimeZone);
            Volatile.Write(ref _profile, new Cached<IReadOnlyList<double?>>(now, today, value));
            return value;
        }
        finally
        {
            _profileGate.Release();
        }
    }

    /// <summary>
    /// The standby floor (<see cref="StandbyRule"/>): one query of the live minutes of the main meter and the streamed plugs
    /// per night window, and one query of the polled means of the plugs without a live stream per night window.
    /// </summary>
    private async Task<StandbyFloor?> ComputeStandbyAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var all = (await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct)).Where(p => p.IsPolled).ToList();
        var main = all.FirstOrDefault(p => p.Role == PlugRole.MainMeter);
        if (main is null) return null;
        var counted = all.Where(p => p.CountsInTotals).ToList();
        var streamed = counted.Where(p => p.StreamEnabled).Select(p => p.Id).ToList();
        var polledOnly = counted.Where(p => !p.StreamEnabled).Select(p => p.Id).ToList();
        var live = scope.ServiceProvider.GetRequiredService<ILiveRepository>();
        var samples = scope.ServiceProvider.GetRequiredService<ISampleRepository>();
        var windows = StandbyRule.NightWindows(now, TimeZone);
        var minutes = new List<LiveMinuteAggregate>();
        var others = new double?[windows.Count];
        for (var i = 0; i < windows.Count; i++)
        {
            var (from, to) = windows[i];
            minutes.AddRange(await live.ListMinutesAsync(streamed.Prepend(main.Id).ToList(), from, to, ct));
            if (polledOnly.Count == 0) { others[i] = 0; continue; }
            var means = await samples.AveragePowerByPlugAsync(polledOnly, from, to, ct);
            others[i] = polledOnly.All(means.ContainsKey) ? polledOnly.Sum(id => means[id]) : null;
        }
        return StandbyRule.Compute(minutes, main.Id, streamed, windows, i => others[i]);
    }
}
