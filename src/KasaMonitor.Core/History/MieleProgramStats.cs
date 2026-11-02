using KasaMonitor.Core.Miele;

namespace KasaMonitor.Core.History;

/// <summary>
/// One Miele cycle of a period, as the history lists it: the energy measured by the appliance's plug and the one reported
/// by Miele kept apart (<see cref="BestKwh"/>: the plug's when it measured, else Miele's), water from Miele.
/// </summary>
public sealed record MieleCycleRow(MieleCycle Cycle, string Appliance, string Program, double? DurationMinutes, double? MieleKwh,
    double? PlugKwh, double? WaterL)
{
    /// <summary>The energy the cost uses: the plug's measurement, else Miele's estimate.</summary>
    public double? BestKwh => PlugKwh ?? MieleKwh;

    public static MieleCycleRow From(MieleCycle c, string appliance) => new(c, appliance, MieleProgramStats.ProgramName(c),
        c.Duration?.TotalMinutes, c.MieleEnergyKwh, c.PlugEnergyWh / 1000.0, c.MieleWaterL);
}

/// <summary>The cycles of one program in a period: how many, mean duration, energy and water, totals.</summary>
public sealed record MieleProgramRow(string Appliance, string Program, int Cycles, double? MeanMinutes, double? MeanKwh, double? MeanWaterL,
    double? TotalKwh, double? TotalWaterL, IReadOnlyList<double> Minutes, IReadOnlyList<double> Kwh);

/// <summary>Per-program statistics of Miele cycles (computed on the fly: a few dozen cycles a month, no daily table needed).</summary>
public static class MieleProgramStats
{
    public const string UnknownProgram = "program necunoscut";

    public static string ProgramName(MieleCycle c) => string.IsNullOrWhiteSpace(c.Program) ? UnknownProgram : c.Program.Trim();

    /// <summary>
    /// Groups the closed cycles by appliance and program, most cycles first. Means are over the cycles that have the value
    /// (an API-only cycle has no plug energy); the energy is <see cref="MieleCycleRow.BestKwh"/>.
    /// </summary>
    public static IReadOnlyList<MieleProgramRow> ByProgram(IEnumerable<MieleCycleRow> cycles) =>
        cycles.Where(c => !c.Cycle.IsOpen)
            .GroupBy(c => (c.Appliance, c.Program))
            .Select(g =>
            {
                var minutes = g.Where(c => c.DurationMinutes is not null).Select(c => c.DurationMinutes!.Value).ToList();
                var kwh = g.Where(c => c.BestKwh is not null).Select(c => c.BestKwh!.Value).ToList();
                var water = g.Where(c => c.WaterL is not null).Select(c => c.WaterL!.Value).ToList();
                return new MieleProgramRow(g.Key.Appliance, g.Key.Program, g.Count(),
                    minutes.Count == 0 ? null : minutes.Average(), kwh.Count == 0 ? null : kwh.Average(), water.Count == 0 ? null : water.Average(),
                    kwh.Count == 0 ? null : kwh.Sum(), water.Count == 0 ? null : water.Sum(), minutes, kwh);
            })
            .OrderByDescending(r => r.Cycles).ThenBy(r => r.Appliance, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Program, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
