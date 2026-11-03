namespace KasaMonitor.Core.Devices.Entities;

/// <summary>One partial value for a history bucket: its time, min / mean / max and the weight of the mean (seconds covered, or 1).</summary>
public readonly record struct EntityPoint(DateTimeOffset TsUtc, double Min, double Avg, double Max, double Weight = 1)
{
    public static EntityPoint Of(DateTimeOffset ts, double v) => new(ts, v, v, v);
}

/// <summary>The fixed-size buckets of the device page charts (pure).</summary>
public static class EntityHistoryBuckets
{
    /// <summary>
    /// Groups points into consecutive buckets of <paramref name="size"/> from <paramref name="fromUtc"/> to
    /// <paramref name="toUtc"/> (exclusive): min of the mins, max of the maxes, mean weighted by <see cref="EntityPoint.Weight"/>.
    /// A bucket without a point has no values (a gap in the line, not a zero).
    /// </summary>
    public static IReadOnlyList<EntityBucket> Build(IEnumerable<EntityPoint> points, DateTimeOffset fromUtc, DateTimeOffset toUtc, TimeSpan size)
    {
        if (size <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(size));
        var count = Math.Max(0, (int)Math.Ceiling((toUtc - fromUtc) / size));
        var min = new double?[count];
        var max = new double?[count];
        var sum = new double[count];
        var weight = new double[count];
        foreach (var p in points)
        {
            if (p.TsUtc < fromUtc || p.TsUtc >= toUtc) continue;
            var i = (int)((p.TsUtc - fromUtc) / size);
            if (i < 0 || i >= count) continue;
            min[i] = min[i] is { } a ? Math.Min(a, p.Min) : p.Min;
            max[i] = max[i] is { } b ? Math.Max(b, p.Max) : p.Max;
            var w = p.Weight > 0 ? p.Weight : 1;
            sum[i] += p.Avg * w;
            weight[i] += w;
        }
        var result = new List<EntityBucket>(count);
        for (var i = 0; i < count; i++)
            result.Add(new EntityBucket(fromUtc + size * i, min[i], weight[i] > 0 ? sum[i] / weight[i] : null, max[i]));
        return result;
    }
}
