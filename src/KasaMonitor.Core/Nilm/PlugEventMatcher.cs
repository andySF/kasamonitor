namespace KasaMonitor.Core.Nilm;

/// <summary>One reading of a plug (10-second poll, or a sample of its live stream), in W.</summary>
public readonly record struct PlugPowerPoint(DateTimeOffset TsUtc, double PowerW);

/// <summary>
/// The readings of one plug around an event. <see cref="CanLabel"/> = false for a plug that does not label
/// (<see cref="KasaMonitor.Core.Models.Plug.LabelsNilmEvents"/>; excluded from learning, without a single appliance): it cannot
/// label the event, but it still counts when deciding whether more than one plug moved.
/// </summary>
public sealed record PlugSeries(Guid PlugId, string Alias, IReadOnlyList<PlugPowerPoint> Points, bool CanLabel = true);

/// <summary>
/// A plug whose consumption changed like the event: <see cref="DeltaW"/> measured by the plug (median after − median
/// before), <see cref="ErrorW"/> = |plug Δ − event ΔP|; the step lies between the last reading before
/// (<see cref="FromUtc"/>) and the first reading after (<see cref="ToUtc"/>).
/// </summary>
public sealed record PlugMatch(Guid PlugId, string Alias, double DeltaW, double ErrorW, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>A plug step that already labelled an event (one plug change labels at most one event).</summary>
public readonly record struct UsedPlugStep(Guid PlugId, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int Sign)
{
    public static UsedPlugStep Of(PlugMatch m) => new(m.PlugId, m.FromUtc, m.ToUtc, Math.Sign(m.DeltaW));

    /// <summary>The same plug step: same plug, same direction, overlapping intervals between the readings.</summary>
    public bool Overlaps(UsedPlugStep o) => PlugId == o.PlugId && Sign == o.Sign && FromUtc < o.ToUtc && o.FromUtc < ToUtc;
}

/// <summary>
/// The main meter's events just before and after the one being labelled: the plug windows stop
/// <see cref="PlugEventMatcher.Inner"/> before them (another step inside a window would move its median).
/// </summary>
public readonly record struct Neighbours(DateTimeOffset? PreviousUtc, DateTimeOffset? NextUtc);

/// <summary>
/// Automatic labels from the plugs, on <b>stabilised levels</b>. The main meter event at <c>t</c> with ΔP is the plug
/// whose level changed by the same amount:
/// <list type="bullet">
/// <item>level before = median of the plug's readings in [t − <see cref="Outer"/>, t − <see cref="Inner"/>], level after =
/// median of those in [t + <see cref="Inner"/>, t + <see cref="Outer"/>] (at least one reading in each window); both
/// windows stop <see cref="Inner"/> before the previous / next event of the main meter (<see cref="Neighbours"/>); the
/// readings inside ±<see cref="Inner"/> are ignored, so a reading taken in the middle of a ramp or of an inrush does not
/// count, and in each window a stray reading is outvoted by the median;</item>
/// <item>the last reading before and the first reading after are at most <see cref="MaxReadingGap"/> apart (a long
/// outage is not a step);</item>
/// <item>the step equals ΔP ± max(<see cref="MinToleranceW"/>, <see cref="ToleranceRatio"/> × |ΔP|);</item>
/// <item>it is the only plug whose level moved in the same direction by ≥ <see cref="AmbiguousShare"/> of |ΔP|, counting
/// every plug, also those excluded from learning (two plugs that moved at once: no label);</item>
/// <item>that plug step did not already label another event (<c>used</c>).</item>
/// </list>
/// The readings of a plug are its live stream held on a 1-second grid when the stream covers both windows, else its
/// 10-second polls (<see cref="Readings"/>).
/// </summary>
public static class PlugEventMatcher
{
    /// <summary>Readings closer than this to the event (or to a neighbouring event) are ignored (the step itself, a ramp, an inrush).</summary>
    public static readonly TimeSpan Inner = TimeSpan.FromSeconds(3);

    /// <summary>The windows of the stabilised levels end this far from the event.</summary>
    public static readonly TimeSpan Outer = TimeSpan.FromSeconds(20);

    public const double MinToleranceW = 10;
    public const double ToleranceRatio = 0.15;
    public const double AmbiguousShare = 0.6;

    /// <summary>Longest interval between the last reading before and the first reading after the step (a long outage is not a step).</summary>
    public static readonly TimeSpan MaxReadingGap = TimeSpan.FromSeconds(25);

    /// <summary>
    /// A stream value holds at most this long after its row: the live stream stores a row on a change, or every 60 s
    /// (<c>ChangeRule</c>); older than that, the stream was not running.
    /// </summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(60);

    /// <summary>How much plug data is needed around an event: the two windows, plus the hold of the row in effect before the first.</summary>
    public static readonly TimeSpan LoadMargin = Outer + MaxHold;

    /// <summary>The two windows around <paramref name="eventTs"/>, cut <see cref="Inner"/> before the neighbouring events.</summary>
    public static (DateTimeOffset BeforeFrom, DateTimeOffset BeforeTo, DateTimeOffset AfterFrom, DateTimeOffset AfterTo) Windows(
        DateTimeOffset eventTs, Neighbours neighbours = default)
    {
        var beforeFrom = eventTs - Outer;
        if (neighbours.PreviousUtc is { } p && p + Inner > beforeFrom) beforeFrom = p + Inner;
        var afterTo = eventTs + Outer;
        if (neighbours.NextUtc is { } n && n - Inner < afterTo) afterTo = n - Inner;
        return (beforeFrom, eventTs - Inner, eventTs + Inner, afterTo);
    }

    /// <summary>
    /// The readings of one plug to compare around <paramref name="eventTs"/>: its live stream held on a 1-second grid
    /// (<see cref="Hold"/>) when that covers both full windows, else its 10-second polls. The same function serves the live
    /// labelling (every sample in memory) and the backfill (rows stored on change): the grid makes both the same level.
    /// </summary>
    public static IReadOnlyList<PlugPowerPoint> Readings(DateTimeOffset eventTs, IReadOnlyList<PlugPowerPoint> stream, IReadOnlyList<PlugPowerPoint> polled)
    {
        if (stream.Count == 0) return polled;
        var sorted = stream.OrderBy(p => p.TsUtc).ToList();
        var w = Windows(eventTs);
        var before = Hold(sorted, w.BeforeFrom, w.BeforeTo);
        var after = Hold(sorted, w.AfterFrom, w.AfterTo);
        return before.Count == 0 || after.Count == 0 ? polled : before.Concat(after).ToList();
    }

    /// <summary>
    /// A series stored on change, held on a 1-second grid over [from, to]: at every whole second, the value of the last
    /// row at or before it, if that row is at most <see cref="MaxHold"/> old (a stable level counts once per second, a
    /// ramp only for the seconds it lasted).
    /// </summary>
    public static List<PlugPowerPoint> Hold(IReadOnlyList<PlugPowerPoint> sorted, DateTimeOffset from, DateTimeOffset to)
    {
        var list = new List<PlugPowerPoint>();
        var start = new DateTimeOffset(from.UtcTicks + (TimeSpan.TicksPerSecond - from.UtcTicks % TimeSpan.TicksPerSecond) % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        var i = -1;
        for (var s = start; s <= to; s = s.AddSeconds(1))
        {
            while (i + 1 < sorted.Count && sorted[i + 1].TsUtc <= s) i++;
            if (i >= 0 && s - sorted[i].TsUtc <= MaxHold) list.Add(new PlugPowerPoint(s, sorted[i].PowerW));
        }
        return list;
    }

    public static double Tolerance(double deltaP) => Math.Max(MinToleranceW, ToleranceRatio * Math.Abs(deltaP));

    /// <summary>Median of a non-empty list (mean of the two middle values for an even count).</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>
    /// The plug's step around <paramref name="eventTs"/> (median after − median before, windows cut at the neighbouring
    /// events), or null without readings in both windows or across a long gap.
    /// </summary>
    public static (double DeltaW, DateTimeOffset FromUtc, DateTimeOffset ToUtc)? Step(DateTimeOffset eventTs, IReadOnlyList<PlugPowerPoint> points,
        Neighbours neighbours = default)
    {
        var w = Windows(eventTs, neighbours);
        var before = points.Where(p => p.TsUtc >= w.BeforeFrom && p.TsUtc <= w.BeforeTo).OrderBy(p => p.TsUtc).ToList();
        var after = points.Where(p => p.TsUtc >= w.AfterFrom && p.TsUtc <= w.AfterTo).OrderBy(p => p.TsUtc).ToList();
        if (before.Count == 0 || after.Count == 0) return null;
        var from = before[^1].TsUtc;
        var to = after[0].TsUtc;
        if (to - from > MaxReadingGap) return null;
        return (Median(after.Select(p => p.PowerW).ToList()) - Median(before.Select(p => p.PowerW).ToList()), from, to);
    }

    public static PlugMatch? Match(DateTimeOffset eventTs, double deltaP, IEnumerable<PlugSeries> plugs, ICollection<UsedPlugStep>? used = null,
        Neighbours neighbours = default)
    {
        var tol = Tolerance(deltaP);
        PlugMatch? best = null;
        var moved = 0;
        foreach (var plug in plugs)
        {
            if (Step(eventTs, plug.Points, neighbours) is not { } step) continue;
            var d = step.DeltaW;
            if (Math.Sign(d) == Math.Sign(deltaP) && Math.Abs(d) >= AmbiguousShare * Math.Abs(deltaP)) moved++;
            var err = Math.Abs(d - deltaP);
            if (!plug.CanLabel || err > tol) continue;
            var m = new PlugMatch(plug.PlugId, plug.Alias, d, err, step.FromUtc, step.ToUtc);
            if (used is not null && used.Any(u => u.Overlaps(UsedPlugStep.Of(m)))) continue;
            if (best is null || err < best.ErrorW) best = m;
        }
        if (best is null || moved > 1) return null;
        used?.Add(UsedPlugStep.Of(best));
        return best;
    }
}
