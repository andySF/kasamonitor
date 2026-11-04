namespace KasaMonitor.Core.Nilm;

/// <summary>How many values, and their 10th, 50th and 90th percentiles (<see cref="SignatureProfiles.Percentile"/>).</summary>
public sealed record DurationSpread(int Count, double? P10, double? P50, double? P90)
{
    public static DurationSpread Of(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return new DurationSpread(sorted.Count, SignatureProfiles.Percentile(sorted, 0.1), SignatureProfiles.Percentile(sorted, 0.5), SignatureProfiles.Percentile(sorted, 0.9));
    }
}

/// <summary>
/// A run and its two events going to another signature. <see cref="RunId"/> 0: the run is not stored yet (it is saved
/// with the new signature). <see cref="Label"/> = the new signature's name, the events' label from then on.
/// </summary>
public sealed record RunMove(long RunId, long StartEventId, long EndEventId, long FromSignatureId, long TargetId, string Label);

/// <summary>What „împarte după durată” would move: the runs, how long they and the others last, and their energy.</summary>
public sealed record DurationSplitPlan(IReadOnlyList<ApplianceRun> Moved, int SkippedUserRuns, DurationSpread MovedDurations, DurationSpread RemainingDurations, double EnergyWh)
{
    /// <summary>Every moved run takes its switch-on and its switch-off.</summary>
    public int Events => Moved.Count * 2;
}

/// <summary>
/// The duration band of a signature: how long its runs last (<see cref="ApplianceSignature.MinRunSeconds"/>,
/// <see cref="ApplianceSignature.MaxRunSeconds"/>, inclusive; null = no limit). It is set only explicitly (HTTP API, the
/// „fișa”), never inferred, and separates appliances that ΔP and ΔQ cannot: an espresso machine (pulses of ≈ 1.43 kW,
/// 10–20 s) and a kettle (≈ 1.45 kW continuous, 1–7 min).
/// </summary>
public static class DurationBands
{
    public static bool HasBand(ApplianceSignature s) => s.MinRunSeconds is not null || s.MaxRunSeconds is not null;

    public static bool Contains(ApplianceSignature s, double seconds) =>
        (s.MinRunSeconds is not { } min || seconds >= min) && (s.MaxRunSeconds is not { } max || seconds <= max);

    /// <summary>
    /// Two bands with nothing in common but at most their edge. The two sides of a split touch at the limit
    /// (<c>max = 40</c> and <c>min = 40</c>): they are different appliances, so the merge leaves them apart.
    /// </summary>
    public static bool Disjoint(ApplianceSignature a, ApplianceSignature b) =>
        (a.MaxRunSeconds is { } amax && b.MinRunSeconds is { } bmin && amax <= bmin)
        || (b.MaxRunSeconds is { } bmax && a.MinRunSeconds is { } amin && bmax <= amin);

    /// <summary>What is wrong with a band, in Romanian, or null. Both null is valid: it clears the band.</summary>
    public static string? Invalid(int? minSeconds, int? maxSeconds)
    {
        if (minSeconds < 0 || maxSeconds < 0) return "Durata nu poate fi negativă.";
        if (minSeconds is { } min && maxSeconds is { } max && min >= max) return "„minSeconds” trebuie să fie mai mic decât „maxSeconds”.";
        return null;
    }

    /// <summary>A run closed by its own switch-off: the only kind whose duration is a measurement.</summary>
    public static bool IsPaired(ApplianceRun r) => r.EndReason == RunEndReason.Paired && r.EndUtc is not null && r.EndEventId is not null;

    public static double Seconds(ApplianceRun r) => ((r.EndUtc ?? r.StartUtc) - r.StartUtc).TotalSeconds;

    /// <summary>A paired run that left the band of its signature (a signature without a band has no outside).</summary>
    public static bool IsOutOfBand(ApplianceRun r, ApplianceSignature s) => IsPaired(r) && HasBand(s) && !Contains(s, Seconds(r));

    /// <summary>
    /// Where a closed run goes: the only other signature, not hidden, with a band that holds the duration, not linked to
    /// another plug than <paramref name="source"/>, whose centre is compatible with the switch-on. Null when the run
    /// stays: not paired, inside the band (or none), an event labelled by the user (the run keeps both events
    /// together, otherwise it would fall apart at the next rebuild), no candidate, or more than one. An exclusive signature
    /// (<see cref="ExclusiveSignatures"/>) is a candidate only for a run whose two events its plug confirmed.
    /// </summary>
    public static ApplianceSignature? Target(ApplianceRun run, MeterEvent? on, MeterEvent? off, ApplianceSignature source, IEnumerable<ApplianceSignature> all,
        IReadOnlySet<Guid>? exclusivePlugs = null)
    {
        if (!IsOutOfBand(run, source) || on is null || off is null) return null;
        if (on.Source == EventLabelSource.User || off.Source == EventLabelSource.User) return null;
        var seconds = Seconds(run);
        var candidates = all.Where(t => t.Id != source.Id && !t.Hidden && HasBand(t) && Contains(t, seconds)
                                        && (t.PlugId is null || t.PlugId == source.PlugId)
                                        && (t.PlugId is not { } plug || exclusivePlugs?.Contains(plug) != true
                                            || (ExclusiveSignatures.ConfirmedBy(on, plug) && ExclusiveSignatures.ConfirmedBy(off, plug)))
                                        && Compatible(on, t, source)).Take(2).ToList();
        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>
    /// |ΔP| and ΔQ within the anchor radius of <paramref name="target"/>, or of <paramref name="source"/> when one was
    /// split from the other: their centres can then be the same, and neither has to be the nearer one. Only for a pair
    /// that was split: any other signature in the same band of durations is another appliance if its power differs.
    /// </summary>
    private static bool Compatible(MeterEvent on, ApplianceSignature target, ApplianceSignature source) =>
        Within(on.DeltaP, on.DeltaQ, target) || (Related(target, source) && Within(on.DeltaP, on.DeltaQ, source));

    private static bool Related(ApplianceSignature a, ApplianceSignature b) => a.SplitFromId == b.Id || b.SplitFromId == a.Id;

    /// <summary>The transient is left out: the rule is about |ΔP| and ΔQ, and two sides of a split can differ in it by chance.</summary>
    private static bool Within(double deltaP, double deltaQ, ApplianceSignature s) =>
        SignatureClusterer.Distance(new EventFeatures(0, deltaP, deltaQ, double.NaN), SignatureCentre.Of(s), SignatureClusterer.AnchorScale) <= 1;

    /// <summary>
    /// The signature a new event should wait in. The two sides of a split can have the same centre, so „the nearest” would
    /// send a switch-on to one and its switch-off to the other, and the run would never close as paired. Every event near
    /// the pair therefore goes to the signature the other was split from; the run's duration sends it across when it closes.
    /// Unchanged when that signature is gone or hidden, when the event is too far from it, or when a band was cleared.
    /// </summary>
    public static long Canonical(long signatureId, EventFeatures e, IReadOnlyCollection<ApplianceSignature> all)
    {
        if (all.FirstOrDefault(s => s.Id == signatureId) is not { SplitFromId: { } parentId } child || !HasBand(child)) return signatureId;
        if (all.FirstOrDefault(s => s.Id == parentId) is not { Hidden: false } parent || !HasBand(parent)) return signatureId;
        return Within(e.DeltaP, e.DeltaQ, parent) ? parentId : signatureId;
    }

    /// <summary>
    /// The survivor of a merge: it keeps its band and its origin, and takes those of the absorbed one when it has none.
    /// A signature merged with the side split off it (only by hand: the automatic merge leaves disjoint bands apart) undoes
    /// the split: the result holds both kinds of runs, so it has no band and no origin.
    /// </summary>
    public static ApplianceSignature Inherit(ApplianceSignature keep, ApplianceSignature gone)
    {
        if (Related(keep, gone)) return keep with { MinRunSeconds = null, MaxRunSeconds = null, SplitFromId = null };
        var band = HasBand(keep) ? keep : gone;
        var origin = keep.SplitFromId == gone.Id ? gone.SplitFromId : keep.SplitFromId ?? gone.SplitFromId;
        return keep with
        {
            MinRunSeconds = band.MinRunSeconds,
            MaxRunSeconds = band.MaxRunSeconds,
            SplitFromId = origin == keep.Id ? null : origin,
        };
    }

    /// <summary>
    /// The paired runs up to <paramref name="maxSeconds"/> that can move: both events still in the signature
    /// (<paramref name="events"/>), neither labelled by the user. The rest of the paired runs stay, and are the other group.
    /// </summary>
    public static DurationSplitPlan PlanSplit(IEnumerable<ApplianceRun> runs, IReadOnlyDictionary<long, MeterEvent> events, int maxSeconds)
    {
        var paired = runs.Where(IsPaired).ToList();
        var moved = new List<ApplianceRun>();
        var stay = new List<ApplianceRun>();
        var skipped = 0;
        foreach (var r in paired)
        {
            var on = events.GetValueOrDefault(r.StartEventId);
            var off = events.GetValueOrDefault(r.EndEventId!.Value);
            if (Seconds(r) > maxSeconds || on is null || off is null)
            {
                stay.Add(r);
            }
            else if (on.Source == EventLabelSource.User || off.Source == EventLabelSource.User)
            {
                skipped++;
                stay.Add(r);
            }
            else
            {
                moved.Add(r);
            }
        }
        return new DurationSplitPlan(moved, skipped, DurationSpread.Of(moved.Select(Seconds)), DurationSpread.Of(stay.Select(Seconds)), moved.Sum(r => r.EstimatedWh ?? 0));
    }

    /// <summary>
    /// The signature „împarte după durată” joins: the one with that name (any case) within the anchor radius of the moved
    /// power, the closest when several, never hidden, not linked to another plug, and with a band the split can keep
    /// (<see cref="KeepsBand"/>); null = a new one.
    /// </summary>
    public static ApplianceSignature? ExistingTarget(string name, double powerW, int maxSeconds, ApplianceSignature source, IEnumerable<ApplianceSignature> all) =>
        all.Where(t => t.Id != source.Id && !t.Hidden && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                       && (t.PlugId is null || t.PlugId == source.PlugId) && KeepsBand(t, maxSeconds)
                       && Math.Abs(t.MeanDeltaP - powerW) <= SignatureClusterer.Eps(t.MeanDeltaP, powerW, SignatureClusterer.AnchorScale))
            .OrderBy(t => Math.Abs(t.MeanDeltaP - powerW)).FirstOrDefault();

    /// <summary>
    /// Whether a signature can take the runs up to <paramref name="maxSeconds"/> and end its band there without losing what
    /// it says: no band, or one that starts at zero and ends at the limit or below it (it only widens). A band of 50–100 s
    /// is another component with the same name, whatever its power.
    /// </summary>
    private static bool KeepsBand(ApplianceSignature t, int maxSeconds) =>
        !HasBand(t) || ((t.MinRunSeconds ?? 0) == 0 && t.MaxRunSeconds is { } max && max <= maxSeconds);

    /// <summary>
    /// The two sides after a split at <paramref name="maxSeconds"/>: <paramref name="t"/> ends there and remembers where it
    /// came from; <paramref name="s"/> starts there unless it already has a band of its own. When <paramref name="s"/> is
    /// measured by a plug, so is <paramref name="t"/>: a split moves energy between two names, it must not take it out of
    /// the plug's side of the balance (it would then be subtracted twice from the rest of the house).
    /// </summary>
    public static (ApplianceSignature S, ApplianceSignature T) AfterSplit(ApplianceSignature s, ApplianceSignature t, int maxSeconds) =>
        (HasBand(s) ? s : s with { MinRunSeconds = maxSeconds, MaxRunSeconds = null },
            t with
            {
                MaxRunSeconds = maxSeconds,
                MinRunSeconds = t.MinRunSeconds < maxSeconds ? t.MinRunSeconds : null,
                SplitFromId = s.SplitFromId == t.Id ? null : t.SplitFromId ?? s.Id,
                PlugId = t.PlugId ?? s.PlugId,
            });
}
