using KasaMonitor.Core.Protocol.Shelly;

namespace KasaMonitor.Core.Reports;

/// <summary>
/// Length of a report period (the ViCare-style selector): a day by hours, a week or a month by days, a year by months;
/// <see cref="Range"/> = a free interval of whole local days („de la – până la”, /history), by days.
/// </summary>
public enum ReportGranularity { Day, Week, Month, Year, Range }

/// <summary>
/// One bar of a report: <see cref="StartUtc"/> inclusive, <see cref="EndUtc"/> exclusive. <see cref="Day"/> is the
/// local day the bucket starts on (the day itself for day buckets, the first of the month for month buckets).
/// <see cref="RepeatedHour"/> marks the second 03:00 of the day the clock goes back (25 h day).
/// </summary>
public sealed record ReportBucket(int Index, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTime LocalStart, DateOnly Day, bool RepeatedHour = false);

/// <summary>
/// A calendar period in local time: a day, a week starting on Monday, a month or a year. <see cref="Start"/> is always
/// the first local day of the period (normalised by <see cref="Containing"/>). A free interval (<see cref="Between"/>,
/// kind <see cref="ReportGranularity.Range"/>) also has its last day (<see cref="RangeLastDay"/>; default for the others).
/// </summary>
public readonly record struct ReportPeriod(ReportGranularity Kind, DateOnly Start, DateOnly RangeLastDay = default)
{
    /// <summary>The longest free interval (days): about two years of daily bars.</summary>
    public const int MaxRangeDays = 732;

    /// <summary>
    /// A free interval of whole local days, <paramref name="from"/> to <paramref name="to"/> inclusive (swapped when given
    /// the other way round, cut to <see cref="MaxRangeDays"/> from its start).
    /// </summary>
    public static ReportPeriod Between(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber + 1 > MaxRangeDays) to = from.AddDays(MaxRangeDays - 1);
        return new ReportPeriod(ReportGranularity.Range, from, to);
    }

    /// <summary>The period of the given kind that contains <paramref name="day"/> (weeks start on Monday; a range: that day alone).</summary>
    public static ReportPeriod Containing(ReportGranularity kind, DateOnly day) => kind switch
    {
        ReportGranularity.Range => Between(day, day),
        ReportGranularity.Day => new(kind, day),
        ReportGranularity.Week => new(kind, day.AddDays(-(((int)day.DayOfWeek + 6) % 7))),
        ReportGranularity.Month => new(kind, new DateOnly(day.Year, day.Month, 1)),
        ReportGranularity.Year => new(kind, new DateOnly(day.Year, 1, 1)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>First local day after the period.</summary>
    public DateOnly EndExclusive => Kind switch
    {
        ReportGranularity.Day => Start.AddDays(1),
        ReportGranularity.Week => Start.AddDays(7),
        ReportGranularity.Month => Start.AddMonths(1),
        ReportGranularity.Range => RangeLastDay.AddDays(1),
        _ => Start.AddYears(1),
    };

    /// <summary>Last local day of the period.</summary>
    public DateOnly LastDay => EndExclusive.AddDays(-1);

    public int DayCount => EndExclusive.DayNumber - Start.DayNumber;

    /// <summary>
    /// The period of the same kind right before this one (the day before, the week before, February before March, …; a
    /// range: the same number of days right before it).
    /// </summary>
    public ReportPeriod Previous() => Kind == ReportGranularity.Range
        ? Between(Start.AddDays(-DayCount), Start.AddDays(-1))
        : Containing(Kind, Start.AddDays(-1));

    public ReportPeriod Next() => Kind == ReportGranularity.Range
        ? Between(EndExclusive, EndExclusive.AddDays(DayCount - 1))
        : new(Kind, EndExclusive);

    public bool Contains(DateOnly day) => day >= Start && day < EndExclusive;

    /// <summary>UTC instant of the period's first local midnight.</summary>
    public DateTimeOffset StartUtc(TimeZoneInfo tz) => ShellyEnergyData.LocalMidnightUtc(Start, tz);

    /// <summary>UTC instant right after the period (the next period's first local midnight).</summary>
    public DateTimeOffset EndUtc(TimeZoneInfo tz) => ShellyEnergyData.LocalMidnightUtc(EndExclusive, tz);

    /// <summary>
    /// The bars of the period: local hours for a day (23 on the spring DST day, 25 on the autumn one; built from whole
    /// UTC hours, so zones with a whole-hour offset get exact local hours), local days for a week, a month or a range, months
    /// for a year.
    /// </summary>
    public IReadOnlyList<ReportBucket> Buckets(TimeZoneInfo tz)
    {
        var list = new List<ReportBucket>();
        switch (Kind)
        {
            case ReportGranularity.Day:
            {
                var end = EndUtc(tz);
                var seen = new HashSet<int>();
                for (var t = StartUtc(tz); t < end; t = t.AddHours(1))
                {
                    var local = TimeZoneInfo.ConvertTime(t, tz).DateTime;
                    var repeated = !seen.Add(local.Hour);
                    var next = t.AddHours(1) < end ? t.AddHours(1) : end;
                    list.Add(new ReportBucket(list.Count, t, next, local, Start, repeated));
                }
                break;
            }
            case ReportGranularity.Week:
            case ReportGranularity.Month:
            case ReportGranularity.Range:
                for (var d = Start; d < EndExclusive; d = d.AddDays(1))
                    list.Add(new ReportBucket(list.Count, ShellyEnergyData.LocalMidnightUtc(d, tz), ShellyEnergyData.LocalMidnightUtc(d.AddDays(1), tz),
                        d.ToDateTime(TimeOnly.MinValue), d));
                break;
            default:
                for (var m = Start; m < EndExclusive; m = m.AddMonths(1))
                    list.Add(new ReportBucket(list.Count, ShellyEnergyData.LocalMidnightUtc(m, tz), ShellyEnergyData.LocalMidnightUtc(m.AddMonths(1), tz),
                        m.ToDateTime(TimeOnly.MinValue), m));
                break;
        }
        return list;
    }

    /// <summary>Index of the bucket that contains a local day (day/week/month/year buckets), or -1.</summary>
    public int BucketOfDay(DateOnly day)
    {
        if (!Contains(day)) return -1;
        return Kind switch
        {
            ReportGranularity.Day => 0,
            ReportGranularity.Year => day.Month - 1,
            _ => day.DayNumber - Start.DayNumber,
        };
    }
}

/// <summary>Small numeric rules shared by the reports.</summary>
public static class ReportMath
{
    /// <summary>
    /// Change from <paramref name="previous"/> to <paramref name="current"/> in percent; null when either is missing or
    /// the previous value is zero (no meaningful percentage).
    /// </summary>
    public static double? DeltaPct(double? current, double? previous)
    {
        if (current is not { } c || previous is not { } p) return null;
        if (Math.Abs(p) < 1e-9) return null;
        return (c - p) / Math.Abs(p) * 100.0;
    }

    /// <summary>Sum of the values that exist; null when none exists.</summary>
    public static double? SumOrNull(IEnumerable<double?> values)
    {
        double? total = null;
        foreach (var v in values)
            if (v is { } x) total = (total ?? 0) + x;
        return total;
    }

    /// <summary>True when two totals differ by more than <paramref name="fraction"/> of the larger one.</summary>
    public static bool DiffersBy(double a, double b, double fraction)
    {
        var max = Math.Max(Math.Abs(a), Math.Abs(b));
        return max > 1e-9 && Math.Abs(a - b) / max > fraction;
    }
}
