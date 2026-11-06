namespace KasaMonitor.Core.Reports;

/// <summary>The step of the points of the power chart of /history (0.14.0): <see cref="PowerHistory.ResolutionFor(int)"/>.</summary>
public enum PowerResolution { Minute, Hour, Day }

/// <summary>
/// The comparison chosen on /history (one control for the page, 0.14.0): none (<c>cmp=0</c>), the previous period
/// (default) or the same interval last year (<c>cmp=an</c>; only the „Putere” section has it, the other sections keep
/// comparing with the previous period).
/// </summary>
public enum CompareMode { None, Previous, LastYear }

/// <summary>
/// One stored power aggregate (mW): a minute row (<see cref="Minutes"/> = 1) or an hour row (<see cref="Minutes"/> = the
/// minutes with data in it).
/// </summary>
public readonly record struct PowerAggregate(DateTimeOffset StartUtc, long AvgMw, long MaxMw, int Minutes);

/// <summary>
/// One hour rolled up from its minute rows: <see cref="AvgMw"/> the mean of the minute averages (every minute weighs the
/// same: time-weighted), <see cref="MaxMw"/> the largest minute maximum, <see cref="Minutes"/> the minutes present,
/// <see cref="EnergyWh"/> = average × minutes present (an hour with gaps holds only the energy of its minutes).
/// </summary>
public readonly record struct PowerHour(DateTimeOffset StartUtc, long AvgMw, long MaxMw, int Minutes, long EnergyWh);

/// <summary>
/// One point of the chart: the average and the maximum power (W) of its bucket, <see cref="MaxAtUtc"/> the start of the
/// stored row that holds the maximum (a minute for minute points, an hour otherwise), <see cref="Minutes"/> the minutes with data.
/// </summary>
public sealed record PowerPoint(DateTimeOffset StartUtc, double AvgW, double MaxW, DateTimeOffset MaxAtUtc, int Minutes);

/// <summary>Average (weighted by the minutes with data) and maximum of a run of points; null when none has data.</summary>
public sealed record PowerSummary(double AvgW, double MaxW, DateTimeOffset MaxAtUtc, int Minutes);

/// <summary>
/// The rules of the power chart of /history (0.14.0), from the aggregates only (never the raw readings, which the retention
/// may have deleted): the resolution of a period, its grid of buckets, the points from minute or hour rows, the hourly
/// roll-up, the shift to the same interval last year and the alignment of a comparison on the period's offset.
/// </summary>
public static class PowerHistory
{
    /// <summary>Periods up to this many days get one point per minute (from OneMinuteAggregates).</summary>
    public const int MinuteMaxDays = 2;

    /// <summary>Periods up to this many days get one point per hour (from OneHourAggregates); longer ones one per local day.</summary>
    public const int HourMaxDays = 45;

    public static PowerResolution ResolutionFor(int days) =>
        days <= MinuteMaxDays ? PowerResolution.Minute : days <= HourMaxDays ? PowerResolution.Hour : PowerResolution.Day;

    public static PowerResolution ResolutionFor(ReportPeriod period) => ResolutionFor(period.DayCount);

    /// <summary>
    /// The bucket starts (UTC) of a period: every minute or every UTC hour from its first local midnight (a zone with a
    /// whole-hour offset gets exact local hours; the spring DST day has 23, the autumn one 25), or every local midnight.
    /// </summary>
    public static IReadOnlyList<DateTimeOffset> Grid(ReportPeriod period, PowerResolution resolution, TimeZoneInfo tz)
    {
        var list = new List<DateTimeOffset>();
        if (resolution == PowerResolution.Day)
        {
            for (var d = period.Start; d < period.EndExclusive; d = d.AddDays(1))
                list.Add(ReportPeriod.Containing(ReportGranularity.Day, d).StartUtc(tz));
            return list;
        }
        var step = resolution == PowerResolution.Minute ? TimeSpan.FromMinutes(1) : TimeSpan.FromHours(1);
        var end = period.EndUtc(tz);
        for (var t = period.StartUtc(tz); t < end; t += step) list.Add(t);
        return list;
    }

    /// <summary>
    /// The points of a grid from stored rows: each row goes to the bucket that holds its start (rows outside
    /// [grid[0], <paramref name="endUtc"/>) are ignored); a bucket's average weighs its rows by their minutes, its maximum
    /// is the largest row maximum (the earliest one on a tie). A bucket without rows stays null: a gap, never a zero.
    /// </summary>
    public static IReadOnlyList<PowerPoint?> Points(IReadOnlyList<DateTimeOffset> grid, DateTimeOffset endUtc, IEnumerable<PowerAggregate> rows)
    {
        var n = grid.Count;
        var sum = new double[n];
        var minutes = new int[n];
        var max = new long[n];
        var maxAt = new DateTimeOffset[n];
        foreach (var r in rows)
        {
            if (n == 0 || r.StartUtc < grid[0] || r.StartUtc >= endUtc) continue;
            var i = BucketOf(grid, r.StartUtc);
            var w = Math.Max(1, r.Minutes);
            if (minutes[i] == 0 || r.MaxMw > max[i] || r.MaxMw == max[i] && r.StartUtc < maxAt[i])
            {
                max[i] = r.MaxMw;
                maxAt[i] = r.StartUtc;
            }
            sum[i] += (double)r.AvgMw * w;
            minutes[i] += w;
        }
        var points = new PowerPoint?[n];
        for (var i = 0; i < n; i++)
            if (minutes[i] > 0)
                points[i] = new PowerPoint(grid[i], sum[i] / minutes[i] / 1000.0, max[i] / 1000.0, maxAt[i], minutes[i]);
        return points;
    }

    /// <summary>The index of the last grid start at or before <paramref name="t"/> (the grid is sorted, t ≥ grid[0]).</summary>
    private static int BucketOf(IReadOnlyList<DateTimeOffset> grid, DateTimeOffset t)
    {
        int lo = 0, hi = grid.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (grid[mid] <= t) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The bucket of the grid that holds <paramref name="nowUtc"/>, or null when now is outside the period.</summary>
    public static int? NowIndex(IReadOnlyList<DateTimeOffset> grid, DateTimeOffset endUtc, DateTimeOffset nowUtc) =>
        grid.Count == 0 || nowUtc < grid[0] || nowUtc >= endUtc ? null : BucketOf(grid, nowUtc);

    /// <summary>Average (weighted by minutes) and maximum of the points that have data; null when none has.</summary>
    public static PowerSummary? Summary(IEnumerable<PowerPoint?> points)
    {
        double sum = 0;
        var minutes = 0;
        PowerPoint? top = null;
        foreach (var p in points)
        {
            if (p is null) continue;
            sum += p.AvgW * p.Minutes;
            minutes += p.Minutes;
            if (top is null || p.MaxW > top.MaxW) top = p;
        }
        return top is null || minutes == 0 ? null : new PowerSummary(sum / minutes, top.MaxW, top.MaxAtUtc, minutes);
    }

    /// <summary>
    /// The hours (UTC) of minute rows, one per hour that has minutes: the mean of the minute averages (rounded to the mW),
    /// the largest minute maximum, the minutes present and the energy of those minutes (Wh, rounded).
    /// </summary>
    public static IReadOnlyList<PowerHour> HoursFromMinutes(IEnumerable<PowerAggregate> minutes) =>
        minutes
            .GroupBy(m => m.StartUtc.UtcTicks / TimeSpan.TicksPerHour)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var count = g.Count();
                var avg = (long)Math.Round(g.Average(m => (double)m.AvgMw), MidpointRounding.AwayFromZero);
                return new PowerHour(new DateTimeOffset(g.Key * TimeSpan.TicksPerHour, TimeSpan.Zero), avg, g.Max(m => m.MaxMw), count,
                    (long)Math.Round(avg / 1000.0 * count / 60.0, MidpointRounding.AwayFromZero));
            })
            .ToList();

    /// <summary>
    /// A stored aggregate is replaced only by one built from at least as much data (<paramref name="newCount"/> readings
    /// for a minute, minutes for an hour): once the raw readings of a day are partly or wholly deleted by the retention, a
    /// new roll-up of that day can never overwrite its aggregates with less (and a day with nothing writes nothing).
    /// </summary>
    public static bool Replaces(int storedCount, int newCount) => newCount >= storedCount;

    /// <summary>The period a comparison draws: the previous one (as everywhere on the page), the same interval last year, or none.</summary>
    public static ReportPeriod? ComparisonPeriod(ReportPeriod period, CompareMode mode) => mode switch
    {
        CompareMode.Previous => period.Previous(),
        CompareMode.LastYear => LastYear(period),
        _ => null,
    };

    /// <summary>
    /// „Același interval anul trecut”: a day → the same date a year before (29 February → 28 February); a week → the week
    /// 52 weeks before (Monday to Sunday again, so weekdays meet weekdays: week 45 of 2026, 2–8 November, → 3–9 November
    /// 2025); a month → the same month; a year → the year before; a range → the same dates a year before (an end on
    /// 29 February → 28 February).
    /// </summary>
    public static ReportPeriod LastYear(ReportPeriod period) => period.Kind switch
    {
        ReportGranularity.Day => ReportPeriod.Containing(ReportGranularity.Day, period.Start.AddYears(-1)),
        ReportGranularity.Week => new ReportPeriod(ReportGranularity.Week, period.Start.AddDays(-364)),
        ReportGranularity.Month => ReportPeriod.Containing(ReportGranularity.Month, period.Start.AddYears(-1)),
        ReportGranularity.Year => ReportPeriod.Containing(ReportGranularity.Year, period.Start.AddYears(-1)),
        _ => ReportPeriod.Between(period.Start.AddYears(-1), period.LastDay.AddYears(-1)),
    };

    /// <summary>
    /// The comparison's points on the current grid, aligned on the wall-clock offset from each period's first local midnight
    /// (hour of the day, day of the period): 14:00 of the 3rd day meets 14:00 of the 3rd day of the comparison. A bucket
    /// without a counterpart stays empty (the 31st against a 30-day month; on the spring DST day the comparison's 03:00 has
    /// no slot); the repeated 03:00 of an autumn DST day meets the comparison's first 03:00.
    /// </summary>
    public static IReadOnlyList<PowerPoint?> Align(ReportPeriod current, IReadOnlyList<DateTimeOffset> currentGrid, ReportPeriod comparison,
        IReadOnlyList<DateTimeOffset> comparisonGrid, IReadOnlyList<PowerPoint?> comparisonPoints, TimeZoneInfo tz)
    {
        var byOffset = new Dictionary<long, int>();
        var compStart = comparison.Start.ToDateTime(TimeOnly.MinValue);
        for (var j = 0; j < comparisonGrid.Count; j++)
            byOffset.TryAdd((TimeZoneInfo.ConvertTime(comparisonGrid[j], tz).DateTime - compStart).Ticks, j);
        var curStart = current.Start.ToDateTime(TimeOnly.MinValue);
        var aligned = new PowerPoint?[currentGrid.Count];
        for (var i = 0; i < currentGrid.Count; i++)
            if (byOffset.TryGetValue((TimeZoneInfo.ConvertTime(currentGrid[i], tz).DateTime - curStart).Ticks, out var j) && j < comparisonPoints.Count)
                aligned[i] = comparisonPoints[j];
        return aligned;
    }

    /// <summary>
    /// The figures of the comparison: a finished period against the whole comparison; a period in progress against the
    /// same part of it (its aligned points up to the bucket of now, as „la aceeași oră” elsewhere on the page).
    /// </summary>
    public static PowerSummary? ComparisonSummary(IReadOnlyList<PowerPoint?> comparisonPoints, IReadOnlyList<PowerPoint?> aligned, int? nowIndex) =>
        nowIndex is { } k ? Summary(aligned.Take(k + 1)) : Summary(comparisonPoints);

    /// <summary>The series a comparison draws (one, to keep the chart readable): the main meter when shown, else the first selected.</summary>
    public static Guid? Reference(IReadOnlyList<Guid> selected, Guid? mainMeter) =>
        mainMeter is { } m && selected.Contains(m) ? m : selected.Count > 0 ? selected[0] : null;
}
