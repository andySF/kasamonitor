namespace KasaMonitor.Core.Climate;

/// <summary>A stored value of one sensor, as used by the aggregations (oldest first).</summary>
public sealed record ClimatePoint(DateTimeOffset TsUtc, double TemperatureC, double? HumidityPercent);

/// <summary>Min / time-weighted mean / max of a period; null when the sensor has no value in it.</summary>
public sealed record ClimateStats(
    double? MinC,
    double? AvgC,
    double? MaxC,
    double? MinHumidity,
    double? AvgHumidity,
    double? MaxHumidity,
    int Count)
{
    public static readonly ClimateStats Empty = new(null, null, null, null, null, null, 0);
}

/// <summary>One chart bucket: the time-weighted mean in [Start, Start + bucket); null = no value in it (a gap).</summary>
public sealed record ClimateBucket(DateTimeOffset StartUtc, double? TemperatureC, double? HumidityPercent);

/// <summary>
/// Period aggregates of the readings stored on change. Each reading holds until the next one, but at most
/// <see cref="MaxHold"/> (two heartbeats): a longer silence is a gap, not the old value. The mean is weighted by how
/// long each value held, so a series that changed often is not over-counted; min and max are over the values that
/// held inside the period (a reading just before the period counts while it still holds).
/// </summary>
public static class ClimateAggregation
{
    public static readonly TimeSpan MaxHold = ClimateWriteRule.Heartbeat * 2;

    /// <summary>Chart bucket of a period: 5 min up to 24 h, 30 min up to 7 days, 2 h beyond (≈ 300 points per line).</summary>
    public static TimeSpan BucketFor(TimeSpan span) =>
        span <= TimeSpan.FromDays(1) ? TimeSpan.FromMinutes(5)
        : span <= TimeSpan.FromDays(7) ? TimeSpan.FromMinutes(30)
        : TimeSpan.FromHours(2);

    private readonly record struct Segment(DateTimeOffset From, DateTimeOffset To, double TemperatureC, double? HumidityPercent);

    /// <summary>The parts of [from, to) during which each reading held (sorted input; unsorted is sorted here).</summary>
    private static IEnumerable<Segment> Segments(IReadOnlyList<ClimatePoint> points, DateTimeOffset from, DateTimeOffset to, TimeSpan maxHold)
    {
        var sorted = IsSorted(points) ? points : points.OrderBy(p => p.TsUtc).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var p = sorted[i];
            var end = p.TsUtc + maxHold;
            if (i + 1 < sorted.Count && sorted[i + 1].TsUtc < end) end = sorted[i + 1].TsUtc;
            if (end > to) end = to;
            var start = p.TsUtc < from ? from : p.TsUtc;
            if (end > start) yield return new Segment(start, end, p.TemperatureC, p.HumidityPercent);
        }
    }

    private static bool IsSorted(IReadOnlyList<ClimatePoint> points)
    {
        for (var i = 1; i < points.Count; i++)
            if (points[i].TsUtc < points[i - 1].TsUtc) return false;
        return true;
    }

    public static ClimateStats Stats(IReadOnlyList<ClimatePoint> points, DateTimeOffset from, DateTimeOffset to, TimeSpan? maxHold = null)
    {
        var count = points.Count(p => p.TsUtc >= from && p.TsUtc <= to);
        double? minT = null, maxT = null, minH = null, maxH = null;
        double sumT = 0, durT = 0, sumH = 0, durH = 0;
        foreach (var s in Segments(points, from, to, maxHold ?? MaxHold))
        {
            var d = (s.To - s.From).TotalSeconds;
            sumT += s.TemperatureC * d;
            durT += d;
            minT = minT is { } a ? Math.Min(a, s.TemperatureC) : s.TemperatureC;
            maxT = maxT is { } b ? Math.Max(b, s.TemperatureC) : s.TemperatureC;
            if (s.HumidityPercent is { } h)
            {
                sumH += h * d;
                durH += d;
                minH = minH is { } c ? Math.Min(c, h) : h;
                maxH = maxH is { } e ? Math.Max(e, h) : h;
            }
        }
        if (durT <= 0)
        {
            // Only readings without duration in the period (e.g. one exactly at its end): their plain values.
            var inside = points.Where(p => p.TsUtc >= from && p.TsUtc <= to).ToList();
            if (inside.Count == 0) return ClimateStats.Empty;
            var hum = inside.Where(p => p.HumidityPercent is not null).Select(p => p.HumidityPercent!.Value).ToList();
            return new ClimateStats(inside.Min(p => p.TemperatureC), inside.Average(p => p.TemperatureC), inside.Max(p => p.TemperatureC),
                hum.Count == 0 ? null : hum.Min(), hum.Count == 0 ? null : hum.Average(), hum.Count == 0 ? null : hum.Max(), count);
        }
        return new ClimateStats(minT, sumT / durT, maxT, minH, durH > 0 ? sumH / durH : null, maxH, count);
    }

    /// <summary>
    /// Every bucket of [from, to) (<paramref name="bucket"/> wide, the first starting at <paramref name="from"/>), with the
    /// time-weighted mean of the values that held in it; buckets without any value are null (the chart line breaks).
    /// </summary>
    public static IReadOnlyList<ClimateBucket> Buckets(IReadOnlyList<ClimatePoint> points, DateTimeOffset from, DateTimeOffset to, TimeSpan bucket,
        TimeSpan? maxHold = null)
    {
        if (bucket <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(bucket));
        var n = Math.Max(0, (int)Math.Ceiling((to - from) / bucket));
        var sumT = new double[n];
        var durT = new double[n];
        var sumH = new double[n];
        var durH = new double[n];
        foreach (var s in Segments(points, from, to, maxHold ?? MaxHold))
        {
            var at = s.From;
            while (at < s.To)
            {
                var index = (int)((at - from) / bucket);
                if (index >= n) break;
                var bucketEnd = from + bucket * (index + 1);
                var end = bucketEnd < s.To ? bucketEnd : s.To;
                var d = (end - at).TotalSeconds;
                sumT[index] += s.TemperatureC * d;
                durT[index] += d;
                if (s.HumidityPercent is { } h)
                {
                    sumH[index] += h * d;
                    durH[index] += d;
                }
                at = end;
            }
        }
        var result = new List<ClimateBucket>(n);
        for (var i = 0; i < n; i++)
            result.Add(new ClimateBucket(from + bucket * i, durT[i] > 0 ? sumT[i] / durT[i] : null, durH[i] > 0 ? sumH[i] / durH[i] : null));
        return result;
    }
}
