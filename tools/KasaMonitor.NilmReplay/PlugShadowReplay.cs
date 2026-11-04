using System.Globalization;
using KasaMonitor.Core.Nilm;

namespace KasaMonitor.NilmReplay;

/// <summary>
/// The rules of a plug with a single appliance (<see cref="PlugTransitions"/>, <see cref="PlugShadows"/>) over two CSV files that
/// stay outside the repository: the plug's stored stream (Ts,PowerW, unix seconds, no header) and the meter's events
/// (Id,Ts,DeltaP,SignatureId,Source,LabelPlugId, no header).
/// </summary>
public static class PlugShadowReplay
{
    public static int Run(string samplesPath, string eventsPath)
    {
        var rows = File.ReadLines(samplesPath).Where(l => l.Length > 0).Select(l => l.Split(','))
            .Select(c => new PlugPowerPoint(DateTimeOffset.FromUnixTimeSeconds((long)Number(c[0])), Number(c[1]))).OrderBy(p => p.TsUtc).ToList();
        var plug = Guid.NewGuid();
        var events = File.ReadLines(eventsPath).Where(l => l.Length > 0).Select(l => l.Split(','))
            .Select(c => new MeterEvent(long.Parse(c[0], CultureInfo.InvariantCulture), Guid.Empty, DateTimeOffset.FromUnixTimeSeconds((long)Number(c[1])), Number(c[2]), 0, 0, 1, 0, 0,
                null, null, NilmSources.EventFromInt(int.Parse(c[4], CultureInfo.InvariantCulture)))).OrderBy(e => e.TsUtc).ToList();
        if (rows.Count == 0 || events.Count == 0)
        {
            Console.Error.WriteLine("Nothing to read.");
            return 2;
        }

        // The span both files cover, as in the measurement.
        var from = rows[0].TsUtc > events[0].TsUtc ? rows[0].TsUtc : events[0].TsUtc;
        var to = rows[^1].TsUtc < events[^1].TsUtc ? rows[^1].TsUtc : events[^1].TsUtc;
        var (allTransitions, allRuns) = PlugTransitions.Replay(PlugTransitions.Grid(rows, Array.Empty<PlugPowerPoint>(), rows[0].TsUtc, rows[^1].TsUtc));
        var transitions = allTransitions.Where(t => t.TsUtc >= from && t.TsUtc < to).ToList();
        var runs = allRuns.Where(r => r.StartUtc >= from && r.StartUtc < to && r.EndUtc is not null).ToList();
        var days = (to - from).TotalDays;

        var shadows = PlugShadows.Find(transitions.Select(t => (plug, t)), events);
        var inWindow = transitions.Select(t => events.Count(e => e.TsUtc >= t.TsUtc - PlugShadows.MaxLead && e.TsUtc <= t.TsUtc
            && e.DeltaP > 0 == t.StepW > 0 && Math.Abs(e.DeltaP - t.StepW) <= PlugShadows.ToleranceW)).ToList();
        string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        Console.WriteLine($"Hours covered: {F((to - from).TotalHours)}");
        Console.WriteLine($"Transitions: {transitions.Count} ({F(transitions.Count / days)} a day), on {transitions.Count(t => t.TurnedOn)}, off {transitions.Count(t => !t.TurnedOn)}");
        Console.WriteLine($"Runs closed: {runs.Count}, paired {runs.Count(r => r.EndReason == RunEndReason.Paired)}, ended by a gap {runs.Count(r => r.EndReason == RunEndReason.Timeout)}");
        Console.WriteLine($"Energy of the runs: {F(runs.Sum(r => r.EstimatedWh ?? 0) / days)} Wh a day");
        if (runs.Count > 0) Console.WriteLine($"Median level of a run, mean: {F(runs.Average(r => r.PowerW))} W");
        Console.WriteLine($"Transitions by candidates: none {inWindow.Count(n => n == 0)}, one {inWindow.Count(n => n == 1)}, several {inWindow.Count(n => n > 1)}");
        Console.WriteLine($"Shadows: {shadows.Count} of {events.Count(e => e.TsUtc >= from && e.TsUtc < to)} events");
        return 0;
    }

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);
}
