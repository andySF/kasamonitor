using System.Diagnostics;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Core.Reports;
using KasaMonitor.Workers.Nilm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KasaMonitor.Workers;

/// <summary>
/// One local day of the burner timeline (≤ <see cref="HistoryService.MaxRawDays"/> days): the runs that overlap it (cut to
/// the day) and the minutes of burning per purpose.
/// </summary>
public sealed record BurnerDayBand(DateOnly Day, DateTimeOffset StartUtc, DateTimeOffset EndUtc, IReadOnlyList<BurnerSegment> Segments,
    double HeatingMinutes, double DhwMinutes, double UnknownMinutes);

/// <summary>
/// „Gaz și centrală” of /history: the boiler report of the period (gas, boiler electricity, heat, on the boiler's days),
/// burner hours and starts per bar, the mean gas per local hour of the day (periods of 2 to <see cref="HistoryService.MaxRawDays"/>
/// days) and the burner timeline per day (≤ <see cref="HistoryService.MaxRawDays"/> days). <see cref="Period"/>: the boiler
/// period used (for the current local day, the boiler's current day). <see cref="HeatingM3"/> / <see cref="DhwM3"/>: the split
/// of the period (a day: its stored row when there is one, its hours otherwise; else the bars).
/// </summary>
public sealed record HistoryGas(ReportPeriod Period, string DeviceName, HeatingReport Report, IReadOnlyList<BurnerBucket> Burner,
    IReadOnlyList<double?>? HourProfileM3, IReadOnlyList<BurnerDayBand>? Timeline, double? HeatingM3 = null, double? DhwM3 = null)
{
    public double? BurnerHours => ReportMath.SumOrNull(Burner.Select(b => b.Hours));
    public long? BurnerStarts => Burner.Any(b => b.Starts is not null) ? Burner.Sum(b => b.Starts ?? 0) : null;
}

/// <summary>„Aparate (Miele)” of /history: the cycles that started in the period (newest first) and the per-program statistics.</summary>
public sealed record HistoryMiele(IReadOnlyList<MieleCycleRow> Cycles, IReadOnlyList<MieleProgramRow> Programs, bool HasAppliances);

/// <summary>
/// „Consumatori” of /history (and the summary of /consumatori): the detected appliances (without a plug) of the period and
/// the breakdown of the meter by how well each part is known (<see cref="ConsumerBreakdownRule"/>: identified consumers,
/// other plugs, the anonymous signatures grouped, „Nedetectat”; per bar). <see cref="Enabled"/>: the detection runs.
/// </summary>
public sealed record HistoryDetected(IReadOnlyList<DetectedApplianceRow> Rows, bool Enabled, ConsumerBreakdown Breakdown, IReadOnlyList<ReportBucket> Buckets);

/// <summary>One sensor of the climate section: its stats per bar and over the whole period.</summary>
public sealed record HistoryClimateSensor(ClimateSensor Sensor, IReadOnlyList<ClimateStats> Buckets, ClimateStats Period);

/// <summary>
/// „Climă” of /history: per sensor min / mean / max per bar (hours of a day, days, months of a year), the heating degree
/// days per bar (outdoor sensor, <see cref="Rule"/>, finished days only; today's value so far in <see cref="PartialDegreeDays"/>,
/// never added) and the energy signature (heating gas kWh/day ~ degree days/day) over
/// <see cref="SignaturePeriod"/> (the period itself when it has at least 7 days, else the 30 days ending with it).
/// </summary>
public sealed record HistoryClimate(IReadOnlyList<ReportBucket> Buckets, IReadOnlyList<HistoryClimateSensor> Sensors, ClimateSensor? Outdoor,
    IReadOnlyList<double?> DegreeDays, double? DegreeDaysTotal, DegreeDayRule Rule, EnergySignature? Signature, ReportPeriod SignaturePeriod,
    DateOnly? FirstReadingDay, DateOnly? PartialDay = null, double? PartialDegreeDays = null);

/// <summary>„Evenimente” of /history, newest first. <see cref="Truncated"/>: some source had more than <see cref="HistoryService.MaxEventsPerSource"/>.</summary>
public sealed record HistoryEventList(IReadOnlyList<HistoryEvent> Events, bool Truncated);

/// <summary>
/// The sections of /history other than electricity (which comes from <see cref="HomeService"/>, already cached and shared
/// with the home page): gas and boiler, Miele, detected appliances, climate, events. Only aggregates and bounded raw reads:
/// never <c>LiveSamples</c>; boiler and climate raw readings only for what needs them (hours of the burner counters are
/// grouped in SQL; the burner timeline and the hourly gas profile only up to <see cref="MaxRawDays"/> days). Every value
/// goes through a <see cref="PeriodCache{TKey,TValue}"/> (one computation shared by every circuit and the CSV export,
/// stale-while-revalidate, 10 minutes for a period in progress) and each computation is logged at Debug with its duration.
/// </summary>
public sealed class HistoryService
{
    /// <summary>Periods up to this many days get the burner timeline and the hourly gas profile (raw boiler readings).</summary>
    public const int MaxRawDays = 31;
    public const int MaxEventsPerSource = 500;
    public const int MaxCachedValues = 64;
    /// <summary>A period shorter than this gets the energy signature of the 30 days ending with it.</summary>
    public const int SignatureFallbackDays = 30;

    private readonly IServiceScopeFactory _scopes;
    private readonly EnergyReportService _reports;
    private readonly IClimateDailyStats _climateDaily;
    private readonly IClimateSettingsService? _climateSettings;
    private readonly IHeatingSettingsService? _heatingSettings;
    private readonly HeatingState? _heating;
    private readonly DetectedRunsProvider? _detectedRuns;
    private readonly IKasaSettingsService? _kasaSettings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;
    private readonly PeriodCache<(string Section, ReportPeriod Period, double Variant), object> _cache;

    public HistoryService(IServiceScopeFactory scopes, EnergyReportService reports, IClimateDailyStats climateDaily,
        IClimateSettingsService? climateSettings = null, IHeatingSettingsService? heatingSettings = null, HeatingState? heating = null,
        DetectedRunsProvider? detectedRuns = null, IKasaSettingsService? kasaSettings = null, ILogger<HistoryService>? log = null, Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _reports = reports;
        _climateDaily = climateDaily;
        _climateSettings = climateSettings;
        _heatingSettings = heatingSettings;
        _heating = heating;
        _detectedRuns = detectedRuns;
        _kasaSettings = kasaSettings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? (ILogger)NullLogger.Instance;
        _cache = new PeriodCache<(string, ReportPeriod, double), object>(_clock, () => _reports.TimeZone, MaxCachedValues, _log, "History section");
    }

    public TimeZoneInfo TimeZone => _reports.TimeZone;

    public DateOnly Today() => _reports.Today();

    /// <summary>The last computation time per section (ms), for the log and the report.</summary>
    public IReadOnlyDictionary<string, long> LastDurations
    {
        get { lock (_durations) return new Dictionary<string, long>(_durations); }
    }

    private readonly Dictionary<string, long> _durations = new();

    private async Task<T> Cached<T>(string section, ReportPeriod period, double variant, Func<DateTimeOffset, Task<T>> compute, CancellationToken ct)
        where T : class =>
        (T)await _cache.GetAsync((section, period, variant), period, async now =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                return await compute(now);
            }
            finally
            {
                lock (_durations) _durations[section] = sw.ElapsedMilliseconds;
                _log.LogDebug("History {Section} of {Kind} {Start} computed in {Ms} ms", section, period.Kind, period.Start, sw.ElapsedMilliseconds);
            }
        }, ct);

    // ------------------------------------------------------------------ gas and boiler

    /// <summary>The gas and boiler section, or null without a boiler.</summary>
    public async Task<HistoryGas?> GetGasAsync(ReportPeriod period, CancellationToken ct)
    {
        var result = await Cached<object>("gas", period, 0, async now => await ComputeGasAsync(period, now, None) ?? (object)NoValue.Instance, ct);
        return result as HistoryGas;
    }

    // A computation is shared by every caller (PeriodCache): no caller's token cancels it.
    private static readonly CancellationToken None = CancellationToken.None;

    private sealed class NoValue
    {
        public static readonly NoValue Instance = new();
    }

    /// <summary>The boiler of the history: the one being read, else the first stored (its history stays when the worker is off).</summary>
    private async Task<HeatingDevice?> DeviceAsync(CancellationToken ct)
    {
        if (_heating?.Current?.Device is { } d) return d;
        using var scope = _scopes.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IHeatingRepository>().ListDevicesAsync(ct)).FirstOrDefault();
    }

    /// <summary>The boiler period of a local period: the current local day is the boiler's current day (as on the home page, 0.8.1).</summary>
    public ReportPeriod BoilerPeriod(ReportPeriod period) =>
        period.Kind == ReportGranularity.Day && period.Start == _reports.Today()
            ? ReportPeriod.Containing(ReportGranularity.Day, _reports.BoilerToday())
            : period;

    private async Task<HistoryGas?> ComputeGasAsync(ReportPeriod period, DateTimeOffset now, CancellationToken ct)
    {
        if (await DeviceAsync(ct) is not { } device) return null;
        var gasPeriod = BoilerPeriod(period);
        var report = await _reports.GetHeatingReportAsync(device.Id, gasPeriod, ct);
        var boilerTz = _reports.BoilerTimeZone;
        var from = gasPeriod.StartUtc(boilerTz);
        var to = gasPeriod.EndUtc(boilerTz);

        IReadOnlyList<BurnerCounterHour> counters;
        IReadOnlyList<HeatingReading>? raw = null;
        IReadOnlyList<HeatingCounterReading>? gasReadings = null;
        using (var scope = _scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IHeatingRepository>();
            counters = await repo.ListBurnerCounterHoursAsync(device.Id, from - EnergyReportService.ReadingLookBack, to, ct);
            if (gasPeriod.DayCount <= MaxRawDays)
            {
                // The local days of the timeline: the period's own days in the application's zone.
                raw = await repo.ListReadingsAsync(device.Id, period.StartUtc(TimeZone) - EnergyReportService.ReadingLookBack, period.EndUtc(TimeZone), ct);
                if (gasPeriod.Kind != ReportGranularity.Day)
                    gasReadings = await repo.ListCounterReadingsAsync(device.Id, from - EnergyReportService.ReadingLookBack, to, ct);
            }
        }
        var burner = BurnerCounters.PerBucket(counters, report.Buckets);
        double? heatingM3 = ReportMath.SumOrNull(Series(report, HeatingReportBuilder.SeriesHeating));
        double? dhwM3 = ReportMath.SumOrNull(Series(report, HeatingReportBuilder.SeriesDhw));
        if (gasPeriod.Kind == ReportGranularity.Day)
        {
            // The hours of a day may be incomplete (no counters before 0.8.0, a gap): the stored day holds the split.
            using var scope = _scopes.CreateScope();
            var today = _reports.BoilerToday();
            if ((await scope.ServiceProvider.GetRequiredService<IHeatingRepository>().ListDailyAsync(device.Id, gasPeriod.Start, gasPeriod.Start, ct))
                .FirstOrDefault() is { } row)
                (heatingM3, dhwM3) = (row.GasHeatingFor(today), row.GasDhwFor(today));
        }
        var profile = gasReadings is null ? null : HourProfile(gasReadings, from, to, boilerTz, now);
        var timeline = raw is null ? null : Timeline(raw, period, now);
        return new HistoryGas(gasPeriod, device.Name, report, burner, profile, timeline, heatingM3, dhwM3);
    }

    private static IReadOnlyList<double?> Series(HeatingReport r, string key) =>
        r.Gas.Series.FirstOrDefault(s => s.Key == key)?.Values ?? Array.Empty<double?>();

    /// <summary>
    /// Mean gas (m³, both purposes) per local hour of the day over the hours of [from, to) that have a pair of readings
    /// (<see cref="HeatingReportBuilder.HourlyIncrements"/>), up to now.
    /// </summary>
    public IReadOnlyList<double?> HourProfile(IReadOnlyList<HeatingCounterReading> readings, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo boilerTz,
        DateTimeOffset now)
    {
        var end = to < now ? to : now;
        var hours = new List<ReportBucket>();
        for (var t = from; t < end; t = t.AddHours(1))
            hours.Add(new ReportBucket(hours.Count, t, t.AddHours(1), TimeZoneInfo.ConvertTime(t, TimeZone).DateTime, DateOnly.FromDateTime(t.UtcDateTime)));
        var heating = HeatingReportBuilder.HourlyIncrements(readings.Select(r => (r.TsUtc, r.GasHeatingM3)), hours, boilerTz);
        var dhw = HeatingReportBuilder.HourlyIncrements(readings.Select(r => (r.TsUtc, r.GasDhwM3)), hours, boilerTz);
        var sum = new double[24];
        var count = new int[24];
        for (var i = 0; i < hours.Count; i++)
        {
            if (ReportMath.SumOrNull(new[] { heating[i], dhw[i] }) is not { } v) continue;
            var h = hours[i].LocalStart.Hour;
            sum[h] += v;
            count[h]++;
        }
        return Enumerable.Range(0, 24).Select(h => count[h] == 0 ? (double?)null : sum[h] / count[h]).ToList();
    }

    /// <summary>The burner runs (<see cref="BurnerTimeline"/>) cut into the period's local days, with the minutes per purpose.</summary>
    public IReadOnlyList<BurnerDayBand> Timeline(IReadOnlyList<HeatingReading> readings, ReportPeriod period, DateTimeOffset now)
    {
        var segments = BurnerTimeline.Build(readings);
        var bands = new List<BurnerDayBand>();
        for (var d = period.Start; d < period.EndExclusive; d = d.AddDays(1))
        {
            var day = ReportPeriod.Containing(ReportGranularity.Day, d);
            var s = day.StartUtc(TimeZone);
            var e = day.EndUtc(TimeZone);
            if (s > now) break;
            var cut = segments.Where(x => x.EndUtc > s && x.StartUtc < e)
                .Select(x => x with { StartUtc = x.StartUtc < s ? s : x.StartUtc, EndUtc = x.EndUtc > e ? e : x.EndUtc })
                .ToList();
            double Minutes(BurnerPurpose p) => cut.Where(x => x.Purpose == p).Sum(x => x.Duration.TotalMinutes);
            bands.Add(new BurnerDayBand(d, s, e, cut, Minutes(BurnerPurpose.Heating), Minutes(BurnerPurpose.Dhw), Minutes(BurnerPurpose.Unknown)));
        }
        return bands;
    }

    // ------------------------------------------------------------------ Miele

    public Task<HistoryMiele> GetMieleAsync(ReportPeriod period, CancellationToken ct) =>
        Cached("miele", period, 0, _ => ComputeMieleAsync(period, None), ct);

    private async Task<HistoryMiele> ComputeMieleAsync(ReportPeriod period, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMieleRepository>();
        var devices = await repo.ListDevicesAsync(includeArchived: true, ct);
        var from = period.StartUtc(TimeZone);
        var to = period.EndUtc(TimeZone);
        var rows = new List<MieleCycleRow>();
        foreach (var d in devices)
        {
            var name = string.IsNullOrWhiteSpace(d.Alias) ? d.TypeText : d.Alias;
            rows.AddRange((await repo.ListCyclesBetweenAsync(d.Id, from, to, ct))
                .Where(c => c.StartUtc < to)
                .Select(c => MieleCycleRow.From(c, name)));
        }
        rows.Sort((a, b) => b.Cycle.StartUtc.CompareTo(a.Cycle.StartUtc));
        return new HistoryMiele(rows, MieleProgramStats.ByProgram(rows), devices.Count > 0);
    }

    // ------------------------------------------------------------------ detected appliances

    /// <summary>
    /// The consumers of a period. The cache key holds a fingerprint of the labels (signatures: name, label source, plug,
    /// hidden; plugs: single appliance and its name), so naming a signature on /consumatori or ticking „un singur aparat” on
    /// /plugs shows at the next load instead of after the cache expires (two small reads per call).
    /// </summary>
    public async Task<HistoryDetected> GetDetectedAsync(ReportPeriod period, CancellationToken ct)
    {
        var labels = await LabelsFingerprintAsync(ct);
        return await Cached("detected", period, labels, _ => ComputeDetectedAsync(period, None), ct);
    }

    /// <summary>A stable number that changes whenever an identification changes (signature labels, single-appliance plugs).</summary>
    public async Task<double> LabelsFingerprintAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var sigs = await scope.ServiceProvider.GetRequiredService<INilmRepository>().ListSignaturesAsync(ct);
        var plugs = await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var text = string.Join("|", sigs.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Name}:{(int)x.LabelSource}:{x.PlugId}:{x.Hidden}"))
                   + "#" + string.Join("|", plugs.OrderBy(x => x.Id).Select(x => $"{x.Id}:{x.Appliance}"));
        // FNV-1a over the text: deterministic across processes (string.GetHashCode is not), exact in a double (32 bits).
        uint h = 2166136261;
        foreach (var c in text) h = (h ^ c) * 16777619;
        return h;
    }

    private async Task<HistoryDetected> ComputeDetectedAsync(ReportPeriod period, CancellationToken ct)
    {
        var from = period.StartUtc(TimeZone);
        var to = period.EndUtc(TimeZone);
        var house = await _reports.GetHouseReportAsync(period, ct);
        var buckets = house?.Buckets ?? period.Buckets(TimeZone);
        IReadOnlyDictionary<long, ApplianceSignature> detected = new Dictionary<long, ApplianceSignature>();
        IReadOnlyList<ApplianceRun> runs = Array.Empty<ApplianceRun>();
        var now = _clock();
        if (_detectedRuns is not null) (detected, runs, now) = await _detectedRuns(from, to, ct);
        IReadOnlyList<ApplianceSignature> all;
        using (var scope = _scopes.CreateScope())
            all = (await scope.ServiceProvider.GetRequiredService<INilmRepository>().ListSignaturesAsync(ct)).Where(s => !s.Hidden).ToList();
        var names = detected.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        var enabled = _detectedRuns is not null && (_kasaSettings is null || (await _kasaSettings.GetAsync(ct)).NilmEnabled);
        var rows = DetectedApplianceStats.Build(runs, names, from, to, now, TimeZone);

        var breakdown = ConsumerBreakdown.Empty;
        if (house is not null)
        {
            var perBucket = DetectedApplianceStats.PerBucket(runs, buckets, now);
            IReadOnlyDictionary<Guid, string?> appliances;
            using (var scope = _scopes.CreateScope())
                appliances = (await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct)).ToDictionary(p => p.Id, p => p.Appliance);
            var plugs = (house.PlugBuckets ?? Array.Empty<PlugBucketSeries>())
                .Select(p => new ConsumerPlug(p.PlugId, p.Alias, p.Kwh, appliances.GetValueOrDefault(p.PlugId))).ToList();
            // The detected ones as the provider read them, plus the plug-confirmed ones (they name their plugs).
            var signatures = detected.Values.Concat(all).GroupBy(s => s.Id).Select(g => g.First())
                .Select(s => new ConsumerSignature(s.Id, s.Name, s.LabelSource == SignatureLabelSource.User, s.PlugId)).ToList();
            breakdown = ConsumerBreakdownRule.Build(house.HouseKwh, plugs, perBucket, signatures);
        }
        return new HistoryDetected(rows, enabled, breakdown, buckets);
    }

    // ------------------------------------------------------------------ climate

    public async Task<HistoryClimate> GetClimateAsync(ReportPeriod period, CancellationToken ct)
    {
        var baseC = _climateSettings is null ? DegreeDayRule.EurostatBaseC : (await _climateSettings.GetAsync(ct)).HeatingDegreeDaysBaseC;
        return await Cached("climate", period, baseC, now => ComputeClimateAsync(period, DegreeDayRule.ForBase(baseC), now, None), ct);
    }

    /// <summary>The period of the energy signature: the period itself from 7 days on, else the 30 days ending with it (never past today).</summary>
    public static ReportPeriod SignaturePeriodOf(ReportPeriod period, DateOnly today)
    {
        var last = period.LastDay < today ? period.LastDay : today;
        if (period.DayCount >= EnergySignatureRule.MinDays) return ReportPeriod.Between(period.Start, last < period.Start ? period.Start : last);
        return ReportPeriod.Between(last.AddDays(-(SignatureFallbackDays - 1)), last);
    }

    private async Task<HistoryClimate> ComputeClimateAsync(ReportPeriod period, DegreeDayRule rule, DateTimeOffset now, CancellationToken ct)
    {
        var tz = TimeZone;
        IReadOnlyList<ClimateSensor> sensors;
        using (var scope = _scopes.CreateScope())
            sensors = (await scope.ServiceProvider.GetRequiredService<IClimateRepository>().ListSensorsAsync(false, ct))
                .Where(s => !ClimateOverview.IsBoilerWater(s))
                .OrderBy(s => s.IsOutdoor).ThenBy(s => s.Room, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        var outdoor = sensors.FirstOrDefault(s => s.IsOutdoor);
        var buckets = period.Buckets(tz);
        var today = LocalToday(now);
        var lastDay = period.LastDay < today ? period.LastDay : today;
        var ids = sensors.Select(s => s.Id).ToList();

        // Daily stats of every sensor over the period (and of the outdoor one over the signature's days).
        var daily = ids.Count == 0 || period.Start > lastDay
            ? Array.Empty<ClimateDayStats>()
            : await _climateDaily.GetAsync(ids, period.Start, lastDay, tz, ct);
        var byKey = daily.ToDictionary(d => (d.SensorId, d.Day));

        var result = new List<HistoryClimateSensor>();
        if (period.Kind == ReportGranularity.Day)
        {
            // Hours of a day straight from the raw readings (one day is small).
            IReadOnlyDictionary<Guid, IReadOnlyList<ClimateReading>> raw;
            using (var scope = _scopes.CreateScope())
                raw = ids.Count == 0 ? new Dictionary<Guid, IReadOnlyList<ClimateReading>>()
                    : await scope.ServiceProvider.GetRequiredService<IClimateRepository>().ListReadingsAsync(ids, buckets[0].StartUtc - ClimateAggregation.MaxHold, buckets[^1].EndUtc, ct);
            var windows = buckets.Select(b => (b.StartUtc, b.EndUtc)).ToList();
            foreach (var s in sensors)
            {
                var points = raw.TryGetValue(s.Id, out var r) ? r.Select(x => new ClimatePoint(x.TsUtc, x.TemperatureC, x.HumidityPercent)).ToList() : new List<ClimatePoint>();
                var stats = ClimateWindows.Stats(points, windows);
                result.Add(new HistoryClimateSensor(s, stats, byKey.TryGetValue((s.Id, period.Start), out var day) ? day.Stats : ClimateStats.Empty));
            }
        }
        else
        {
            foreach (var s in sensors)
            {
                var days = Enumerable.Range(0, period.DayCount).Select(i => period.Start.AddDays(i))
                    .Select(d => byKey.TryGetValue((s.Id, d), out var x) ? x.Stats : ClimateStats.Empty).ToList();
                var perBucket = period.Kind == ReportGranularity.Year
                    ? buckets.Select(b => ClimateWindows.Combine(days.Where((_, i) => period.Start.AddDays(i).Month == b.Day.Month))).ToList()
                    : days;
                result.Add(new HistoryClimateSensor(s, perBucket, ClimateWindows.Combine(days)));
            }
        }

        // Degree days per bar from the outdoor sensor's daily means (a day period: that day's), finished days only: today's
        // mean so far (the morning is colder than the whole day) is shown apart as „parțial”, never added.
        var dd = new double?[buckets.Count];
        double? ddTotal = null, partialDd = null;
        DateOnly? partialDay = null;
        if (outdoor is not null)
        {
            for (var d = period.Start; d <= lastDay; d = d.AddDays(1))
            {
                if (!byKey.TryGetValue((outdoor.Id, d), out var x) || x.Stats.AvgC is not { } mean) continue;
                var v = rule.Of(mean);
                if (d >= today)
                {
                    (partialDay, partialDd) = (d, v);
                    continue;
                }
                ddTotal = (ddTotal ?? 0) + v;
                // A day's bars are hours: its degree days are only the total.
                if (period.Kind != ReportGranularity.Day && period.BucketOfDay(d) is var i and >= 0) dd[i] = (dd[i] ?? 0) + v;
            }
        }

        // Energy signature: heating gas per boiler day ~ degree days per local day (the one-hour offset is negligible per day).
        var sigPeriod = SignaturePeriodOf(period, today);
        EnergySignature? signature = null;
        DateOnly? firstReading = null;
        if (outdoor is not null && await DeviceAsync(ct) is { } device)
        {
            var outdoorDaily = sigPeriod == ReportPeriod.Between(period.Start, lastDay)
                ? daily.Where(x => x.SensorId == outdoor.Id).ToList()
                : await _climateDaily.GetAsync(new[] { outdoor.Id }, sigPeriod.Start, sigPeriod.LastDay, tz, ct);
            var means = outdoorDaily.Where(x => x.Stats.AvgC is not null).ToDictionary(x => x.Day, x => x.Stats.AvgC!.Value);
            firstReading = means.Count == 0 ? null : means.Keys.Min();
            var kwhPerM3 = _heatingSettings is null ? ViessmannApi.DefaultKwhPerM3 : (await _heatingSettings.GetAsync(ct)).GasKwhPerM3;
            var boilerToday = _reports.BoilerToday();
            IReadOnlyList<HeatingEnergyDay> gasDays;
            using (var scope = _scopes.CreateScope())
                gasDays = await scope.ServiceProvider.GetRequiredService<IHeatingRepository>().ListDailyAsync(device.Id, sigPeriod.Start, sigPeriod.LastDay, ct);
            // Whole days only: today's gas is not over yet.
            var gas = gasDays.Where(g => g.Day < boilerToday && g.GasHeatingFor(boilerToday) is not null)
                .GroupBy(g => g.Day).ToDictionary(g => g.Key, g => g.First().GasHeatingFor(boilerToday)!.Value * kwhPerM3);
            signature = EnergySignatureRule.Build(means.Where(kv => kv.Key < today).ToDictionary(kv => kv.Key, kv => kv.Value), gas, rule);
        }
        firstReading ??= result.SelectMany(r => r.Buckets).Any(b => b.AvgC is not null) ? period.Start : null;
        return new HistoryClimate(buckets, result, outdoor, dd, ddTotal, rule, signature, sigPeriod, firstReading, partialDay, partialDd);
    }

    private DateOnly LocalToday(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZone).DateTime);

    // ------------------------------------------------------------------ events

    public Task<HistoryEventList> GetEventsAsync(ReportPeriod period, CancellationToken ct) =>
        Cached("events", period, 0, _ => ComputeEventsAsync(period, None), ct);

    private async Task<HistoryEventList> ComputeEventsAsync(ReportPeriod period, CancellationToken ct)
    {
        var from = period.StartUtc(TimeZone);
        var to = period.EndUtc(TimeZone);
        var events = new List<HistoryEvent>();
        var truncated = false;
        using var scope = _scopes.CreateScope();
        var sp = scope.ServiceProvider;

        var aliases = (await sp.GetRequiredService<IPlugRepository>().ListAllAsync(ct)).ToDictionary(p => p.Id, p => p.Alias);
        var relay = await sp.GetRequiredService<ILiveRepository>().ListRelayCommandsAsync(from, to, MaxEventsPerSource + 1, ct);
        truncated |= relay.Count > MaxEventsPerSource;
        events.AddRange(HistoryEvents.Relay(relay.Take(MaxEventsPerSource), aliases));

        if (await DeviceAsync(ct) is { } device)
        {
            // Only the readings where something changes leave the database (window functions), not every reading of the period.
            var changes = await sp.GetRequiredService<IHeatingRepository>().ListReadingChangesAsync(device.Id, from, to, HistoryEvents.BoilerGap, ct);
            var boiler = HistoryEvents.BoilerFromChanges(changes, device.Name).ToList();
            truncated |= boiler.Count > MaxEventsPerSource;
            events.AddRange(boiler.OrderByDescending(e => e.TsUtc).Take(MaxEventsPerSource));
        }

        var miele = sp.GetRequiredService<IMieleRepository>();
        foreach (var d in await miele.ListDevicesAsync(includeArchived: true, ct))
        {
            var name = string.IsNullOrWhiteSpace(d.Alias) ? d.TypeText : d.Alias;
            var list = HistoryEvents.Miele(await miele.ListSnapshotsAsync(d.Id, from, to, ct), name).ToList();
            truncated |= list.Count > MaxEventsPerSource;
            events.AddRange(list.OrderByDescending(e => e.TsUtc).Take(MaxEventsPerSource));
        }
        return new HistoryEventList(events.OrderByDescending(e => e.TsUtc).ToList(), truncated);
    }
}

/// <summary>
/// <see cref="IClimateDailyStats"/> from the raw readings: one query for every sensor over the days (plus the hold of the
/// reading before the first midnight), then <see cref="ClimateWindows.Stats"/> per day. Used by <see cref="ClimateDailyStore"/>
/// for today and the days not stored yet.
/// </summary>
public sealed class RawClimateDailyStats : IClimateDailyStats
{
    private readonly IServiceScopeFactory _scopes;
    public RawClimateDailyStats(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<IReadOnlyList<ClimateDayStats>> GetAsync(IReadOnlyCollection<Guid> sensorIds, DateOnly from, DateOnly to, TimeZoneInfo tz, CancellationToken ct)
    {
        if (sensorIds.Count == 0 || to < from) return Array.Empty<ClimateDayStats>();
        var period = ReportPeriod.Between(from, to);
        var days = period.Buckets(tz);
        IReadOnlyDictionary<Guid, IReadOnlyList<ClimateReading>> raw;
        using (var scope = _scopes.CreateScope())
            raw = await scope.ServiceProvider.GetRequiredService<IClimateRepository>()
                .ListReadingsAsync(sensorIds, days[0].StartUtc - ClimateAggregation.MaxHold, days[^1].EndUtc, ct);
        var windows = days.Select(b => (b.StartUtc, b.EndUtc)).ToList();
        var result = new List<ClimateDayStats>();
        foreach (var (id, readings) in raw)
        {
            var stats = ClimateWindows.Stats(readings.Select(r => new ClimatePoint(r.TsUtc, r.TemperatureC, r.HumidityPercent)).ToList(), windows);
            for (var i = 0; i < days.Count; i++)
                if (stats[i].AvgC is not null) result.Add(new ClimateDayStats(id, days[i].Day, stats[i]));
        }
        return result;
    }
}
