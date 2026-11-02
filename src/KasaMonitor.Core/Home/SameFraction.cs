using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.Home;

/// <summary>
/// „La aceeași parte a perioadei”: a period in progress is compared with the same share of the previous one (the week up to
/// Wednesday 15:00 with the previous week up to Wednesday 15:00), not with the whole previous period. Positions are by bucket
/// index (day of the week or month, month of the year) plus the elapsed fraction of the current bucket.
/// </summary>
public static class SameFraction
{
    /// <summary>The bucket that holds <paramref name="nowUtc"/> and the elapsed fraction of it; null when now is outside.</summary>
    public static (int Index, double Fraction)? Position(IReadOnlyList<ReportBucket> buckets, DateTimeOffset nowUtc)
    {
        for (var i = 0; i < buckets.Count; i++)
        {
            var b = buckets[i];
            if (nowUtc < b.StartUtc || nowUtc >= b.EndUtc) continue;
            var len = (b.EndUtc - b.StartUtc).TotalSeconds;
            return (i, len <= 0 ? 0 : Math.Clamp((nowUtc - b.StartUtc).TotalSeconds / len, 0, 1));
        }
        return null;
    }

    /// <summary>
    /// Σ of the previous period's buckets before <paramref name="position"/>'s index, plus that bucket × the fraction
    /// (a bucket beyond the previous period's end is left out); null when none of them has data.
    /// </summary>
    public static double? Previous(IReadOnlyList<double?> previous, (int Index, double Fraction) position)
    {
        double? sum = null;
        for (var i = 0; i <= position.Index && i < previous.Count; i++)
        {
            if (previous[i] is not { } v) continue;
            sum = (sum ?? 0) + (i < position.Index ? v : v * position.Fraction);
        }
        return sum;
    }
}
