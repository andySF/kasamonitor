using KasaMonitor.Core.Climate;

namespace KasaMonitor.Core.History;

/// <summary>Min / time-weighted mean / max of one sensor on one local day (<see cref="ClimateAggregation.Stats"/>).</summary>
public sealed record ClimateDayStats(Guid SensorId, DateOnly Day, ClimateStats Stats);

/// <summary>
/// Climate statistics over many windows at once: each window reads only its own slice of the sorted readings (the
/// reading before it, which still holds at its start, plus the ones inside), so a year of days stays linear in the
/// number of readings instead of days × readings.
/// </summary>
public static class ClimateWindows
{
    /// <summary>The stats of each window [From, To) over <paramref name="points"/> (any order).</summary>
    public static IReadOnlyList<ClimateStats> Stats(IReadOnlyList<ClimatePoint> points, IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> windows)
    {
        var sorted = points.OrderBy(p => p.TsUtc).ToArray();
        var result = new List<ClimateStats>(windows.Count);
        foreach (var (from, to) in windows)
        {
            var first = Math.Max(0, FirstAtOrAfter(sorted, from) - 1);
            var end = FirstAfter(sorted, to);
            if (end <= first)
            {
                result.Add(ClimateStats.Empty);
                continue;
            }
            result.Add(ClimateAggregation.Stats(new ArraySegment<ClimatePoint>(sorted, first, end - first), from, to));
        }
        return result;
    }

    /// <summary>
    /// Several days of one sensor combined (a month of a year report): the lowest minimum, the highest maximum and the mean
    /// of the daily means (each day with a value counts once); <see cref="ClimateStats.Empty"/> when none has a value.
    /// </summary>
    public static ClimateStats Combine(IEnumerable<ClimateStats> days)
    {
        var list = days.Where(d => d.AvgC is not null).ToList();
        if (list.Count == 0) return ClimateStats.Empty;
        var hum = list.Where(d => d.AvgHumidity is not null).ToList();
        return new ClimateStats(list.Min(d => d.MinC), list.Average(d => d.AvgC!.Value), list.Max(d => d.MaxC),
            hum.Count == 0 ? null : hum.Min(d => d.MinHumidity), hum.Count == 0 ? null : hum.Average(d => d.AvgHumidity!.Value),
            hum.Count == 0 ? null : hum.Max(d => d.MaxHumidity), list.Sum(d => d.Count));
    }

    private static int FirstAtOrAfter(ClimatePoint[] a, DateTimeOffset t)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (a[mid].TsUtc < t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int FirstAfter(ClimatePoint[] a, DateTimeOffset t)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (a[mid].TsUtc <= t) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
