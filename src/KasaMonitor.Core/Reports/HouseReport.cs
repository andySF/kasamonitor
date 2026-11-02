using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Reports;

/// <summary>Energy of one plug in one month (Wh): a device month total (MonthlyEnergy) or the sum of its stored days.</summary>
public sealed record PlugMonthWh(Guid PlugId, int Year, int Month, double Wh);

/// <summary>Live-stream energy of one plug in one UTC hour (Σ of its minute aggregates) and how many minutes had an aggregate.</summary>
public sealed record PlugHourWh(Guid PlugId, DateTimeOffset HourStartUtc, double Wh, int Minutes = 60);

/// <summary>Period total of one sub-meter (kWh; null without data).</summary>
public sealed record SubMeterTotal(Guid PlugId, string Alias, double? Kwh);

/// <summary>
/// The "Casă" report of /history: the main meter split into the measured plugs and the rest of the house, per bucket
/// (null = no data for the main meter in that bucket), with the period totals and the previous period's house total.
/// <see cref="RestKwh"/> = max(0, house − plugs); <see cref="RestKinds"/> tells whether each difference is a real rest,
/// within the measurement margin (max(15 W × hours, 3 % of the house)), plugs above the meter beyond it, or plugs above
/// the meter on a bucket whose meter data is partial (<see cref="RestKind.PartialData"/>, not a clamp problem).
/// <see cref="PartialBuckets"/>: the main meter's live minutes cover less than 90 % of the bucket (elapsed part for the
/// current one) — set only when the stream has minutes in it. <see cref="PlugTotals"/>: each counted plug's energy of
/// the period, over the same buckets as <see cref="PlugsTotalKwh"/> (a week, month or year counts a plug only on the
/// buckets the main meter has data for, so plugs and meter cover the same days; the home page's "Aparate" and Sankey). <see cref="PreviousBuckets"/> /
/// <see cref="PreviousHouseKwh"/>: the bars of the previous period (read by the same queries: no second report needed).
/// </summary>
public sealed record HouseReport(
    ReportPeriod Period,
    IReadOnlyList<ReportBucket> Buckets,
    string MainAlias,
    IReadOnlyList<double?> HouseKwh,
    IReadOnlyList<double?> PlugsKwh,
    IReadOnlyList<double?> RestKwh,
    IReadOnlyList<RestKind?> RestKinds,
    double? HouseTotalKwh,
    double? PlugsTotalKwh,
    double? RestTotalKwh,
    double? PreviousHouseTotalKwh,
    double? DeltaPct,
    IReadOnlyList<SubMeterTotal> SubMeters,
    DateOnly? FirstHouseDay,
    RestKind? RestTotalKind = null,
    IReadOnlyList<bool>? PartialBuckets = null,
    bool IsCurrent = false,
    IReadOnlyList<SubMeterTotal>? PlugTotals = null,
    IReadOnlyList<ReportBucket>? PreviousBuckets = null,
    IReadOnlyList<double?>? PreviousHouseKwh = null)
{
    /// <summary>Some bucket (or the total) has the plugs above the meter beyond the margin: "verifică poziția clemei".</summary>
    public bool RestClamped => RestTotalKind == RestKind.Negative || RestKinds.Any(k => k == RestKind.Negative);

    /// <summary>Some bucket (or the total) is within the measurement margin ("≈ 0").</summary>
    public bool RestWithinMargin => RestTotalKind == RestKind.WithinMargin || RestKinds.Any(k => k == RestKind.WithinMargin);

    /// <summary>Some bucket (or the total) has plugs above a meter whose data is partial.</summary>
    public bool RestPartial => RestTotalKind == RestKind.PartialData || RestKinds.Any(k => k == RestKind.PartialData);

    /// <summary>A day report without any hourly bar (no live stream that day) while the day itself has a total.</summary>
    public bool DayWithoutHours => Period.Kind == ReportGranularity.Day && HouseTotalKwh is not null && HouseKwh.All(v => v is null);
}

/// <summary>Everything the house report is built from (read by the service in a fixed number of queries).</summary>
/// <param name="Daily">DailyEnergy rows of the days needed (current and previous period; for a year the days that have live data).</param>
/// <param name="Monthly">Year only: device month totals (MonthlyEnergy) of the two years.</param>
/// <param name="DailyByMonth">Year only: Σ DailyEnergy per plug and month of the two years.</param>
/// <param name="LiveHours">Live stream per plug and UTC hour over the current and the previous period.</param>
/// <param name="NowUtc">Now: the coverage of the bucket in progress counts only its elapsed part.</param>
public sealed record HouseReportInput(
    ReportPeriod Period,
    TimeZoneInfo Tz,
    Plug Main,
    IReadOnlyList<Plug> Plugs,
    IReadOnlyList<Plug> SubMeters,
    IReadOnlyList<PlugDailyKwh> Daily,
    IReadOnlyList<PlugMonthWh> Monthly,
    IReadOnlyList<PlugMonthWh> DailyByMonth,
    IReadOnlyList<PlugHourWh> LiveHours,
    DateTimeOffset NowUtc,
    DateOnly? FirstHouseDay);

/// <summary>
/// Builds the house report. Rules: a day's value of a channel is the larger of its DailyEnergy row (device memory / own
/// readings) and the Σ of KasaMonitor's live minute aggregates of that day (the device memory can be shorter than the
/// stream, and its sync lags by up to 6 h). A month is the larger of the device month total and Σ of its days.
/// "Restul casei" = max(0, house − plugs), classified with <see cref="HouseBalance.Classify"/> and the energy margin of
/// the bucket (15 W × its hours, or 3 % of the house); plugs above a meter whose bucket is partial (live coverage under
/// 90 %) is <see cref="RestKind.PartialData"/>, not a clamp warning. Hours of a day report come from the live stream only.
/// </summary>
public static class HouseReportBuilder
{
    /// <summary>Minimum share of a bucket's minutes the main meter's live stream must cover not to be "date parțiale".</summary>
    public const double MinCoverage = 0.9;

    public static HouseReport Build(HouseReportInput input)
    {
        var period = input.Period;
        var buckets = period.Buckets(input.Tz);
        var values = new ChannelValues(input);
        var plugIds = input.Plugs.Select(p => p.Id).ToList();
        var mainId = input.Main.Id;

        var house = new double?[buckets.Count];
        var plugs = new double?[buckets.Count];
        var rest = new double?[buckets.Count];
        var kinds = new RestKind?[buckets.Count];
        var partial = new bool[buckets.Count];
        var byHour = period.Kind == ReportGranularity.Day;

        for (var i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            house[i] = byHour ? values.Hour(mainId, b) : values.Bucket(mainId, period, b);
            partial[i] = values.IsPartial(mainId, b.StartUtc, b.EndUtc);
            if (house[i] is not { } h) continue;
            var p = plugIds.Sum(id => (byHour ? values.Hour(id, b) : values.Bucket(id, period, b)) ?? 0);
            plugs[i] = p;
            (rest[i], kinds[i]) = Rest(h, p, (b.EndUtc - b.StartUtc).TotalHours, partial[i]);
        }

        double? houseTotal, plugsTotal;
        double hoursWithData;
        if (byHour)
        {
            // The day's total is the larger of the stored day and the live sum, not only the hours the stream saw.
            houseTotal = values.Day(mainId, period.Start);
            plugsTotal = houseTotal is null ? null : plugIds.Sum(id => values.Day(id, period.Start) ?? 0);
            hoursWithData = (period.EndUtc(input.Tz) - period.StartUtc(input.Tz)).TotalHours;
        }
        else
        {
            houseTotal = ReportMath.SumOrNull(house);
            plugsTotal = ReportMath.SumOrNull(plugs);
            // The margin covers only the buckets that have data (a month with one stored day gets one day's margin).
            hoursWithData = buckets.Where((_, i) => house[i] is not null).Sum(x => (x.EndUtc - x.StartUtc).TotalHours);
        }
        double? restTotal = null;
        RestKind? totalKind = null;
        if (houseTotal is { } ht && plugsTotal is { } pt)
        {
            var totalPartial = byHour ? values.IsPartial(mainId, period.StartUtc(input.Tz), period.EndUtc(input.Tz)) : partial.Where((_, i) => house[i] is not null).Any(x => x);
            (restTotal, totalKind) = Rest(ht, pt, hoursWithData, totalPartial);
        }

        var previousTotal = values.Total(mainId, period.Previous());
        var subs = input.SubMeters
            .OrderBy(s => s.Alias, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new SubMeterTotal(s.Id, s.Alias, values.Total(s.Id, period)))
            .ToList();

        var previousPeriod = period.Previous();
        var previousBuckets = previousPeriod.Buckets(input.Tz);
        var previousHouse = previousBuckets
            .Select(b => byHour ? values.Hour(mainId, b) : values.Bucket(mainId, previousPeriod, b))
            .ToList();

        var plugTotals = input.Plugs
            .Select(p => new SubMeterTotal(p.Id, p.Alias, byHour
                ? values.Day(p.Id, period.Start)
                : ReportMath.SumOrNull(buckets.Select((b, i) => house[i] is null ? null : values.Bucket(p.Id, period, b)))))
            .ToList();

        var isCurrent = input.NowUtc >= period.StartUtc(input.Tz) && input.NowUtc < period.EndUtc(input.Tz);
        return new HouseReport(period, buckets, input.Main.Alias, house, plugs, rest, kinds,
            houseTotal, plugsTotal, restTotal, previousTotal, ReportMath.DeltaPct(houseTotal, previousTotal), subs, input.FirstHouseDay,
            totalKind, partial, isCurrent, plugTotals, previousBuckets, previousHouse);
    }

    private static (double? Value, RestKind Kind) Rest(double house, double plugs, double hours, bool partial)
    {
        var kind = HouseBalance.Classify(house, plugs, HouseBalance.EnergyMarginKwh(hours));
        if (kind == RestKind.Negative && partial) kind = RestKind.PartialData;
        return (Math.Max(0, house - plugs), kind);
    }

    /// <summary>Lookups of one channel's energy by hour, day, month and period (kWh; null without data).</summary>
    private sealed class ChannelValues
    {
        private readonly HouseReportInput _in;
        private readonly Dictionary<(Guid, DateOnly), double> _daily = new();
        private readonly Dictionary<(Guid, int, int), double> _monthly = new();
        private readonly Dictionary<(Guid, int, int), double> _dailyByMonth = new();
        private readonly Dictionary<(Guid, DateOnly), double> _liveDay = new();
        private readonly Dictionary<(Guid, DateTimeOffset), (double Wh, int Minutes)> _liveHour = new();

        public ChannelValues(HouseReportInput input)
        {
            _in = input;
            foreach (var r in input.Daily)
            {
                var key = (r.PlugId, new DateOnly(r.Day.Year, r.Day.Month, r.Day.Day));
                _daily[key] = _daily.GetValueOrDefault(key) + r.Day.EnergyWh;
            }
            foreach (var m in input.Monthly)
                _monthly[(m.PlugId, m.Year, m.Month)] = _monthly.GetValueOrDefault((m.PlugId, m.Year, m.Month)) + m.Wh;
            foreach (var m in input.DailyByMonth)
                _dailyByMonth[(m.PlugId, m.Year, m.Month)] = _dailyByMonth.GetValueOrDefault((m.PlugId, m.Year, m.Month)) + m.Wh;
            foreach (var h in input.LiveHours)
            {
                var hour = h.HourStartUtc.ToUniversalTime();
                var old = _liveHour.GetValueOrDefault((h.PlugId, hour));
                _liveHour[(h.PlugId, hour)] = (old.Wh + h.Wh, old.Minutes + h.Minutes);
                var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(hour, input.Tz).DateTime);
                _liveDay[(h.PlugId, day)] = _liveDay.GetValueOrDefault((h.PlugId, day)) + h.Wh;
            }
        }

        /// <summary>Live energy of an hour bucket (null when the channel has no live minute in it).</summary>
        public double? Hour(Guid plugId, ReportBucket b)
        {
            double? sum = null;
            for (var t = b.StartUtc.ToUniversalTime(); t < b.EndUtc; t = t.AddHours(1))
                if (_liveHour.TryGetValue((plugId, t), out var v)) sum = (sum ?? 0) + v.Wh;
            return sum / 1000.0;
        }

        /// <summary>
        /// The live minutes of the channel cover less than <see cref="MinCoverage"/> of the interval's elapsed minutes
        /// (false when the stream has no minute in it: nothing is known about the coverage then).
        /// </summary>
        public bool IsPartial(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc)
        {
            var end = toUtc < _in.NowUtc ? toUtc : _in.NowUtc;
            var expected = (end - fromUtc).TotalMinutes;
            if (expected < 1) return false;
            var minutes = 0;
            for (var t = fromUtc.ToUniversalTime(); t < toUtc; t = t.AddHours(1))
                if (_liveHour.TryGetValue((plugId, t), out var v)) minutes += v.Minutes;
            return minutes > 0 && minutes < MinCoverage * expected;
        }

        /// <summary>Value of a local day: the larger of the stored row and the live sum.</summary>
        public double? Day(Guid plugId, DateOnly day)
        {
            double? wh = _daily.TryGetValue((plugId, day), out var d) ? d : null;
            if (_liveDay.TryGetValue((plugId, day), out var live))
                wh = wh is { } x ? Math.Max(x, live) : live;
            return wh / 1000.0;
        }

        /// <summary>Value of a month: the larger of the device month total and Σ its days (each day counted as in <see cref="Day"/>).</summary>
        public double? Month(Guid plugId, int year, int month)
        {
            double? fromDays = _dailyByMonth.TryGetValue((plugId, year, month), out var s) ? s : null;
            // A day is in DailyByMonth as stored; its live sum can only raise it.
            foreach (var ((id, day), live) in _liveDay)
            {
                if (id != plugId || day.Year != year || day.Month != month) continue;
                var stored = _daily.TryGetValue((plugId, day), out var d) ? d : 0;
                if (live > stored) fromDays = (fromDays ?? 0) + live - stored;
            }
            double? device = _monthly.TryGetValue((plugId, year, month), out var m) ? m : null;
            var wh = device is { } a && fromDays is { } b ? Math.Max(a, b) : device ?? fromDays;
            return wh / 1000.0;
        }

        public double? Bucket(Guid plugId, ReportPeriod period, ReportBucket b) =>
            period.Kind == ReportGranularity.Year ? Month(plugId, b.Day.Year, b.Day.Month) : Day(plugId, b.Day);

        /// <summary>Total of a whole period (null when no part of it has data).</summary>
        public double? Total(Guid plugId, ReportPeriod p) => p.Kind switch
        {
            ReportGranularity.Day => Day(plugId, p.Start),
            ReportGranularity.Year => ReportMath.SumOrNull(Enumerable.Range(1, 12).Select(m => Month(plugId, p.Start.Year, m))),
            _ => ReportMath.SumOrNull(Enumerable.Range(0, p.DayCount).Select(i => Day(plugId, p.Start.AddDays(i)))),
        };
    }
}
