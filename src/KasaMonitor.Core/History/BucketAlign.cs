using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.History;

/// <summary>
/// Values of one set of bars moved onto another set by time: each source bar lands on the target bar that holds its start
/// (the boiler's hours, which begin at 01:00 Romanian time, onto the local hours of the meter). A source bar outside every
/// target bar is left out; two landing on the same target add up.
/// </summary>
public static class BucketAlign
{
    public static IReadOnlyList<double?> ByStart(IReadOnlyList<ReportBucket> source, IReadOnlyList<double?> values, IReadOnlyList<ReportBucket> target)
    {
        var result = new double?[target.Count];
        for (var i = 0; i < source.Count && i < values.Count; i++)
        {
            if (values[i] is not { } v) continue;
            var j = BurnerCounters.Index(target, source[i].StartUtc);
            if (j >= 0) result[j] = (result[j] ?? 0) + v;
        }
        return result;
    }
}
