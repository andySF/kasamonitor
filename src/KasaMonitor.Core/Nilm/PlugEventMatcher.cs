namespace KasaMonitor.Core.Nilm;

/// <summary>One 10-second reading of a plug, in W.</summary>
public readonly record struct PlugPowerPoint(DateTimeOffset TsUtc, double PowerW);

/// <summary>
/// The readings of one plug around an event. <see cref="CanLabel"/> = false for a plug excluded from learning: it cannot
/// label the event, but it still counts when deciding whether more than one plug moved.
/// </summary>
public sealed record PlugSeries(Guid PlugId, string Alias, IReadOnlyList<PlugPowerPoint> Points, bool CanLabel = true);

/// <summary>
/// A plug whose consumption changed like the event: <see cref="DeltaW"/> measured by the plug, <see cref="ErrorW"/> =
/// |plug Δ − event ΔP|, between the readings at <see cref="FromUtc"/> and <see cref="ToUtc"/> (one plug change labels at
/// most one event: the readings of the step are marked as used).
/// </summary>
public sealed record PlugMatch(Guid PlugId, string Alias, double DeltaW, double ErrorW, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>
/// Automatic labels from the plugs. The main meter event at <c>Ts</c> with ΔP is the plug that made a clean step:
/// <list type="bullet">
/// <item>the change happened inside Ts ± <see cref="Window"/>: the middle of the interval between the two readings
/// (consecutive, or two apart to skip a reading taken during a motor's inrush) is in the window, readings at most
/// <see cref="MaxReadingGap"/> apart;</item>
/// <item>the plug was steady before and after: the reading before the step and the one after it agree with the step's
/// readings within the tolerance (a variable load is not a step);</item>
/// <item>the step equals ΔP ± max(<see cref="MinToleranceW"/>, <see cref="ToleranceRatio"/> × |ΔP|);</item>
/// <item>it is the only plug that changed in the same direction by ≥ <see cref="AmbiguousShare"/> of |ΔP| in the
/// window, counting every plug, also those excluded from learning (two plugs that moved at once: no label);</item>
/// <item>that plug change did not already label another event (<c>used</c>).</item>
/// </list>
/// </summary>
public static class PlugEventMatcher
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(15);
    public const double MinToleranceW = 10;
    public const double ToleranceRatio = 0.15;
    public const double AmbiguousShare = 0.6;

    /// <summary>Longest interval between the two readings of a plug change (a missed poll is fine, a long outage is not).</summary>
    public static readonly TimeSpan MaxReadingGap = TimeSpan.FromSeconds(25);

    /// <summary>How much plug data is needed around an event: the window plus two readings on each side.</summary>
    public static readonly TimeSpan LoadMargin = Window + MaxReadingGap + MaxReadingGap;

    public static double Tolerance(double deltaP) => Math.Max(MinToleranceW, ToleranceRatio * Math.Abs(deltaP));

    public static PlugMatch? Match(DateTimeOffset eventTs, double deltaP, IEnumerable<PlugSeries> plugs, ISet<(Guid, DateTimeOffset)>? used = null)
    {
        var tol = Tolerance(deltaP);
        var from = eventTs - Window;
        var to = eventTs + Window;
        PlugMatch? best = null;
        var moved = 0;
        var all = plugs.ToList();
        foreach (var plug in all)
        {
            var pts = plug.Points.OrderBy(p => p.TsUtc).ToList();
            PlugMatch? bestOfPlug = null;
            var plugMoved = false;
            for (var i = 0; i < pts.Count; i++)
            {
                for (var j = i + 1; j <= i + 2 && j < pts.Count; j++)
                {
                    var a = pts[i];
                    var b = pts[j];
                    if (b.TsUtc - a.TsUtc > MaxReadingGap) continue;
                    var middle = a.TsUtc + (b.TsUtc - a.TsUtc) / 2;
                    if (middle < from || middle > to) continue;
                    var d = b.PowerW - a.PowerW;
                    if (Math.Sign(d) == Math.Sign(deltaP) && Math.Abs(d) >= AmbiguousShare * Math.Abs(deltaP)) plugMoved = true;
                    var err = Math.Abs(d - deltaP);
                    if (err > tol) continue;
                    // Steady before and after: a reading on each side that agrees with the step's ends.
                    if (i == 0 || j == pts.Count - 1) continue;
                    if (Math.Abs(pts[i - 1].PowerW - a.PowerW) > tol || Math.Abs(pts[j + 1].PowerW - b.PowerW) > tol) continue;
                    if (used is not null && (used.Contains((plug.PlugId, a.TsUtc)) || used.Contains((plug.PlugId, b.TsUtc)))) continue;
                    // Equal error: the consecutive pair is the real step (the skip-one pair only bridges an inrush reading).
                    if (bestOfPlug is null || err < bestOfPlug.ErrorW || (err == bestOfPlug.ErrorW && j == i + 1))
                        bestOfPlug = new PlugMatch(plug.PlugId, plug.Alias, d, err, a.TsUtc, b.TsUtc);
                }
            }
            if (plugMoved) moved++;
            if (plug.CanLabel && bestOfPlug is not null && (best is null || bestOfPlug.ErrorW < best.ErrorW)) best = bestOfPlug;
        }
        if (best is null || moved > 1) return null;
        if (used is not null)
            foreach (var p in all.First(x => x.PlugId == best.PlugId).Points.Where(p => p.TsUtc >= best.FromUtc && p.TsUtc <= best.ToUtc))
                used.Add((best.PlugId, p.TsUtc));
        return best;
    }
}
