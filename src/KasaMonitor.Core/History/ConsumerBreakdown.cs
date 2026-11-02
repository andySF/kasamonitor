using KasaMonitor.Core.Home;

namespace KasaMonitor.Core.History;

/// <summary>What a row of the consumers' breakdown is.</summary>
public enum ConsumerKind
{
    /// <summary>A detected appliance named by the user (NILM signature, label „utilizator”).</summary>
    IdentifiedDetected,
    /// <summary>
    /// A plug whose appliance is known: marked „un singur aparat pe priză” (0.9.1, named by its appliance) or confirmed by a
    /// NILM signature (named by the signature); the plug's energy.
    /// </summary>
    IdentifiedPlug,
    /// <summary>The other plugs: measured, but which appliance is on them is not known (several, or no confirmed signature).</summary>
    OtherPlugs,
    /// <summary>Every anonymous detected signature („Necunoscut #n”), grouped in one row.</summary>
    Unidentified,
    /// <summary>The rest of the house nobody explains („Nedetectat”).</summary>
    Undetected,
    /// <summary>
    /// Σ plugs above the meter on a bar (the clamp's tolerance, or partial meter data): shown apart, never part of the total
    /// nor of the identified share.
    /// </summary>
    AboveMeter,
}

/// <summary>One member of a grouped row (an anonymous signature, a plug of „Alte prize”) and its energy over the period.</summary>
public sealed record ConsumerMember(long SignatureId, string Name, double Kwh);

/// <summary>One row of the breakdown: total over the period and per bucket (kWh; null = the meter has no data in that bucket).</summary>
public sealed record ConsumerRow(string Key, string Name, ConsumerKind Kind, double Kwh, IReadOnlyList<double?> Buckets,
    IReadOnlyList<ConsumerMember>? Members = null);

/// <summary>
/// Where the main meter's energy went, by how well each part is known (/history „Consumatori”, /consumatori): every
/// identified consumer as its own row (a detected signature named by the user, a single-appliance plug, a plug confirmed by a
/// signature), the measured plugs whose appliance is not known, all the anonymous detected signatures grouped as
/// „Neidentificați (n semnături)”, and „Nedetectat”. <see cref="TotalKwh"/> = the meter (Σ of its bars); the rows other than
/// <see cref="ConsumerKind.AboveMeter"/> add up to it, bar by bar. <see cref="IdentifiedPct"/> = identified / meter: the
/// progress of the learning.
/// </summary>
public sealed record ConsumerBreakdown(IReadOnlyList<ConsumerRow> Rows, double TotalKwh, IReadOnlyList<double?> TotalBuckets)
{
    public double IdentifiedKwh => Rows.Where(r => r.Kind is ConsumerKind.IdentifiedDetected or ConsumerKind.IdentifiedPlug).Sum(r => r.Kwh);
    public double? IdentifiedPct => TotalKwh > 1e-9 ? IdentifiedKwh / TotalKwh * 100 : null;
    public double KwhOf(ConsumerKind kind) => Rows.Where(r => r.Kind == kind).Sum(r => r.Kwh);
    public ConsumerRow? Unidentified => Rows.FirstOrDefault(r => r.Kind == ConsumerKind.Unidentified);
    public double AboveMeterKwh => KwhOf(ConsumerKind.AboveMeter);

    public static readonly ConsumerBreakdown Empty = new(Array.Empty<ConsumerRow>(), 0, Array.Empty<double?>());
}

/// <summary>A signature as the breakdown needs it: detected (no plug) or confirmed by a plug, named by the user or not.</summary>
public sealed record ConsumerSignature(long Id, string Name, bool NamedByUser, Guid? PlugId);

/// <summary>
/// A plug's energy per bucket (kWh, null = no data; only on the buckets the meter has). <see cref="Appliance"/>: the
/// appliance of a single-appliance plug (<c>Plug.Appliance</c>), which makes it identified under that name.
/// </summary>
public sealed record ConsumerPlug(Guid PlugId, string Alias, IReadOnlyList<double?> Buckets, string? Appliance = null);

public static class ConsumerBreakdownRule
{
    public const string UnidentifiedKey = "unidentified";
    public const string UndetectedKey = "undetected";
    public const string OtherPlugsKey = "plugs-other";
    public const string AboveMeterKey = "above-meter";
    public const string AboveMeterName = "Peste contor (toleranța clemei / date parțiale)";

    /// <summary>
    /// Per bucket with meter data: the plugs as measured, scaled down to the meter when their Σ exceeds it (the excess goes to
    /// <see cref="ConsumerKind.AboveMeter"/>); the detected appliances by <see cref="EnergyFlowRule.ScalePerBucket"/> (the home
    /// page's rule: scaled down together to the meter minus the plugs, noise under 0.01 kWh left in „Nedetectat”). A plug is
    /// identified when it is a single-appliance plug or a signature confirmed by it exists; a detected signature when the user
    /// named it; the others are the „Neidentificați”.
    /// </summary>
    /// <param name="meter">The main meter per bucket (kWh; null = no data).</param>
    /// <param name="detectedKwh">The energy of each detected (not plug-confirmed, not hidden) signature per bucket (kWh).</param>
    public static ConsumerBreakdown Build(IReadOnlyList<double?> meter, IReadOnlyList<ConsumerPlug> plugs,
        IReadOnlyDictionary<long, IReadOnlyList<double>> detectedKwh, IReadOnlyList<ConsumerSignature> signatures)
    {
        var n = meter.Count;
        var byId = signatures.ToDictionary(s => s.Id);
        var plugValues = plugs.ToDictionary(p => p.PlugId, _ => new double?[n]);
        var plugsSum = new double?[n];
        var above = new double?[n];
        for (var i = 0; i < n; i++)
        {
            if (meter[i] is not { } m) continue;
            var p = plugs.Sum(x => i < x.Buckets.Count ? Math.Max(0, x.Buckets[i] ?? 0) : 0);
            plugsSum[i] = p;
            var k = p > m && p > 0 ? m / p : 1;
            foreach (var plug in plugs) plugValues[plug.PlugId][i] = (i < plug.Buckets.Count ? Math.Max(0, plug.Buckets[i] ?? 0) : 0) * k;
            above[i] = Math.Max(0, p - m);
        }
        var detected = detectedKwh.Where(kv => byId.TryGetValue(kv.Key, out var s) && s.PlugId is null).ToDictionary(kv => kv.Key, kv => kv.Value);
        var scaling = EnergyFlowRule.ScalePerBucket(meter, plugsSum, detected);

        static double Sum(IEnumerable<double?> values) => values.Sum(v => v ?? 0);
        var rows = new List<ConsumerRow>();
        var confirmed = signatures.Where(s => s.PlugId is not null).GroupBy(s => s.PlugId!.Value).ToDictionary(g => g.Key, g => g.First());
        var otherPlugs = new List<Guid>();
        foreach (var plug in plugs)
        {
            // A single-appliance plug is its appliance (its name, not the plug's alias); else a signature confirmed by the plug names it.
            var name = plug.Appliance ?? (confirmed.TryGetValue(plug.PlugId, out var sig) ? sig.Name : null);
            if (name is not null)
                rows.Add(new ConsumerRow("plug-" + plug.PlugId, name, ConsumerKind.IdentifiedPlug, Sum(plugValues[plug.PlugId]), plugValues[plug.PlugId]));
            else otherPlugs.Add(plug.PlugId);
        }
        foreach (var (id, values) in scaling.Detected.Where(kv => byId[kv.Key].NamedByUser))
            rows.Add(new ConsumerRow("sig-" + id, byId[id].Name, ConsumerKind.IdentifiedDetected, Sum(values), values));
        rows = rows.OrderByDescending(r => r.Kwh).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        if (otherPlugs.Count > 0)
        {
            var values = Enumerable.Range(0, n).Select(i => meter[i] is null ? (double?)null : otherPlugs.Sum(id => plugValues[id][i] ?? 0)).ToList();
            rows.Add(new ConsumerRow(OtherPlugsKey, $"Alte prize ({otherPlugs.Count})", ConsumerKind.OtherPlugs, Sum(values), values,
                plugs.Where(p => otherPlugs.Contains(p.PlugId)).Select(p => new ConsumerMember(0, p.Alias, Sum(plugValues[p.PlugId]))).OrderByDescending(x => x.Kwh).ToList()));
        }
        var anonymous = scaling.Detected.Where(kv => !byId[kv.Key].NamedByUser).ToList();
        if (anonymous.Count > 0)
        {
            var values = Enumerable.Range(0, n).Select(i => meter[i] is null ? (double?)null : anonymous.Sum(kv => kv.Value[i] ?? 0)).ToList();
            rows.Add(new ConsumerRow(UnidentifiedKey, $"Neidentificați ({anonymous.Count} semnături)", ConsumerKind.Unidentified, Sum(values), values,
                anonymous.Select(kv => new ConsumerMember(kv.Key, byId[kv.Key].Name, Sum(kv.Value))).OrderByDescending(x => x.Kwh).ToList()));
        }
        rows.Add(new ConsumerRow(UndetectedKey, "Nedetectat", ConsumerKind.Undetected, Sum(scaling.Undetected), scaling.Undetected));
        if (Sum(above) > 1e-9)
            rows.Add(new ConsumerRow(AboveMeterKey, AboveMeterName, ConsumerKind.AboveMeter, Sum(above), above));
        var total = Enumerable.Range(0, n).Select(i => meter[i] is { } m ? (double?)Math.Max(0, m) : null).ToList();
        return new ConsumerBreakdown(rows, Sum(total), total);
    }
}
