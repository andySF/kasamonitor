using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.History;

/// <summary>
/// The boiler's cumulative burner counters in one UTC hour (HeatingReadings grouped in SQL): the lowest and highest
/// burner hours and starts read in it; null when no reading of the hour had the counter.
/// </summary>
public sealed record BurnerCounterHour(DateTimeOffset HourUtc, double? MinHours, double? MaxHours, long? MinStarts, long? MaxStarts);

/// <summary>Burner hours and starts of one report bucket (null = no reading in it).</summary>
public sealed record BurnerBucket(double? Hours, long? Starts);

/// <summary>
/// Burner hours and starts per bucket from the cumulative counters: each hour adds its highest value minus the highest
/// value of the hour before it that had the counter (its own lowest value for the first one), so a rise between two
/// hours is never lost and nothing is counted twice. A lower value than before (a counter replaced or reset) adds only the
/// rise inside its own hour. Hours before the first bucket only serve as the reference of the first one.
/// </summary>
public static class BurnerCounters
{
    public static IReadOnlyList<BurnerBucket> PerBucket(IReadOnlyList<BurnerCounterHour> hours, IReadOnlyList<ReportBucket> buckets)
    {
        var hoursSum = new double?[buckets.Count];
        var startsSum = new long?[buckets.Count];
        double? prevHours = null;
        long? prevStarts = null;
        foreach (var h in hours.OrderBy(x => x.HourUtc))
        {
            var i = Index(buckets, h.HourUtc);
            if (h.MaxHours is { } maxH)
            {
                var inc = prevHours is { } p && maxH >= p ? maxH - p : maxH - (h.MinHours ?? maxH);
                if (i >= 0) hoursSum[i] = (hoursSum[i] ?? 0) + Math.Max(0, inc);
                prevHours = maxH;
            }
            if (h.MaxStarts is { } maxS)
            {
                var inc = prevStarts is { } p && maxS >= p ? maxS - p : maxS - (h.MinStarts ?? maxS);
                if (i >= 0) startsSum[i] = (startsSum[i] ?? 0) + Math.Max(0, inc);
                prevStarts = maxS;
            }
        }
        return buckets.Select((_, i) => new BurnerBucket(hoursSum[i], startsSum[i])).ToList();
    }

    /// <summary>The bucket holding <paramref name="t"/> (buckets are contiguous and ordered), or -1.</summary>
    public static int Index(IReadOnlyList<ReportBucket> buckets, DateTimeOffset t)
    {
        int lo = 0, hi = buckets.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (t < buckets[mid].StartUtc) hi = mid - 1;
            else if (t >= buckets[mid].EndUtc) lo = mid + 1;
            else return mid;
        }
        return -1;
    }
}
