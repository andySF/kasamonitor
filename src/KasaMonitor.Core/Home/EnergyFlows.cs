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

/// <summary>
/// The detected appliances scaled bar by bar (<see cref="EnergyFlowRule.ScalePerBucket"/>): per signature its energy per bar
/// (kWh; null where the meter has no data), „Nedetectat” per bar, and whether anything had to be scaled down.
/// <see cref="EstimatedKwh"/>: the detected appliances' energy before scaling.
/// </summary>
public sealed record BucketScaling(IReadOnlyDictionary<long, IReadOnlyList<double?>> Detected, IReadOnlyList<double?> Undetected, bool Scaled,
    double EstimatedKwh)
{
    public double DetectedKwh(long id) => Detected.TryGetValue(id, out var v) ? v.Sum(x => x ?? 0) : 0;
    public double UndetectedKwh => Undetected.Sum(x => x ?? 0);
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

    /// <summary>
    /// The single rule for the detected appliances (0.9.2: the home page and /history use it alike): bar by bar, the
    /// detected appliances (estimates) are scaled down together when they exceed the meter minus the plugs of that bar;
    /// „Nedetectat” = what is left of the bar (≥ 0). A signature under <see cref="MinDetectedKwh"/> over the period (before or
    /// after scaling) is noise: its energy stays in „Nedetectat”. Per bar: plugs + detected + nedetectat = max(meter, plugs).
    /// </summary>
    /// <param name="meter">The main meter per bar (kWh; null = no data, the bar is left out).</param>
    /// <param name="plugs">Σ of the counted plugs per bar (kWh).</param>
    /// <param name="detected">Each detected signature's estimated energy per bar (kWh).</param>
    public static BucketScaling ScalePerBucket(IReadOnlyList<double?> meter, IReadOnlyList<double?> plugs, IReadOnlyDictionary<long, IReadOnlyList<double>> detected)
    {
        var n = meter.Count;
        static double At(IReadOnlyList<double> v, int i) => i < v.Count ? Math.Max(0, v[i]) : 0;
        var estimated = detected.Sum(kv => Enumerable.Range(0, n).Where(i => meter[i] is not null).Sum(i => At(kv.Value, i)));
        var kept = detected.Where(kv => Enumerable.Range(0, n).Where(i => meter[i] is not null).Sum(i => At(kv.Value, i)) >= MinDetectedKwh)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var scaled = kept.ToDictionary(kv => kv.Key, _ => new double?[n]);
        var undetected = new double?[n];
        var any = false;
        for (var i = 0; i < n; i++)
        {
            if (meter[i] is not { } m) continue;
            var available = Math.Max(0, m - (i < plugs.Count ? plugs[i] ?? 0 : 0));
            var d = kept.Sum(kv => At(kv.Value, i));
            var k = d > available + 1e-12 ? available / d : 1;
            if (k < 1) any = true;
            foreach (var kv in kept) scaled[kv.Key][i] = At(kv.Value, i) * k;
            undetected[i] = Math.Max(0, available - d * k);
        }
        // Scaled down to almost nothing: noise as well, back into „Nedetectat”.
        foreach (var id in scaled.Where(kv => kv.Value.Sum(v => v ?? 0) < MinDetectedKwh).Select(kv => kv.Key).ToList())
        {
            for (var i = 0; i < n; i++)
                if (scaled[id][i] is { } v) undetected[i] = (undetected[i] ?? 0) + v;
            scaled.Remove(id);
        }
        return new BucketScaling(scaled.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<double?>)kv.Value), undetected, any, estimated);
    }

    /// <summary>
    /// The flows of a period with the detected appliances already scaled bar by bar (<see cref="ScalePerBucket"/>):
    /// <paramref name="detected"/> holds their scaled totals, „Nedetectat” is <see cref="BucketScaling.UndetectedKwh"/>.
    /// </summary>
    public static EnergyFlows Build(double meterKwh, IEnumerable<FlowItem> plugs, IEnumerable<FlowItem> detected, BucketScaling scaling,
        int topPlugs = TopPlugs, int topDetected = TopDetected)
    {
        var meter = Math.Max(0, meterKwh);
        var plugList = plugs.Select(p => p with { Kwh = Math.Max(0, p.Kwh), Kind = FlowKind.Plug })
            .OrderByDescending(p => p.Kwh).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var det = detected.Select(d => d with { Kind = FlowKind.Detected }).Where(d => d.Kwh >= MinDetectedKwh)
            .OrderByDescending(d => d.Kwh).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return Assemble(meter, plugList, det, scaling.UndetectedKwh, scaling.Scaled, scaling.EstimatedKwh, topPlugs, topDetected);
    }

    /// <summary>
    /// The flows of a period from its totals only (no bars): the detected appliances scaled down together when they exceed
    /// the meter minus the plugs. Kept for callers without bars; the home page and /history scale bar by bar.
    /// </summary>
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
        return Assemble(meter, plugList, det, undetected, scaled, estimated, topPlugs, topDetected);
    }

    private static EnergyFlows Assemble(double meter, List<FlowItem> plugList, List<FlowItem> det, double undetected, bool scaled, double estimated,
        int topPlugs, int topDetected)
    {
        var plugsKwh = plugList.Sum(p => p.Kwh);
        var detKwh = det.Sum(d => d.Kwh);
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
