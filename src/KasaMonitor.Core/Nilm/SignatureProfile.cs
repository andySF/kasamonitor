namespace KasaMonitor.Core.Nilm;

/// <summary>
/// The „fișa” of a signature: the data points that describe it, computed from its events (and runs) in the window.
/// The same profile is shown on /consumatori when a row is expanded and feeds the automatic merge
/// (<see cref="SignatureMerger"/>): one calculation for both.
/// </summary>
/// <param name="MeanDeltaP">Mean |ΔP| of the events (switch-ons and switch-offs).</param>
/// <param name="StdDeltaP">Standard deviation of |ΔP|.</param>
/// <param name="MeanDeltaQ">Mean ΔQ seen from the switch-on side.</param>
/// <param name="MeanTransient">Mean transient ratio of the switch-ons whose transient is known (<see cref="TransientRule"/>); null when none.</param>
/// <param name="TransientKnown">How many of the <paramref name="OnEvents"/> switch-ons have a known transient.</param>
/// <param name="Runs">Runs with a seen end (paired or house below level) used for the durations.</param>
/// <param name="Hours">Events per local hour of the day (24 values).</param>
/// <param name="TopPlugId">The plug that labelled most of its events (not excluded from learning), or null.</param>
/// <param name="TopPlugLabel">That plug's label on the newest of those events.</param>
/// <param name="Last7DaysWh">Energy of its runs over the last 7 days.</param>
/// <param name="EventTimes">Times of its events, oldest first (the co-occurrence of the merge rule d; not shown).</param>
/// <param name="PlugEvents">Its events labelled per plug (not excluded from learning): the plug share after a merge.</param>
public sealed record SignatureProfile(
    long SignatureId,
    int Events,
    int OnEvents,
    int OffEvents,
    double MeanDeltaP,
    double StdDeltaP,
    double MeanDeltaQ,
    double? MeanTransient,
    int TransientKnown,
    int Runs,
    double? MedianRunSeconds,
    double? P10RunSeconds,
    double? P90RunSeconds,
    IReadOnlyList<int> Hours,
    Guid? TopPlugId,
    string? TopPlugLabel,
    int TopPlugEvents,
    DateTimeOffset? FirstSeenUtc,
    DateTimeOffset? LastSeenUtc,
    double Last7DaysWh,
    IReadOnlyList<DateTimeOffset>? EventTimes = null,
    IReadOnlyDictionary<Guid, int>? PlugEvents = null)
{
    /// <summary>Power factor of the step: |ΔP| / √(ΔP² + ΔQ²) (1 = purely resistive).</summary>
    public double StepPowerFactor => MeanDeltaP <= 0 ? 1 : MeanDeltaP / Math.Sqrt(MeanDeltaP * MeanDeltaP + MeanDeltaQ * MeanDeltaQ);

    /// <summary>Share of its events labelled by <see cref="TopPlugId"/>.</summary>
    public double TopPlugShare => Events > 0 ? (double)TopPlugEvents / Events : 0;

    /// <summary>The matching centre (an unknown transient is not compared).</summary>
    public SignatureCentre Centre => new(SignatureId, MeanDeltaP, MeanDeltaQ, MeanTransient ?? 0, MeanTransient is not null);
}

public static class SignatureProfiles
{
    /// <summary>The energy column of the profile covers this many days.</summary>
    public static readonly TimeSpan EnergyPeriod = TimeSpan.FromDays(7);

    /// <summary>Profiles of every signature that has events among <paramref name="events"/> (runs matched by signature).</summary>
    public static Dictionary<long, SignatureProfile> Build(IEnumerable<MeterEvent> events, IEnumerable<ApplianceRun> runs,
        IReadOnlyCollection<Guid> excludedPlugs, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var runsBySignature = runs.ToLookup(r => r.SignatureId);
        return events.Where(e => e.SignatureId is not null).GroupBy(e => e.SignatureId!.Value)
            .ToDictionary(g => g.Key, g => Of(g.Key, g.ToList(), runsBySignature[g.Key].ToList(), excludedPlugs, nowUtc, tz));
    }

    /// <summary>The profile of one signature from its events and runs.</summary>
    public static SignatureProfile Of(long signatureId, IReadOnlyCollection<MeterEvent> events, IReadOnlyCollection<ApplianceRun> runs,
        IReadOnlyCollection<Guid> excludedPlugs, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        var features = events.Select(EventFeatures.Of).ToList();
        var (p, q, t) = SignatureClusterer.Means(features);
        var known = features.Count(f => f.HasTransient);
        var std = features.Count > 0 ? Math.Sqrt(features.Average(f => (f.Magnitude - p) * (f.Magnitude - p))) : 0;

        var durations = runs.Where(r => r.EndUtc is not null && r.EndReason != RunEndReason.Timeout)
            .Select(r => (r.EndUtc!.Value - r.StartUtc).TotalSeconds).OrderBy(x => x).ToList();

        var hours = new int[24];
        foreach (var e in events) hours[TimeZoneInfo.ConvertTime(e.TsUtc, tz).Hour]++;

        var byPlug = events.Where(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId is { } id && !excludedPlugs.Contains(id))
            .GroupBy(e => e.LabelPlugId!.Value).ToList();
        var top = byPlug.OrderByDescending(g => g.Count()).ThenByDescending(g => g.Max(e => e.TsUtc)).FirstOrDefault();

        var from7 = nowUtc - EnergyPeriod;
        return new SignatureProfile(signatureId, events.Count, events.Count(e => e.IsOn), events.Count(e => !e.IsOn),
            p, std, q, known > 0 ? t : null, known, durations.Count,
            Percentile(durations, 0.5), Percentile(durations, 0.1), Percentile(durations, 0.9), hours,
            top?.Key, top?.OrderByDescending(e => e.TsUtc).First().Label, top?.Count() ?? 0,
            events.Count > 0 ? events.Min(e => e.TsUtc) : null, events.Count > 0 ? events.Max(e => e.TsUtc) : null,
            runs.Sum(r => ApplianceEnergy.InRange(r, from7, nowUtc, nowUtc)),
            events.Select(e => e.TsUtc).OrderBy(t => t).ToList(),
            byPlug.ToDictionary(g => g.Key, g => g.Count()));
    }

    /// <summary>Percentile of sorted values, linear between the closest ranks; null when empty.</summary>
    public static double? Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 0) return null;
        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }
}
