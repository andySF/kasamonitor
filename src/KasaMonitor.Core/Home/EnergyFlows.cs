namespace KasaMonitor.Core.Home;

/// <summary>Category of an energy flow of the period (bars "Aparate" and the Sankey "Unde s-a dus energia").</summary>
public enum FlowKind
{
    Plug,
    Detected,
    Undetected,
}

/// <summary>
/// Energy of one consumer (or group) over the period. <see cref="Href"/> = the page that explains it; <see cref="Children"/>
/// = the members of a group („Alte N prize”), formatted by the UI.
/// </summary>
public sealed record FlowItem(string Key, string Name, FlowKind Kind, double Kwh, string? Detail = null, string? Href = null,
    IReadOnlyList<FlowItem>? Children = null);

/// <summary>
/// Where the meter's energy of a period went: every plug, every detected appliance without a plug, and the undetected rest.
/// <see cref="Rows"/> lists them all (the "Aparate" bars), <see cref="Flows"/> groups them for the Sankey (top plugs + „Alte
/// prize”, top detected + „Alți detectați”, „Nedetectat”). Both add up to max(meter, Σ plugs): the detected appliances
/// (estimates) are scaled down together when they would exceed the meter minus the plugs (<see cref="DetectedScaled"/>);
/// a detected one under <see cref="EnergyFlowRule.MinDetectedKwh"/> is counted in „Nedetectat”.
/// </summary>
/// <param name="DetectedEstimatedKwh">The detected appliances' estimated energy before scaling (for the note when none fits).</param>
public sealed record EnergyFlows(double MeterKwh, IReadOnlyList<FlowItem> Rows, IReadOnlyList<FlowItem> Flows,
    double PlugsKwh, double DetectedKwh, double UndetectedKwh, bool PlugsAboveMeter, bool DetectedScaled, double DetectedEstimatedKwh = 0)
{
    /// <summary>Some detected appliance remains as a row (a „reduced proportionally” note makes sense only then).</summary>
    public bool HasDetected => Rows.Any(r => r.Kind == FlowKind.Detected);

    /// <summary>The left node of the Sankey: the meter, or Σ plugs when the plugs exceed it.</summary>
    public double SourceKwh => Math.Max(MeterKwh, PlugsKwh);

    public double FlowsKwh => Flows.Sum(f => f.Kwh);
}

public static class EnergyFlowRule
{
    public const int TopPlugs = 5;
    public const int TopDetected = 3;
    public const string OtherPlugsKey = "plugs-other";
    public const string OtherDetectedKey = "detected-other";
    public const string UndetectedKey = "undetected";
    /// <summary>A detected appliance below this (after scaling) is noise: its energy goes to „Nedetectat” instead of a row.</summary>
    public const double MinDetectedKwh = 0.01;

    public static EnergyFlows Build(double meterKwh, IEnumerable<FlowItem> plugs, IEnumerable<FlowItem> detected,
        int topPlugs = TopPlugs, int topDetected = TopDetected)
    {
        var meter = Math.Max(0, meterKwh);
        var plugList = plugs.Select(p => p with { Kwh = Math.Max(0, p.Kwh), Kind = FlowKind.Plug })
            .OrderByDescending(p => p.Kwh).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var plugsKwh = plugList.Sum(p => p.Kwh);
        var available = Math.Max(0, meter - plugsKwh);

        var det = detected.Select(d => d with { Kwh = Math.Max(0, d.Kwh), Kind = FlowKind.Detected }).Where(d => d.Kwh > 0)
            .OrderByDescending(d => d.Kwh).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var detKwh = det.Sum(d => d.Kwh);
        var estimated = detKwh;
        var scaled = detKwh > available + 1e-9;
        if (scaled)
        {
            var f = detKwh > 0 ? available / detKwh : 0;
            det = det.Select(d => d with { Kwh = d.Kwh * f }).ToList();
            detKwh = available;
        }
        // Rows of a few Wh (dozens of rare signatures, or everything scaled down to nothing) say nothing: their energy
        // stays in the total as part of „Nedetectat”.
        det = det.Where(d => d.Kwh >= MinDetectedKwh).ToList();
        detKwh = det.Sum(d => d.Kwh);
        var undetected = Math.Max(0, available - detKwh);
        var undetectedItem = new FlowItem(UndetectedKey, "Nedetectat", FlowKind.Undetected, undetected, "contor − prize − detectați", "/consumatori");

        var rows = plugList.Concat(det).OrderByDescending(r => r.Kwh).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Append(undetectedItem).ToList();

        var flows = new List<FlowItem>();
        flows.AddRange(Grouped(plugList, topPlugs, OtherPlugsKey, n => $"Alte {n} prize", FlowKind.Plug, "/prize"));
        flows.AddRange(Grouped(det, topDetected, OtherDetectedKey, n => $"Alți {n} detectați", FlowKind.Detected, "/consumatori"));
        flows.Add(undetectedItem);
        return new EnergyFlows(meter, rows, flows, plugsKwh, detKwh, undetected, plugsKwh > meter, scaled, estimated);
    }

    private static IEnumerable<FlowItem> Grouped(IReadOnlyList<FlowItem> items, int top, string key, Func<int, string> name, FlowKind kind, string href)
    {
        // A group of one is shown by its own name.
        if (items.Count <= top + 1) return items;
        var rest = items.Skip(top).ToList();
        return items.Take(top).Append(new FlowItem(key, name(rest.Count), kind, rest.Sum(r => r.Kwh), null, href, rest));
    }
}
