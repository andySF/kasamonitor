using KasaMonitor.Core.Nilm;
using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.History;

/// <summary>
/// One detected appliance (a NILM signature without a plug) over a period: its energy inside the period, the runs that
/// started in it, their mean duration (closed runs) and how many started in each local hour of the day.
/// </summary>
public sealed record DetectedApplianceRow(long SignatureId, string Name, double Kwh, int Runs, double? MeanMinutes, IReadOnlyList<int> StartsByHour);

public static class DetectedApplianceStats
{
    /// <summary>
    /// Per signature (<paramref name="names"/>: only the detected ones, the others are left out), largest energy first;
    /// a signature without energy and without a run in the period is left out. Energy = <see cref="ApplianceEnergy.InRange"/>
    /// (an open run up to <paramref name="nowUtc"/>), the same rule as the home page.
    /// </summary>
    public static IReadOnlyList<DetectedApplianceRow> Build(IEnumerable<ApplianceRun> runs, IReadOnlyDictionary<long, string> names,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc, TimeZoneInfo tz) =>
        runs.Where(r => names.ContainsKey(r.SignatureId))
            .GroupBy(r => r.SignatureId)
            .Select(g =>
            {
                var started = g.Where(r => r.StartUtc >= fromUtc && r.StartUtc < toUtc).ToList();
                var closed = started.Where(r => r.EndUtc is not null).Select(r => (r.EndUtc!.Value - r.StartUtc).TotalMinutes).ToList();
                var hours = new int[24];
                foreach (var r in started) hours[TimeZoneInfo.ConvertTime(r.StartUtc, tz).Hour]++;
                return new DetectedApplianceRow(g.Key, names[g.Key], g.Sum(r => ApplianceEnergy.InRange(r, fromUtc, toUtc, nowUtc)) / 1000.0,
                    started.Count, closed.Count == 0 ? null : closed.Average(), hours);
            })
            .Where(x => x.Kwh > 0 || x.Runs > 0)
            .OrderByDescending(x => x.Kwh).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>The energy of each signature's runs per bucket (kWh, <see cref="ApplianceEnergy.InRange"/>, an open run up to now).</summary>
    public static IReadOnlyDictionary<long, IReadOnlyList<double>> PerBucket(IReadOnlyList<ApplianceRun> runs, IReadOnlyList<ReportBucket> buckets,
        DateTimeOffset nowUtc)
    {
        var result = new Dictionary<long, double[]>();
        if (buckets.Count == 0) return new Dictionary<long, IReadOnlyList<double>>();
        foreach (var r in runs)
        {
            if (!result.TryGetValue(r.SignatureId, out var values)) result[r.SignatureId] = values = new double[buckets.Count];
            var end = r.EndUtc ?? nowUtc;
            var first = Math.Max(0, BurnerCounters.Index(buckets, r.StartUtc < buckets[0].StartUtc ? buckets[0].StartUtc : r.StartUtc));
            for (var i = first; i < buckets.Count && buckets[i].StartUtc <= end; i++)
                values[i] += ApplianceEnergy.InRange(r, buckets[i].StartUtc, buckets[i].EndUtc, nowUtc) / 1000.0;
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double>)kv.Value);
    }
}
