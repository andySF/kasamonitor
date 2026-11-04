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
/// <param name="Repetition">How its runs repeat over the last 30 days (shown only; no rule reads it).</param>
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
    IReadOnlyDictionary<Guid, int>? PlugEvents = null,
    RunRepetition? Repetition = null)
{
    /// <summary>Power factor of the step: |ΔP| / √(ΔP² + ΔQ²) (1 = purely resistive).</summary>
    public double StepPowerFactor => MeanDeltaP <= 0 ? 1 : MeanDeltaP / Math.Sqrt(MeanDeltaP * MeanDeltaP + MeanDeltaQ * MeanDeltaQ);

    /// <summary>Share of its events labelled by <see cref="TopPlugId"/>.</summary>
    public double TopPlugShare => Events > 0 ? (double)TopPlugEvents / Events : 0;

    /// <summary>The matching centre (an unknown transient is not compared).</summary>
    public SignatureCentre Centre => new(SignatureId, MeanDeltaP, MeanDeltaQ, MeanTransient ?? 0, MeanTransient is not null);
}

/// <summary>
/// How the closed runs of a signature repeat (the „fișa”; information only, never part of the classification).
/// </summary>
/// <param name="Durations">Seconds of a run.</param>
/// <param name="Intervals">Seconds between consecutive switch-ons, without the pauses over <see cref="SignatureProfiles.MaxRepeatGap"/>.</param>
/// <param name="Sessions">Groups of runs at most <see cref="SignatureProfiles.SessionGap"/> apart, from an end to the next start.</param>
/// <param name="PulsesPerSessionP50">Median of the runs per session.</param>
/// <param name="EnergyPerSessionWhP50">Median of the energy per session.</param>
public sealed record RunRepetition(DurationSpread Durations, DurationSpread Intervals, int Sessions, double? PulsesPerSessionP50, double? EnergyPerSessionWhP50);

/// <summary>
/// The row of a signature whose runs come from its plug (/consumatori): what the plug measured in the window, not what the
/// signature's events say. Computed when the page asks for it; <see cref="ApplianceSignature.Count"/> stays the number of events.
/// </summary>
/// <param name="Starts">Runs from the plug started in the window.</param>
/// <param name="MeanPowerW">Mean of their median levels; null without one.</param>
/// <param name="LastStartUtc">The newest of them.</param>
/// <param name="Repetition">Their durations and repetition (<see cref="SignatureProfiles.Repetition"/>).</param>
public sealed record PlugRunSummary(int Starts, double? MeanPowerW, DateTimeOffset? LastStartUtc, RunRepetition Repetition)
{
    public static PlugRunSummary Of(IEnumerable<ApplianceRun> runs, DateTimeOffset fromUtc, DateTimeOffset nowUtc)
    {
        var fromPlug = runs.Where(r => r.FromPlug && r.StartUtc >= fromUtc).ToList();
        return new PlugRunSummary(fromPlug.Count, fromPlug.Count > 0 ? fromPlug.Average(r => r.PowerW) : null,
            fromPlug.Count > 0 ? fromPlug.Max(r => r.StartUtc) : null, SignatureProfiles.Repetition(fromPlug, nowUtc));
    }
}

public static class SignatureProfiles
{
    /// <summary>The energy column of the profile covers this many days.</summary>
    public static readonly TimeSpan EnergyPeriod = TimeSpan.FromDays(7);

    /// <summary>The repetition of the profile covers this many days.</summary>
    public static readonly TimeSpan RepetitionPeriod = TimeSpan.FromDays(30);

    /// <summary>A longer pause between two switch-ons is not a repetition.</summary>
    public static readonly TimeSpan MaxRepeatGap = TimeSpan.FromHours(6);

    /// <summary>Two runs at most this far apart, from an end to the next start, are one session of the appliance.</summary>
    public static readonly TimeSpan SessionGap = TimeSpan.FromSeconds(120);

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
            byPlug.ToDictionary(g => g.Key, g => g.Count()),
            Repetition(runs, nowUtc));
    }

    /// <summary>
    /// Durations, repetition and sessions of the closed runs with a seen end (a timed-out run has no real duration) that
    /// started in the last <see cref="RepetitionPeriod"/>.
    /// </summary>
    public static RunRepetition Repetition(IEnumerable<ApplianceRun> runs, DateTimeOffset nowUtc)
    {
        var from = nowUtc - RepetitionPeriod;
        var closed = runs.Where(r => r.EndUtc is not null && r.EndReason != RunEndReason.Timeout && r.StartUtc >= from).OrderBy(r => r.StartUtc).ToList();
        var intervals = closed.Zip(closed.Skip(1), (a, b) => (b.StartUtc - a.StartUtc).TotalSeconds).Where(s => s <= MaxRepeatGap.TotalSeconds);

        var pulses = new List<double>();
        var energy = new List<double>();
        DateTimeOffset? end = null;
        foreach (var r in closed)
        {
            if (end is not { } e || r.StartUtc - e > SessionGap)
            {
                pulses.Add(0);
                energy.Add(0);
                end = null;
            }
            pulses[^1]++;
            energy[^1] += r.EstimatedWh ?? 0;
            end = end is { } last && last > r.EndUtc!.Value ? last : r.EndUtc;
        }
        pulses.Sort();
        energy.Sort();
        return new RunRepetition(DurationSpread.Of(closed.Select(DurationBands.Seconds)), DurationSpread.Of(intervals), pulses.Count, Percentile(pulses, 0.5), Percentile(energy, 0.5));
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
