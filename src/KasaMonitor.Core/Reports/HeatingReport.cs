using KasaMonitor.Core.Heating;

namespace KasaMonitor.Core.Reports;

/// <summary>The boiler's "today" counters as stored with one reading (HeatingReadings): gas in m³, electricity and heat in kWh (both purposes).</summary>
public sealed record HeatingCounterReading(DateTimeOffset TsUtc, double? GasHeatingM3, double? GasDhwM3, double? PowerKwh, double? HeatKwh);

/// <summary>Σ of the stored days of one month (HeatingEnergyDaily; gas counted as in <see cref="HeatingEnergyDay.GasHeatingFor"/>).</summary>
public sealed record HeatingMonthSums(
    int Year,
    int Month,
    double? GasHeatingM3,
    double? GasDhwM3,
    double? HeatHeatingKwh,
    double? HeatDhwKwh,
    double? PowerHeatingKwh,
    double? PowerDhwKwh);

/// <summary>A drop of a "today" counter that is not a reset (kept out of the hourly bars; the last higher value stays the reference).</summary>
public sealed record CounterDecrease(string Kind, string Series, DateTimeOffset TsUtc, double Previous, double Received);

/// <summary>One stacked series of a report chart. Keys: <c>heating</c>, <c>dhw</c> or <c>total</c> (hours of electricity/heat: no split).</summary>
public sealed record ReportSeries(string Key, IReadOnlyList<double?> Values);

/// <summary>
/// One quantity (gas m³, boiler electricity kWh, heat produced kWh) over a period.
/// <see cref="OwnTotal"/> = what KasaMonitor stored (Σ bars, the stored day for a day report, plus <see cref="Remainder"/>);
/// <see cref="ApiTotal"/> = the boiler's own value for this calendar period (today, this/last month, this/last year);
/// <see cref="Total"/> = the boiler's value when it has one, otherwise ours.
/// <see cref="Remainder"/> (year only, aligned with <see cref="Series"/>): the part of the boiler's year total not
/// covered by stored days ("restul anului").
/// </summary>
public sealed record HeatingKindReport(
    string Kind,
    string Unit,
    IReadOnlyList<ReportSeries> Series,
    IReadOnlyList<double>? Remainder,
    double? OwnTotal,
    double? ApiTotal,
    double? Total,
    double? PreviousTotal,
    double? DeltaPct,
    bool ApiDiffers);

/// <summary>The "Rapoarte" of /centrala for one period: gas, boiler electricity and heat produced.</summary>
public sealed record HeatingReport(
    ReportPeriod Period,
    IReadOnlyList<ReportBucket> Buckets,
    HeatingKindReport Gas,
    HeatingKindReport Power,
    HeatingKindReport Heat,
    DateOnly? FirstDay,
    IReadOnlyList<CounterDecrease>? IgnoredDecreases = null);

/// <param name="Days">Stored days of the current and the previous period (not used for a year).</param>
/// <param name="Months">Year only: month sums of the current and the previous year.</param>
/// <param name="ApiPeriods">The boiler's period values (day, month, last month, year, last year) with the time they were read.</param>
/// <param name="Readings">Day only: readings of the day, starting a little before its midnight.</param>
/// <param name="FirstDay">First stored day (the history starts there).</param>
public sealed record HeatingReportInput(
    ReportPeriod Period,
    TimeZoneInfo Tz,
    DateOnly Today,
    IReadOnlyList<HeatingEnergyDay> Days,
    IReadOnlyList<HeatingMonthSums> Months,
    IReadOnlyList<HeatingEnergyPeriod> ApiPeriods,
    IReadOnlyList<HeatingCounterReading> Readings,
    DateOnly? FirstDay);

/// <summary>
/// Builds the boiler reports. Days and months come from the daily history KasaMonitor builds from the boiler's
/// "currentDay" values; hours of a day are the increases of the "today" counters between consecutive readings
/// (a lower value = the boiler reset its counter, the new value is what was used since). The boiler's own period
/// values are the check and the fallback where no day is stored.
/// </summary>
public static class HeatingReportBuilder
{
    public const string SeriesHeating = HeatingKinds.Heating;
    public const string SeriesDhw = HeatingKinds.Dhw;
    public const string SeriesTotal = "total";

    /// <summary>Own and boiler totals differing by more than this fraction are both shown.</summary>
    public const double ApiDifferenceThreshold = 0.05;

    private sealed record KindDef(
        string Kind,
        string Unit,
        Func<HeatingEnergyDay, DateOnly, double?> Heating,
        Func<HeatingEnergyDay, DateOnly, double?> Dhw,
        Func<HeatingMonthSums, double?> MonthHeating,
        Func<HeatingMonthSums, double?> MonthDhw,
        IReadOnlyList<(string Key, Func<HeatingCounterReading, double?> Value)> Hourly);

    private static readonly KindDef GasDef = new(HeatingKinds.Gas, HeatingKinds.UnitM3,
        (d, today) => d.GasHeatingFor(today), (d, today) => d.GasDhwFor(today),
        m => m.GasHeatingM3, m => m.GasDhwM3,
        new (string, Func<HeatingCounterReading, double?>)[] { (SeriesHeating, r => r.GasHeatingM3), (SeriesDhw, r => r.GasDhwM3) });

    private static readonly KindDef PowerDef = new(HeatingKinds.Power, HeatingKinds.UnitKwh,
        (d, _) => d.PowerHeatingKwh, (d, _) => d.PowerDhwKwh,
        m => m.PowerHeatingKwh, m => m.PowerDhwKwh,
        new (string, Func<HeatingCounterReading, double?>)[] { (SeriesTotal, r => r.PowerKwh) });

    private static readonly KindDef HeatDef = new(HeatingKinds.Heat, HeatingKinds.UnitKwh,
        (d, _) => d.HeatHeatingKwh, (d, _) => d.HeatDhwKwh,
        m => m.HeatHeatingKwh, m => m.HeatDhwKwh,
        new (string, Func<HeatingCounterReading, double?>)[] { (SeriesTotal, r => r.HeatKwh) });

    public static HeatingReport Build(HeatingReportInput input)
    {
        var buckets = input.Period.Buckets(input.Tz);
        var days = input.Days.GroupBy(d => d.Day).ToDictionary(g => g.Key, g => g.First());
        var months = input.Months.GroupBy(m => (m.Year, m.Month)).ToDictionary(g => g.Key, g => g.First());
        var ignored = new List<CounterDecrease>();
        return new HeatingReport(input.Period, buckets,
            Kind(GasDef, input, buckets, days, months, ignored),
            Kind(PowerDef, input, buckets, days, months, ignored),
            Kind(HeatDef, input, buckets, days, months, ignored),
            input.FirstDay, ignored);
    }

    private static HeatingKindReport Kind(KindDef def, HeatingReportInput input, IReadOnlyList<ReportBucket> buckets,
        IReadOnlyDictionary<DateOnly, HeatingEnergyDay> days, IReadOnlyDictionary<(int, int), HeatingMonthSums> months, List<CounterDecrease> ignored)
    {
        var period = input.Period;
        List<ReportSeries> series;
        IReadOnlyList<double>? remainder = null;
        if (period.Kind == ReportGranularity.Day)
        {
            series = def.Hourly
                .Select(h =>
                {
                    var dropped = new List<(DateTimeOffset, double, double)>();
                    var values = HourlyIncrements(input.Readings.Select(r => (r.TsUtc, h.Value(r))), buckets, input.Tz, dropped);
                    ignored.AddRange(dropped.Select(d => new CounterDecrease(def.Kind, h.Key, d.Item1, d.Item2, d.Item3)));
                    return new ReportSeries(h.Key, values);
                })
                .ToList();
        }
        else if (period.Kind == ReportGranularity.Year)
        {
            series = new List<ReportSeries>
            {
                new(SeriesHeating, buckets.Select(b => months.TryGetValue((b.Day.Year, b.Day.Month), out var m) ? def.MonthHeating(m) : null).ToList()),
                new(SeriesDhw, buckets.Select(b => months.TryGetValue((b.Day.Year, b.Day.Month), out var m) ? def.MonthDhw(m) : null).ToList()),
            };
            remainder = YearRemainder(def, input, period, series.Select(s => ReportMath.SumOrNull(s.Values)).ToList());
        }
        else
        {
            series = new List<ReportSeries>
            {
                new(SeriesHeating, buckets.Select(b => days.TryGetValue(b.Day, out var d) ? def.Heating(d, input.Today) : null).ToList()),
                new(SeriesDhw, buckets.Select(b => days.TryGetValue(b.Day, out var d) ? def.Dhw(d, input.Today) : null).ToList()),
            };
        }

        double? own;
        if (period.Kind == ReportGranularity.Day)
            own = StoredDay(def, days, period.Start, input.Today) ?? ReportMath.SumOrNull(series.SelectMany(s => s.Values));
        else
        {
            own = ReportMath.SumOrNull(series.SelectMany(s => s.Values));
            if (remainder is { Count: > 0 }) own = (own ?? 0) + remainder.Sum();
        }
        var api = Api(def, input, period);
        var total = api ?? own;
        var previous = TotalOf(def, input, period.Previous(), days, months);
        var differs = own is { } o && api is { } a && ReportMath.DiffersBy(o, a, ApiDifferenceThreshold);
        return new HeatingKindReport(def.Kind, def.Unit, series, remainder, own, api, total, previous, ReportMath.DeltaPct(total, previous), differs);
    }

    /// <summary>Total of a period without its bars: the boiler's value when it has one, otherwise the stored days / months.</summary>
    private static double? TotalOf(KindDef def, HeatingReportInput input, ReportPeriod p,
        IReadOnlyDictionary<DateOnly, HeatingEnergyDay> days, IReadOnlyDictionary<(int, int), HeatingMonthSums> months)
    {
        if (Api(def, input, p) is { } api) return api;
        if (p.Kind == ReportGranularity.Day) return StoredDay(def, days, p.Start, input.Today);
        if (p.Kind == ReportGranularity.Year)
        {
            var heating = ReportMath.SumOrNull(Enumerable.Range(1, 12).Select(m => months.TryGetValue((p.Start.Year, m), out var s) ? def.MonthHeating(s) : null));
            var dhw = ReportMath.SumOrNull(Enumerable.Range(1, 12).Select(m => months.TryGetValue((p.Start.Year, m), out var s) ? def.MonthDhw(s) : null));
            return ReportMath.SumOrNull(new[] { heating, dhw });
        }
        return ReportMath.SumOrNull(Enumerable.Range(0, p.DayCount).Select(i => StoredDay(def, days, p.Start.AddDays(i), input.Today)));
    }

    private static double? StoredDay(KindDef def, IReadOnlyDictionary<DateOnly, HeatingEnergyDay> days, DateOnly day, DateOnly today) =>
        days.TryGetValue(day, out var d) ? ReportMath.SumOrNull(new[] { def.Heating(d, today), def.Dhw(d, today) }) : null;

    /// <summary>Per purpose: the boiler's year value minus Σ stored months, when positive (only for the year the boiler reports).</summary>
    private static IReadOnlyList<double>? YearRemainder(KindDef def, HeatingReportInput input, ReportPeriod year, IReadOnlyList<double?> monthSums)
    {
        var heating = ApiPurpose(def.Kind, HeatingKinds.Heating, input, year);
        var dhw = ApiPurpose(def.Kind, HeatingKinds.Dhw, input, year);
        if (heating is null && dhw is null) return null;
        var rem = new[]
        {
            Math.Max(0, (heating ?? 0) - (monthSums[0] ?? 0)),
            Math.Max(0, (dhw ?? 0) - (monthSums[1] ?? 0)),
        };
        return rem.Sum() > 1e-6 ? rem : null;
    }

    /// <summary>The boiler's value (both purposes) for a calendar period, or null when it reports none for it.</summary>
    public static double? Api(string kind, IReadOnlyList<HeatingEnergyPeriod> periods, ReportPeriod p, TimeZoneInfo tz) =>
        ReportMath.SumOrNull(new[] { HeatingKinds.Heating, HeatingKinds.Dhw }.Select(purpose => ApiPurpose(kind, purpose, periods, p, tz)));

    private static double? Api(KindDef def, HeatingReportInput input, ReportPeriod p) => Api(def.Kind, input.ApiPeriods, p, input.Tz);

    private static double? ApiPurpose(string kind, string purpose, HeatingReportInput input, ReportPeriod p) =>
        ApiPurpose(kind, purpose, input.ApiPeriods, p, input.Tz);

    /// <summary>
    /// Maps the boiler's relative periods to calendar periods by the local day they were read on: "day" = that day,
    /// "month"/"lastmonth" = its month / the month before, "year"/"lastyear" = its year / the year before.
    /// "last7days" is a rolling window, never a calendar week.
    /// </summary>
    private static double? ApiPurpose(string kind, string purpose, IReadOnlyList<HeatingEnergyPeriod> periods, ReportPeriod p, TimeZoneInfo tz)
    {
        foreach (var v in periods.Where(x => x.Kind == kind && x.Purpose == purpose))
        {
            var read = HeatingEnergy.DayOf(v.UpdatedUtc, tz);
            var matches = (p.Kind, v.Period) switch
            {
                (ReportGranularity.Day, "day") => p.Start == read,
                (ReportGranularity.Month, "month") => p == ReportPeriod.Containing(ReportGranularity.Month, read),
                (ReportGranularity.Month, "lastmonth") => p == ReportPeriod.Containing(ReportGranularity.Month, read).Previous(),
                (ReportGranularity.Year, "year") => p == ReportPeriod.Containing(ReportGranularity.Year, read),
                (ReportGranularity.Year, "lastyear") => p == ReportPeriod.Containing(ReportGranularity.Year, read).Previous(),
                _ => false,
            };
            if (matches) return v.Value;
        }
        return null;
    }

    /// <summary>A drop of a "today" counter is a reset only below this share of the previous value…</summary>
    public const double ResetMaxShare = 0.2;

    /// <summary>…and only when the new reading's local time is between these (around midnight; inclusive).</summary>
    public static readonly TimeOnly ResetWindowFrom = new(23, 50), ResetWindowTo = new(0, 30);

    /// <summary>True when a drop from <paramref name="previous"/> to <paramref name="value"/> at <paramref name="tsUtc"/> is the daily reset.</summary>
    public static bool IsReset(double previous, double value, DateTimeOffset tsUtc, TimeZoneInfo tz)
    {
        if (!(value < ResetMaxShare * previous)) return false;
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(tsUtc, tz).DateTime);
        return local >= ResetWindowFrom || local <= ResetWindowTo;
    }

    /// <summary>
    /// Hourly use from a "today" counter: each reading adds its increase over the reference value to the hour it falls
    /// in. A drop is the daily reset only when the new value is under 20 % of the reference and the reading is between
    /// 23:50 and 00:30 local (<see cref="IsReset"/>): the new value is then the use since the reset. Any other drop is a
    /// correction: it adds nothing, the reference keeps the last higher value, and it is reported in
    /// <paramref name="ignored"/> (time, reference, received). An hour without a pair of readings stays null ("fără
    /// date"); the first reading of the day pairs with the last one before midnight.
    /// </summary>
    public static IReadOnlyList<double?> HourlyIncrements(IEnumerable<(DateTimeOffset TsUtc, double? Value)> readings, IReadOnlyList<ReportBucket> buckets,
        TimeZoneInfo tz, List<(DateTimeOffset TsUtc, double Previous, double Received)>? ignored = null)
    {
        var values = new double?[buckets.Count];
        if (buckets.Count == 0) return values;
        var start = buckets[0].StartUtc;
        var end = buckets[^1].EndUtc;
        double? prev = null;
        foreach (var (ts, value) in readings.OrderBy(r => r.TsUtc))
        {
            if (value is not { } v) continue;
            if (ts >= end) break;
            var inRange = ts >= start;
            var i = (int)((ts - start).Ticks / TimeSpan.TicksPerHour);
            if (prev is { } p)
            {
                double increment;
                if (v >= p) increment = v - p;
                else if (IsReset(p, v, ts, tz)) increment = v;
                else
                {
                    // A correction, not a reset: nothing is added and the higher value stays the reference.
                    if (inRange) ignored?.Add((ts, p, v));
                    if (inRange && i >= 0 && i < buckets.Count) values[i] ??= 0;
                    continue;
                }
                if (inRange && i >= 0 && i < buckets.Count) values[i] = (values[i] ?? 0) + increment;
            }
            prev = v;
        }
        return values;
    }
}
