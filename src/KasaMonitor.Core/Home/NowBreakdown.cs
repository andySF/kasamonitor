namespace KasaMonitor.Core.Home;

/// <summary>Category of a part of "Acum în casă" (colour and legend of the bubbles).</summary>
public enum NowKind
{
    /// <summary>A measured plug drawing power.</summary>
    Plug,
    /// <summary>A measured plug that is off (relay off or 0 W).</summary>
    Off,
    /// <summary>An appliance detected from the main meter (NILM) that has no plug.</summary>
    Detected,
    /// <summary>The night floor of the rest of the house ("Standby / mereu pornit").</summary>
    Standby,
    /// <summary>What is left of the meter after plugs, standby and the detected appliances ("Rest nedetectat").</summary>
    Rest,
}

/// <summary>One part of the house now. <see cref="Key"/> is stable (plug id, signature id) so the bubble keeps its place.</summary>
public sealed record NowPart(string Key, string Name, NowKind Kind, double Watts, string? Note = null, bool Active = false, string? Href = null);

/// <summary>
/// "Acum în casă": the main meter split into parts. The parts add up to max(meter, Σ plugs): plugs with all their power,
/// then the standby floor (at most the rest), then the detected appliances (scaled down together when they would exceed what
/// is left: <see cref="DetectedScaled"/>), then the undetected rest (≥ 0).
/// </summary>
public sealed record NowBreakdown(double? MeterW, IReadOnlyList<NowPart> Parts, double PlugsW, double RestW, bool PlugsAboveMeter, bool DetectedScaled)
{
    public double PartsW => Parts.Sum(p => p.Watts);
}

public static class NowBreakdownRule
{
    public const string StandbyKey = "standby";
    public const string RestKey = "rest";
    /// <summary>
    /// A standby at or below this is no part (no bubble, no row) and is not taken from the rest: a floor this small is not
    /// measurable (at night the plugs cover the meter), and a standby cut to this by a small rest now would be an empty bubble.
    /// </summary>
    public const double MinStandbyW = 5;

    /// <param name="meterW">The main meter now (null without a reading: only the plugs are shown).</param>
    /// <param name="plugs">Plugs (role Plug) with their power; negative values count as 0.</param>
    /// <param name="detected">Detected appliances without a plug that run now (NILM; plug-confirmed signatures excluded by the caller).</param>
    /// <param name="standbyFloorW">The night floor of the rest (<see cref="StandbyRule"/>); null when not known yet; 0 = no standby part.</param>
    public static NowBreakdown Build(double? meterW, IEnumerable<NowPart> plugs, IEnumerable<NowPart> detected, double? standbyFloorW)
    {
        var plugList = plugs.Select(p => p with { Watts = Math.Max(0, p.Watts) }).ToList();
        var plugsW = plugList.Sum(p => p.Watts);
        var parts = new List<NowPart>(plugList);
        if (meterW is not { } meter) return new NowBreakdown(null, parts, plugsW, 0, false, false);

        var rest = Math.Max(0, meter - plugsW);
        var standby = standbyFloorW is > MinStandbyW ? Math.Min(standbyFloorW.Value, rest) : 0;
        if (standby <= MinStandbyW) standby = 0;
        // A floor of 0 (at night the plugs cover the whole meter) is no part: no empty bubble.
        if (standby > 0)
            parts.Add(new NowPart(StandbyKey, "Standby / mereu pornit", NowKind.Standby, standby, "planșeul nocturn al restului casei"));

        var available = rest - standby;
        var det = detected.Select(d => d with { Watts = Math.Max(0, d.Watts), Kind = NowKind.Detected }).ToList();
        var detW = det.Sum(d => d.Watts);
        var scaled = detW > available + 1e-9;
        if (scaled)
        {
            var f = detW > 0 ? available / detW : 0;
            det = det.Select(d => d with { Watts = d.Watts * f }).ToList();
            detW = available;
        }
        parts.AddRange(det);
        parts.Add(new NowPart(RestKey, "Nedetectat", NowKind.Rest, Math.Max(0, available - detW), "contor − prize − standby − detectați"));
        return new NowBreakdown(meter, parts, plugsW, rest, plugsW > meter, scaled);
    }
}
