using System.Globalization;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;

namespace KasaMonitor.NilmReplay;

/// <summary>One stored second of the main meter.</summary>
public sealed record MeterRow(long Ts, double PowerW, double? ApparentVa, double? Pf);

/// <summary>Two detected events of the two modes taken as the same step (same direction, within the time tolerance).</summary>
public sealed record EventMatch(DetectedEvent Actual, DetectedEvent Median)
{
    public double SecondsApart => (Median.TsUtc - Actual.TsUtc).TotalSeconds;
    public double DeltaPDifference => Median.DeltaP - Actual.DeltaP;
    public bool RiseChanged => Math.Abs(Median.RiseSeconds - Actual.RiseSeconds) >= 1;
    public bool TransientChanged => Math.Abs(Median.TransientRatio - Actual.TransientRatio) > 0.1;

    /// <summary>The transient is compared in one mode and unknown in the other (<see cref="TransientRule"/>; switch-ons only).</summary>
    public bool TransientKnownChanged => Actual.DeltaP > 0
        && TransientRule.IsKnown(Actual.RiseSeconds, Actual.TransientRatio) != TransientRule.IsKnown(Median.RiseSeconds, Median.TransientRatio);
}

/// <summary>The comparison of the two modes over one series.</summary>
public sealed record ReplayComparison(
    IReadOnlyList<DetectedEvent> Actual,
    IReadOnlyList<DetectedEvent> Median,
    IReadOnlyList<EventMatch> Matches,
    IReadOnlyList<DetectedEvent> OnlyActual,
    IReadOnlyList<DetectedEvent> OnlyMedian);

/// <summary>
/// Runs <see cref="MeterEventDetector"/>, unchanged, on the seconds of a meter in two modes. <b>Actual</b>: the series as
/// the service feeds it. <b>Median</b>: every second replaced by the second, among itself and its two neighbours, whose
/// power is the median, so power, apparent power and power factor stay from one measurement; a step that lasts at least
/// 2 s keeps its second, a spike of one second is gone. The levels come from that series; the transient ratio of each event
/// is then computed from the raw seconds, as the median would otherwise hide every inrush.
/// </summary>
public static class MedianReplay
{
    public const int MatchSeconds = 2;

    /// <summary>The rows of a CSV of <c>Ts,PowerW,ApparentVa,VoltageV,CurrentA,Pf</c> (unix seconds; a header line is skipped).</summary>
    public static List<MeterRow> ReadCsv(string path)
    {
        var rows = new List<MeterRow>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(',');
            if (p.Length < 2 || !long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)) continue;
            rows.Add(new MeterRow(ts, Number(p[1]) ?? 0, p.Length > 2 ? Number(p[2]) : null, p.Length > 5 ? Number(p[5]) : null));
        }
        return rows.OrderBy(r => r.Ts).ToList();
    }

    private static double? Number(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>The series on a 1-second grid: a missing second holds the previous row up to the detector's limit, as the detector would.</summary>
    public static List<MeterRow> Grid(IReadOnlyList<MeterRow> rows)
    {
        var grid = new List<MeterRow>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0 && rows[i].Ts <= rows[i - 1].Ts) continue;
            grid.Add(rows[i]);
            if (i + 1 >= rows.Count) break;
            var gap = rows[i + 1].Ts - rows[i].Ts;
            if (gap > MeterEventDetector.MaxFillSeconds) continue;
            for (var k = 1; k < gap; k++) grid.Add(rows[i] with { Ts = rows[i].Ts + k });
        }
        return grid;
    }

    /// <summary>The 3-second centred median of a grid; the seconds next to a hole keep their own value.</summary>
    public static List<MeterRow> Median3(IReadOnlyList<MeterRow> grid)
    {
        var result = new List<MeterRow>(grid.Count);
        for (var i = 0; i < grid.Count; i++)
        {
            if (i == 0 || i == grid.Count - 1 || grid[i].Ts - grid[i - 1].Ts != 1 || grid[i + 1].Ts - grid[i].Ts != 1)
            {
                result.Add(grid[i]);
                continue;
            }
            var mid = new[] { grid[i - 1], grid[i], grid[i + 1] }.OrderBy(x => x.PowerW).ElementAt(1);
            result.Add(mid with { Ts = grid[i].Ts });
        }
        return result;
    }

    public static List<DetectedEvent> Detect(IEnumerable<MeterRow> series, double minEventDeltaW = MeterEventDetector.DefaultMinEventDeltaW)
    {
        var detector = new MeterEventDetector(minEventDeltaW);
        var events = new List<DetectedEvent>();
        foreach (var r in series)
            events.AddRange(detector.Add(new LiveSample(Guid.Empty, DateTimeOffset.FromUnixTimeSeconds(r.Ts), r.PowerW, r.ApparentVa, null, null, r.Pf)));
        return events;
    }

    /// <summary>Both modes over the rows, and their comparison.</summary>
    public static ReplayComparison Run(IReadOnlyList<MeterRow> rows, double minEventDeltaW = MeterEventDetector.DefaultMinEventDeltaW)
    {
        var grid = Grid(rows);
        var raw = new Dictionary<long, double>(grid.Count);
        foreach (var r in grid) raw[r.Ts] = r.PowerW;
        var actual = Detect(grid, minEventDeltaW);
        var median = Detect(Median3(grid), minEventDeltaW).Select(e => WithRawTransient(e, raw)).ToList();
        return Compare(actual, median);
    }

    /// <summary>The detector's ratio, computed on the raw seconds: the peak of the first 2 s over the step.</summary>
    public static DetectedEvent WithRawTransient(DetectedEvent e, IReadOnlyDictionary<long, double> raw)
    {
        var sign = Math.Sign(e.DeltaP);
        var t0 = e.TsUtc.ToUnixTimeSeconds();
        var peak = double.MinValue;
        for (var t = t0; t < t0 + MeterEventDetector.TransientSeconds; t++)
            if (raw.TryGetValue(t, out var w)) peak = Math.Max(peak, (w - e.PowerBeforeW) * sign);
        var ratio = peak == double.MinValue ? 1 : Math.Max(0, peak / Math.Abs(e.DeltaP));
        return e with { TransientRatio = Math.Round(ratio, 3) };
    }

    /// <summary>Pairs the events of the two modes, nearest in time first: same direction, within ±<see cref="MatchSeconds"/> s.</summary>
    public static ReplayComparison Compare(IReadOnlyList<DetectedEvent> actual, IReadOnlyList<DetectedEvent> median)
    {
        var candidates = new List<(int A, int M, double Apart)>();
        var from = 0;
        for (var a = 0; a < actual.Count; a++)
        {
            while (from < median.Count && (actual[a].TsUtc - median[from].TsUtc).TotalSeconds > MatchSeconds) from++;
            for (var m = from; m < median.Count && (median[m].TsUtc - actual[a].TsUtc).TotalSeconds <= MatchSeconds; m++)
                if (Math.Sign(actual[a].DeltaP) == Math.Sign(median[m].DeltaP))
                    candidates.Add((a, m, Math.Abs((median[m].TsUtc - actual[a].TsUtc).TotalSeconds)));
        }
        var usedA = new bool[actual.Count];
        var usedM = new bool[median.Count];
        var matches = new List<EventMatch>();
        foreach (var c in candidates.OrderBy(c => c.Apart).ThenBy(c => Math.Abs(actual[c.A].DeltaP - median[c.M].DeltaP)))
        {
            if (usedA[c.A] || usedM[c.M]) continue;
            usedA[c.A] = usedM[c.M] = true;
            matches.Add(new EventMatch(actual[c.A], median[c.M]));
        }
        return new ReplayComparison(actual, median, matches.OrderBy(m => m.Actual.TsUtc).ToList(),
            actual.Where((_, i) => !usedA[i]).ToList(), median.Where((_, i) => !usedM[i]).ToList());
    }

    /// <summary>
    /// Pairs of a step and its opposite of about the same size within <paramref name="seconds"/>: a flicker of the level,
    /// the likely false events (a real load switched on and off that fast is rare, not impossible: the condensate pump runs 3–6 s).
    /// </summary>
    public static int Flickers(IReadOnlyList<DetectedEvent> events, int seconds)
    {
        var used = new HashSet<int>();
        var pairs = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (used.Contains(i)) continue;
            for (var j = i + 1; j < events.Count && (events[j].TsUtc - events[i].TsUtc).TotalSeconds <= seconds; j++)
                if (!used.Contains(j) && Math.Sign(events[i].DeltaP) != Math.Sign(events[j].DeltaP)
                    && Math.Abs(Math.Abs(events[i].DeltaP) - Math.Abs(events[j].DeltaP)) <= SignatureClusterer.Eps(events[i].DeltaP, events[j].DeltaP))
                {
                    used.Add(i);
                    used.Add(j);
                    pairs++;
                    break;
                }
        }
        return pairs;
    }

    /// <summary>
    /// A step the raw seconds confirm whatever the mode: the mean of the 5 s before and of the 5 s after it, leaving out the
    /// 3 s from its start, differ by at least half of |ΔP| in its direction. An event outside this is a step of no lasting level.
    /// </summary>
    public static bool ConfirmedByRaw(DetectedEvent e, IReadOnlyDictionary<long, double> raw)
    {
        var t0 = e.TsUtc.ToUnixTimeSeconds();
        var before = Enumerable.Range(1, 5).Select(k => t0 - k).Where(raw.ContainsKey).Select(t => raw[t]).ToList();
        var after = Enumerable.Range(3, 5).Select(k => t0 + k).Where(raw.ContainsKey).Select(t => raw[t]).ToList();
        if (before.Count == 0 || after.Count == 0) return false;
        return (after.Average() - before.Average()) * Math.Sign(e.DeltaP) >= 0.5 * Math.Abs(e.DeltaP);
    }
}
