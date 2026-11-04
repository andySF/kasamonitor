using System.Globalization;
using KasaMonitor.Core.Nilm;
using KasaMonitor.NilmReplay;

// kasa-nilm-replay <meter.csv> [--min-delta W] [--window <from ISO> <to ISO>]
if (args.Length == 0 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("Usage: kasa-nilm-replay <meter.csv> [--min-delta W] [--window <from ISO 8601> <to ISO 8601>]");
    Console.Error.WriteLine("The CSV: Ts,PowerW,ApparentVa,VoltageV,CurrentA,Pf of the main meter (unix seconds). It stays outside the repository.");
    return 2;
}

var minDelta = MeterEventDetector.DefaultMinEventDeltaW;
DateTimeOffset? from = null, to = null;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--min-delta") minDelta = double.Parse(args[++i], CultureInfo.InvariantCulture);
    else if (args[i] == "--window")
    {
        from = DateTimeOffset.Parse(args[++i], CultureInfo.InvariantCulture);
        to = DateTimeOffset.Parse(args[++i], CultureInfo.InvariantCulture);
    }
}

var rows = MedianReplay.ReadCsv(args[0]);
var grid = MedianReplay.Grid(rows);
var raw = new Dictionary<long, double>(grid.Count);
foreach (var r in grid) raw[r.Ts] = r.PowerW;
var c = MedianReplay.Run(rows, minDelta);

string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
static double Pct(IReadOnlyList<double> sorted, double p) => SignatureProfiles.Percentile(sorted, p) ?? double.NaN;
void Table(string title, Func<DetectedEvent, bool> filter)
{
    var a = c.Actual.Where(filter).ToList();
    var m = c.Median.Where(filter).ToList();
    var matches = c.Matches.Where(x => filter(x.Actual) || filter(x.Median)).ToList();
    var diffs = matches.Select(x => Math.Abs(x.DeltaPDifference)).OrderBy(x => x).ToList();
    Console.WriteLine($"## {title}");
    Console.WriteLine();
    Console.WriteLine("| | actual | median |");
    Console.WriteLine("|---|---:|---:|");
    Console.WriteLine($"| events | {a.Count} | {m.Count} |");
    Console.WriteLine($"| of which abs(dP) >= 100 W | {a.Count(e => Math.Abs(e.DeltaP) >= 100)} | {m.Count(e => Math.Abs(e.DeltaP) >= 100)} |");
    Console.WriteLine($"| of which abs(dP) >= 500 W | {a.Count(e => Math.Abs(e.DeltaP) >= 500)} | {m.Count(e => Math.Abs(e.DeltaP) >= 500)} |");
    Console.WriteLine($"| on / off | {a.Count(e => e.DeltaP > 0)} / {a.Count(e => e.DeltaP < 0)} | {m.Count(e => e.DeltaP > 0)} / {m.Count(e => e.DeltaP < 0)} |");
    Console.WriteLine($"| flicker pairs within 5 s | {MedianReplay.Flickers(a, 5)} | {MedianReplay.Flickers(m, 5)} |");
    Console.WriteLine($"| abs(dP) >= 100 W, not confirmed by the raw levels | {a.Count(e => Math.Abs(e.DeltaP) >= 100 && !MedianReplay.ConfirmedByRaw(e, raw))} | {m.Count(e => Math.Abs(e.DeltaP) >= 100 && !MedianReplay.ConfirmedByRaw(e, raw))} |");
    Console.WriteLine($"| not matched in the other mode | {c.OnlyActual.Count(filter)} | {c.OnlyMedian.Count(filter)} |");
    Console.WriteLine($"| not matched, abs(dP) >= 100 W | {c.OnlyActual.Where(filter).Count(e => Math.Abs(e.DeltaP) >= 100)} | {c.OnlyMedian.Where(filter).Count(e => Math.Abs(e.DeltaP) >= 100)} |");
    Console.WriteLine($"| not matched, abs(dP) >= 100 W, confirmed by the raw levels | {c.OnlyActual.Where(filter).Count(e => Math.Abs(e.DeltaP) >= 100 && MedianReplay.ConfirmedByRaw(e, raw))} | {c.OnlyMedian.Where(filter).Count(e => Math.Abs(e.DeltaP) >= 100 && MedianReplay.ConfirmedByRaw(e, raw))} |");
    Console.WriteLine();
    Console.WriteLine($"Matches within +-{MedianReplay.MatchSeconds} s: {matches.Count} (same second {matches.Count(x => x.SecondsApart == 0)}, 1 s apart {matches.Count(x => Math.Abs(x.SecondsApart) == 1)}, 2 s apart {matches.Count(x => Math.Abs(x.SecondsApart) == 2)}).");
    if (diffs.Count > 0)
    {
        Console.WriteLine($"abs(dP) difference, W: p50 {F(Pct(diffs, 0.5))}, p90 {F(Pct(diffs, 0.9))}, p99 {F(Pct(diffs, 0.99))}, max {F(diffs[^1])}.");
        var large = matches.Where(x => Math.Abs(x.Actual.DeltaP) >= 100).Select(x => Math.Abs(x.DeltaPDifference) / Math.Abs(x.Actual.DeltaP)).OrderBy(x => x).ToList();
        if (large.Count > 0)
            Console.WriteLine($"The same as a share of abs(dP), steps >= 100 W: p50 {Pct(large, 0.5) * 100:0.0} %, p90 {Pct(large, 0.9) * 100:0.0} %, max {large[^1] * 100:0.0} %; outside eps (8 % + 3 W): {matches.Count(x => Math.Abs(x.Actual.DeltaP) >= 100 && Math.Abs(x.DeltaPDifference) > SignatureClusterer.Eps(x.Actual.DeltaP, x.Median.DeltaP))}.");
    }
    Console.WriteLine($"Rise changed by >= 1 s: {matches.Count(x => x.RiseChanged)}; transient ratio changed by > 0.1: {matches.Count(x => x.TransientChanged)}; transient known in one mode only: {matches.Count(x => x.TransientKnownChanged)}.");
    Console.WriteLine();
}

Console.WriteLine($"# Replay of {rows.Count} stored seconds, {grid.Count} on the grid, minimum step {minDelta:0} W");
Console.WriteLine();
Table("Whole series", _ => true);
if (from is { } f && to is { } t)
{
    Table($"Window {f:o} to {t:o}", e => e.TsUtc >= f && e.TsUtc <= t);
    Console.WriteLine("| mode | second in the window | dP | dQ | transient | rise |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|");
    foreach (var (mode, list) in new[] { ("actual", c.Actual), ("median", c.Median) })
        foreach (var e in list.Where(e => e.TsUtc >= f && e.TsUtc <= t && Math.Abs(e.DeltaP) >= 100))
            Console.WriteLine($"| {mode} | {(e.TsUtc - f).TotalSeconds:0} | {F(e.DeltaP)} | {F(e.DeltaQ)} | {e.TransientRatio.ToString("0.00", CultureInfo.InvariantCulture)} | {e.RiseSeconds:0} |");
}
return 0;
