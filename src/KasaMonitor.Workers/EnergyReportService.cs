using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KasaMonitor.Workers;

/// <summary>
/// The period reports of /history ("Casă") and /centrala ("Rapoarte"): period → bars + total + previous period.
/// Every report reads a fixed number of grouped queries (no query per plug, day or month); the arithmetic lives in
/// <see cref="HouseReportBuilder"/> and <see cref="HeatingReportBuilder"/>.
/// </summary>
public sealed class EnergyReportService
{
    /// <summary>Readings this long before a day's midnight are read too, so the first hour pairs with the last reading before it.</summary>
    public static readonly TimeSpan ReadingLookBack = TimeSpan.FromHours(3);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeZoneInfo _tz;
    private readonly Func<TimeZoneInfo> _boilerTz;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;
    // Counter drops already logged (once per kind, series and boiler day, not at every reading or report load).
    private readonly HashSet<(string, string, DateOnly)> _loggedDecreases = new();

    /// <param name="timeZone">The application's zone (house reports, hour labels).</param>
    /// <param name="boilerTimeZone">The boiler's zone (<see cref="BoilerClock"/>; the boiler reports follow its day); default Europe/Berlin.</param>
    public EnergyReportService(IServiceScopeFactory scopeFactory, TimeZoneInfo? timeZone = null, Func<DateTimeOffset>? clock = null,
        ILogger<EnergyReportService>? log = null, Func<TimeZoneInfo>? boilerTimeZone = null)
    {
        _boilerTz = boilerTimeZone ?? (() => BoilerClock.DefaultTimeZone);
        _scopeFactory = scopeFactory;
        _tz = timeZone ?? TimeZoneInfo.Local;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public TimeZoneInfo TimeZone => _tz;

    /// <summary>The boiler's zone: its reports, days and hours follow the boiler's midnight.</summary>
    public TimeZoneInfo BoilerTimeZone => _boilerTz();

    /// <summary>Today in the boiler's zone (the boiler reports).</summary>
    public DateOnly BoilerToday() => BoilerClock.DayOf(_clock(), BoilerTimeZone);

    /// <summary>Today in the report time zone.</summary>
    public DateOnly Today() => HeatingEnergy.DayOf(_clock(), _tz);

    /// <summary>The current period of a kind (contains today).</summary>
    public ReportPeriod Current(ReportGranularity kind) => ReportPeriod.Containing(kind, Today());

    /// <summary>The house report, or null when there is no polled main meter.</summary>
    public async Task<HouseReport?> GetHouseReportAsync(ReportPeriod period, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plugRepo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        var history = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
        var live = scope.ServiceProvider.GetRequiredService<ILiveRepository>();

        var all = (await plugRepo.ListAllAsync(ct)).Where(p => p.IsPolled).ToList();
        var main = all.FirstOrDefault(p => p.Role == PlugRole.MainMeter);
        if (main is null) return null;
        var plugs = all.Where(p => p.CountsInTotals).ToList();
        var subs = all.Where(p => p.Role == PlugRole.SubMeter).ToList();

        var previous = period.Previous();
        var from = previous.Start;
        var to = period.EndExclusive;
        var ids = all.Where(p => p.Role == PlugRole.MainMeter || p.CountsInTotals || p.Role == PlugRole.SubMeter).Select(p => p.Id).ToList();

        // Live minute aggregates of both periods, grouped per channel and UTC hour in SQL: every day is the larger of
        // the stored day and the live sum (the meter's memory can be shorter than the stream), hours of a day report.
        var hours = await live.SumEnergyByHourAsync(ids, previous.StartUtc(_tz), period.EndUtc(_tz), ct);

        IReadOnlyList<PlugDailyKwh> daily;
        IReadOnlyList<PlugMonthWh> monthly = Array.Empty<PlugMonthWh>(), byMonth = Array.Empty<PlugMonthWh>();
        if (period.Kind == ReportGranularity.Year)
        {
            monthly = await history.GetMonthlyRangeAllAsync(previous.Start.Year, period.Start.Year, ct);
            byMonth = await history.SumDailyByMonthAllAsync(previous.Start.Year, period.Start.Year, ct);
            // Stored rows only of the days the stream saw (to raise them by the live sum).
            var liveDays = hours.Select(h => HeatingEnergy.DayOf(h.HourStartUtc, _tz)).ToList();
            daily = liveDays.Count == 0 ? Array.Empty<PlugDailyKwh>() : await history.GetDailyRangeAllAsync(liveDays.Min(), liveDays.Max(), ct);
        }
        else
        {
            daily = await history.GetDailyRangeAllAsync(from, to.AddDays(-1), ct);
        }

        var first = await history.FirstDailyDayAsync(main.Id, ct);
        return HouseReportBuilder.Build(new HouseReportInput(period, _tz, main, plugs, subs, daily, monthly, byMonth, hours, _clock(), first));
    }

    /// <summary>The boiler report of a device for a period: boiler days (<see cref="BoilerTimeZone"/>), hours labelled in local time.</summary>
    public async Task<HeatingReport> GetHeatingReportAsync(Guid deviceId, ReportPeriod period, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IHeatingRepository>();
        var boilerTz = BoilerTimeZone;
        var today = BoilerClock.DayOf(_clock(), boilerTz);
        var previous = period.Previous();

        IReadOnlyList<HeatingEnergyDay> days = Array.Empty<HeatingEnergyDay>();
        IReadOnlyList<HeatingMonthSums> months = Array.Empty<HeatingMonthSums>();
        IReadOnlyList<HeatingCounterReading> readings = Array.Empty<HeatingCounterReading>();
        if (period.Kind == ReportGranularity.Year)
            months = await repo.SumDailyByMonthAsync(deviceId, previous.Start.Year, period.Start.Year, today, ct);
        else
            days = await repo.ListDailyAsync(deviceId, previous.Start, period.LastDay, ct);
        IReadOnlyList<HeatingCounterReading>? previousReadings = null;
        var now = _clock();
        if (period.Kind == ReportGranularity.Day)
        {
            readings = await repo.ListCounterReadingsAsync(deviceId, period.StartUtc(boilerTz) - ReadingLookBack, period.EndUtc(boilerTz), ct);
            // The day in progress is compared with the previous day up to the same time: its hours are needed too.
            if (now >= period.StartUtc(boilerTz) && now < period.EndUtc(boilerTz))
                previousReadings = await repo.ListCounterReadingsAsync(deviceId, previous.StartUtc(boilerTz) - ReadingLookBack, previous.EndUtc(boilerTz), ct);
        }

        var api = await repo.ListPeriodsAsync(deviceId, ct);
        var first = await repo.FirstDailyDayAsync(deviceId, ct);
        var report = HeatingReportBuilder.Build(new HeatingReportInput(period, boilerTz, today, days, months, api, readings, first, _tz, now, previousReadings));
        LogDecreases(report.IgnoredDecreases, boilerTz);
        return report;
    }

    /// <summary>
    /// Gas used today vs yesterday up to the same time (boiler days) (from the "today" counters stored with the readings, so the
    /// comparison is fair at any hour); <see cref="GasDayComparison.YesterdaySameTimeM3"/> is null when yesterday has no
    /// stored counters, and then only yesterday's whole-day total is shown.
    /// </summary>
    public async Task<GasDayComparison> GetGasTodayVsYesterdayAsync(Guid deviceId, CancellationToken ct)
    {
        var now = _clock();
        var tz = BoilerTimeZone;
        var today = ReportPeriod.Containing(ReportGranularity.Day, BoilerClock.DayOf(now, tz));
        var report = await GetHeatingReportAsync(deviceId, today, ct);
        var yesterday = today.Previous();
        var sameUtc = KasaMonitor.Core.Home.LocalClock.SameTimeOnUtc(now, tz, yesterday.Start);

        IReadOnlyList<HeatingCounterReading> readings;
        using (var scope = _scopeFactory.CreateScope())
            readings = await scope.ServiceProvider.GetRequiredService<IHeatingRepository>()
                .ListCounterReadingsAsync(deviceId, yesterday.StartUtc(tz) - ReadingLookBack, sameUtc, ct);
        var buckets = yesterday.Buckets(tz);
        double? sameTime = null;
        if (readings.Any(r => r.TsUtc >= yesterday.StartUtc(tz) && (r.GasHeatingM3 is not null || r.GasDhwM3 is not null)))
        {
            var heating = HeatingReportBuilder.HourlyIncrements(readings.Select(r => (r.TsUtc, r.GasHeatingM3)), buckets, tz);
            var dhw = HeatingReportBuilder.HourlyIncrements(readings.Select(r => (r.TsUtc, r.GasDhwM3)), buckets, tz);
            sameTime = ReportMath.SumOrNull(heating.Concat(dhw));
        }
        var todayM3 = report.Gas.Total;
        return new GasDayComparison(todayM3, sameTime, report.Gas.PreviousTotal, ReportMath.DeltaPct(todayM3, sameTime));
    }

    private void LogDecreases(IReadOnlyList<CounterDecrease>? decreases, TimeZoneInfo boilerTz)
    {
        if (decreases is null) return;
        foreach (var d in decreases)
        {
            lock (_loggedDecreases)
                if (!_loggedDecreases.Add((d.Kind, d.Series, BoilerClock.DayOf(d.TsUtc, boilerTz)))) continue;
            _log.LogWarning(
                "Heating {Kind} {Series} 'today' counter dropped from {Previous} to {Received} at {Boiler} boiler time ({Local} local): not a reset (needs 23:45-00:30 boiler time, or < 20 % with no reset in 20 h), ignored in the hourly report; logged once a day",
                d.Kind, d.Series, d.Previous, d.Received, TimeZoneInfo.ConvertTime(d.TsUtc, boilerTz).ToString("yyyy-MM-dd HH:mm:ss"),
                TimeZoneInfo.ConvertTime(d.TsUtc, _tz).ToString("yyyy-MM-dd HH:mm:ss"));
        }
    }
}

/// <summary>Gas today, yesterday up to the same local time, yesterday's whole day (m³) and today vs the same time yesterday (%).</summary>
public sealed record GasDayComparison(double? TodayM3, double? YesterdaySameTimeM3, double? YesterdayM3, double? DeltaPct);
