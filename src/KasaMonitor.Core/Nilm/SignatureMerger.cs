namespace KasaMonitor.Core.Nilm;

/// <summary>Why two signatures are one component (<see cref="SignatureMerger"/>).</summary>
public enum MergeReason
{
    /// <summary>The same name given by the user, and centres within the anchor radius.</summary>
    SameName = 0,

    /// <summary>Their centres are within eps of each other (|ΔP|, ΔQ, and the transient when both know it).</summary>
    Centres = 1,

    /// <summary>Linked to (or clearly „posibil”) the same plug and their |ΔP| differ by less than 15 %.</summary>
    SamePlug = 2,

    /// <summary>
    /// The same inductive component with a variable load (a drum motor's pulses): ΔQ/ΔP alike, |ΔP| within 25 %, and they
    /// occur together in time far more than by chance.
    /// </summary>
    VariableLoad = 3,
}

/// <summary>A signature and its profile (null when it has no event in the window: its stored means are used).</summary>
public sealed record MergeCandidate(ApplianceSignature Signature, SignatureProfile? Profile);

/// <summary>One merge of the plan: <see cref="AbsorbedId"/> goes into <see cref="SurvivorId"/>.</summary>
public sealed record MergeStep(long SurvivorId, long AbsorbedId, MergeReason Reason);

/// <summary>
/// The automatic merge of signatures that are the same component (nightly job, startup after a missed night, „Regrupează
/// acum”). Never merged: two signatures linked to different plugs, two signatures the user named differently, hidden
/// ones, two signatures with disjoint duration bands (<see cref="DurationBands.Disjoint"/>: their runs say they are
/// different appliances, however alike their steps), an exclusive signature (<see cref="ExclusiveSignatures"/>) with
/// anything but another signature of the same plug, both with events the plug confirmed. Otherwise two signatures are merged when
/// <list type="bullet">
/// <item>(c) the user gave them the same name <b>and</b> their centres are within the anchor radius
/// (<see cref="SignatureClusterer.AnchorScale"/>; the same name on distant components stays two rows);</item>
/// <item>(a) their centres are within eps (scale 1; an unknown transient is not compared);</item>
/// <item>(b) both are linked to the same plug, or clearly „posibil” it (at least <see cref="PossibleMinEvents"/> events and
/// <see cref="PossibleMinShare"/> of them from that plug), and their |ΔP| differ by less than <see cref="SamePlugMaxDifference"/>;</item>
/// <item>(d) neither is an anchor, both are inductive (|ΔQ/ΔP| ≥ <see cref="VariableLoadMinQp"/>) and at least
/// <see cref="VariableLoadMinPowerW"/>, their ΔQ/ΔP differ by at most <see cref="VariableLoadMaxQpDifference"/> with the
/// same sign, their |ΔP| by at most <see cref="VariableLoadMaxDifference"/>, they are not „posibil” two different plugs,
/// and they occur together: at least <see cref="CoOccurrenceShare"/> of the smaller one's events have an event of the
/// other within ±<see cref="CoOccurrenceWindow"/>, at least <see cref="MinLift"/> times more than with the other one's
/// times shifted by ±1 h (chance; a dense small load is „together” with everything).</item>
/// </list>
/// A merge with a signature linked to a plug is accepted only when that plug would still label at least 60 % of the
/// merged events (<see cref="SignatureNaming.PlugShare"/>; otherwise the merge would unlink the survivor). The survivor
/// (<see cref="Survivor"/>) is the one with a user label, else the one linked to a plug, else the one with more events.
/// Pairs are merged one at a time, best first (same name, the closest centres, the same plug, the variable load); the
/// merged group's centre is the event-weighted mean, so a chain of small differences cannot drift without limit.
/// </summary>
public static class SignatureMerger
{
    /// <summary>Largest relative |ΔP| difference of two signatures of the same plug (rule b).</summary>
    public const double SamePlugMaxDifference = 0.15;

    /// <summary>Rule b: a „posibil” plug counts with at least this many of the signature's events…</summary>
    public const int PossibleMinEvents = 2;

    /// <summary>…and at least this share of them.</summary>
    public const double PossibleMinShare = 0.3;

    /// <summary>Rule d: largest difference of the ΔQ/ΔP ratios of the two centres (same sign).</summary>
    public const double VariableLoadMaxQpDifference = 0.15;

    /// <summary>Rule d: largest relative |ΔP| difference.</summary>
    public const double VariableLoadMaxDifference = 0.25;

    /// <summary>Rule d: only inductive loads (motors): |ΔQ/ΔP| of both at least this (resistive ≈ 0.06, small loads ≈ 0.3).</summary>
    public const double VariableLoadMinQp = 0.5;

    /// <summary>Rule d: only components of at least this |ΔP|.</summary>
    public const double VariableLoadMinPowerW = 150;

    /// <summary>Rule d: an event of the other signature within this time counts as occurring together.</summary>
    public static readonly TimeSpan CoOccurrenceWindow = TimeSpan.FromMinutes(10);

    /// <summary>Rule d: share of the smaller signature's events that must occur together with the other.</summary>
    public const double CoOccurrenceShare = 0.6;

    /// <summary>Rule d: the co-occurrence must be at least this many times the one by chance (times shifted by ±1 h).</summary>
    public const double MinLift = 2;

    /// <summary>Rule d: the shift of the chance co-occurrence.</summary>
    public static readonly TimeSpan ChanceShift = TimeSpan.FromHours(1);

    /// <summary>The survivor of a merge: user label, then linked to a plug, then more events, then the older id.</summary>
    public static ApplianceSignature Survivor(ApplianceSignature a, ApplianceSignature b) => Compare(a, b) >= 0 ? a : b;

    private static int Compare(ApplianceSignature a, ApplianceSignature b)
    {
        var c = (a.LabelSource == SignatureLabelSource.User).CompareTo(b.LabelSource == SignatureLabelSource.User);
        if (c != 0) return c;
        c = (a.PlugId is not null).CompareTo(b.PlugId is not null);
        if (c != 0) return c;
        c = a.Count.CompareTo(b.Count);
        return c != 0 ? c : b.Id.CompareTo(a.Id);
    }

    private static string? UserName(ApplianceSignature s) =>
        s.LabelSource == SignatureLabelSource.User ? SignatureNaming.CleanName(s.Name) : null;

    private sealed class Group
    {
        public required ApplianceSignature Signature;
        public required double P, Q, T;
        public required double Weight, KnownWeight;
        public required Dictionary<Guid, int> PlugEvents;   // events labelled per plug (not excluded)
        public required List<DateTimeOffset> Times;         // event times, oldest first (empty without a profile)

        public SignatureCentre Centre => new(Signature.Id, P, Q, T, KnownWeight > 0);

        /// <summary>The linked plug, else the „posibil” plug when it labelled ≥ 2 events and ≥ 30 % of them.</summary>
        public Guid? Plug
        {
            get
            {
                if (Signature.PlugId is { } linked) return linked;
                if (PlugEvents.Count == 0) return null;
                var top = PlugEvents.OrderByDescending(kv => kv.Value).First();
                return top.Value >= PossibleMinEvents && top.Value >= PossibleMinShare * Weight ? top.Key : null;
            }
        }
    }

    /// <summary>Why <paramref name="a"/> and <paramref name="b"/> are the same component, or null.</summary>
    public static MergeReason? Reason(MergeCandidate a, MergeCandidate b, IReadOnlySet<Guid>? exclusivePlugs = null) =>
        Reason(ToGroup(a), ToGroup(b), exclusivePlugs ?? NoPlugs).Reason;

    private static readonly IReadOnlySet<Guid> NoPlugs = new HashSet<Guid>();

    /// <summary>Neither is exclusive, or both belong to the same plug and hold something it confirmed.</summary>
    private static bool ExclusiveAllows(Group a, Group b, IReadOnlySet<Guid> exclusivePlugs)
    {
        if (!ExclusiveSignatures.IsExclusive(a.Signature, exclusivePlugs) && !ExclusiveSignatures.IsExclusive(b.Signature, exclusivePlugs)) return true;
        return a.Signature.PlugId is { } plug && plug == b.Signature.PlugId && a.PlugEvents.GetValueOrDefault(plug) > 0 && b.PlugEvents.GetValueOrDefault(plug) > 0;
    }

    private static (MergeReason? Reason, double Score) Reason(Group a, Group b, IReadOnlySet<Guid> exclusivePlugs)
    {
        if (a.Signature.PlugId is { } pa && b.Signature.PlugId is { } pb && pa != pb) return (null, 0);
        if (DurationBands.Disjoint(a.Signature, b.Signature)) return (null, 0);
        if (!ExclusiveAllows(a, b, exclusivePlugs)) return (null, 0);
        var d = SignatureClusterer.Distance(a.Centre, b.Centre);
        (MergeReason? Reason, double Score) result;
        var nameA = UserName(a.Signature);
        var nameB = UserName(b.Signature);
        if (nameA is not null && nameB is not null)
            result = string.Equals(nameA, nameB, StringComparison.OrdinalIgnoreCase)
                     && SignatureClusterer.Distance(a.Centre, b.Centre, SignatureClusterer.AnchorScale) <= 1
                ? (MergeReason.SameName, -1)
                : (null, 0);
        else if (d <= 1)
            result = (MergeReason.Centres, d);
        else
        {
            var diff = Diff(a, b);
            if (a.Plug is { } plugA && plugA == b.Plug && diff < SamePlugMaxDifference) result = (MergeReason.SamePlug, 1 + diff);
            else if (VariableLoad(a, b)) result = (MergeReason.VariableLoad, 2 + diff);
            else result = (null, 0);
        }
        if (result.Reason is null) return result;

        // The survivor must stay linked: its plug still labels ≥ 60 % of the merged events.
        if ((a.Signature.PlugId ?? b.Signature.PlugId) is { } linkedPlug)
        {
            var share = (a.PlugEvents.GetValueOrDefault(linkedPlug) + b.PlugEvents.GetValueOrDefault(linkedPlug)) / (a.Weight + b.Weight);
            if (share < SignatureNaming.PlugShare) return (null, 0);
        }
        return result;
    }

    private static double Diff(Group a, Group b) => Math.Abs(a.P - b.P) / Math.Max(Math.Max(a.P, b.P), 1e-9);

    private static bool VariableLoad(Group a, Group b)
    {
        if (a.Signature.IsAnchor || b.Signature.IsAnchor) return false;
        if (Math.Min(a.P, b.P) < VariableLoadMinPowerW) return false;
        var qpA = a.Q / a.P;
        var qpB = b.Q / b.P;
        if (Math.Min(Math.Abs(qpA), Math.Abs(qpB)) < VariableLoadMinQp || Math.Sign(qpA) != Math.Sign(qpB)) return false;
        if (Math.Abs(qpA - qpB) > VariableLoadMaxQpDifference || Diff(a, b) > VariableLoadMaxDifference) return false;
        if (a.Plug is { } x && b.Plug is { } y && x != y) return false;
        var (share, lift) = CoOccurrenceLift(a.Times, b.Times);
        return share >= CoOccurrenceShare && lift >= MinLift;
    }

    /// <summary>
    /// Share of the smaller list's times that have a time of the other list within ±<see cref="CoOccurrenceWindow"/>
    /// (both sorted; 0 when either is empty).
    /// </summary>
    public static double CoOccurrence(IReadOnlyList<DateTimeOffset> a, IReadOnlyList<DateTimeOffset> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        return Together(small, large, TimeSpan.Zero);
    }

    /// <summary>
    /// The co-occurrence (<see cref="CoOccurrence"/>) and its lift over chance: divided by the mean co-occurrence with the
    /// larger list shifted by +1 h and by −1 h (infinite when that chance is 0).
    /// </summary>
    public static (double Share, double Lift) CoOccurrenceLift(IReadOnlyList<DateTimeOffset> a, IReadOnlyList<DateTimeOffset> b)
    {
        if (a.Count == 0 || b.Count == 0) return (0, 0);
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var share = Together(small, large, TimeSpan.Zero);
        var chance = (Together(small, large, ChanceShift) + Together(small, large, -ChanceShift)) / 2;
        return (share, chance > 0 ? share / chance : share > 0 ? double.PositiveInfinity : 0);
    }

    private static double Together(IReadOnlyList<DateTimeOffset> small, IReadOnlyList<DateTimeOffset> large, TimeSpan shift)
    {
        var together = 0;
        var j = 0;
        foreach (var t in small)
        {
            while (j < large.Count && large[j] + shift < t - CoOccurrenceWindow) j++;
            if (j < large.Count && large[j] + shift <= t + CoOccurrenceWindow) together++;
        }
        return (double)together / small.Count;
    }

    private static Group ToGroup(MergeCandidate c)
    {
        var centre = c.Profile?.Centre ?? SignatureCentre.Of(c.Signature);
        var weight = Math.Max(1, c.Profile?.Events ?? c.Signature.Count);
        var known = c.Profile is { } p ? p.TransientKnown : centre.HasTransient ? weight : 0;
        var plugEvents = c.Profile?.PlugEvents?.ToDictionary(kv => kv.Key, kv => kv.Value)
                         ?? (c.Profile?.TopPlugId is { } top ? new Dictionary<Guid, int> { [top] = c.Profile.TopPlugEvents } : new Dictionary<Guid, int>());
        return new Group
        {
            Signature = c.Signature,
            P = centre.MeanDeltaP,
            Q = centre.MeanDeltaQ,
            T = centre.HasTransient ? centre.MeanTransient : 0,
            Weight = weight,
            KnownWeight = known,
            PlugEvents = plugEvents,
            Times = c.Profile?.EventTimes?.OrderBy(t => t).ToList() ?? new List<DateTimeOffset>(),
        };
    }

    /// <summary>The merges to apply, in order (a later step can name the survivor of an earlier one).</summary>
    public static IReadOnlyList<MergeStep> Plan(IReadOnlyList<MergeCandidate> candidates, IReadOnlySet<Guid>? exclusivePlugs = null)
    {
        var exclusive = exclusivePlugs ?? NoPlugs;
        var groups = candidates.Where(c => !c.Signature.Hidden).Select(ToGroup).ToList();
        var steps = new List<MergeStep>();
        while (true)
        {
            (int I, int J, MergeReason Reason, double Score)? best = null;
            for (var i = 0; i < groups.Count; i++)
                for (var j = i + 1; j < groups.Count; j++)
                {
                    var (reason, score) = Reason(groups[i], groups[j], exclusive);
                    if (reason is { } r && (best is null || score < best.Value.Score)) best = (i, j, r, score);
                }
            if (best is not { } b) return steps;

            var x = groups[b.I];
            var y = groups[b.J];
            var survivor = Survivor(x.Signature, y.Signature);
            var (keep, gone) = survivor.Id == x.Signature.Id ? (x, y) : (y, x);
            steps.Add(new MergeStep(keep.Signature.Id, gone.Signature.Id, b.Reason));

            var w = keep.Weight + gone.Weight;
            var kw = keep.KnownWeight + gone.KnownWeight;
            var plugs = new Dictionary<Guid, int>(keep.PlugEvents);
            foreach (var (plug, n) in gone.PlugEvents) plugs[plug] = plugs.GetValueOrDefault(plug) + n;
            var merged = new Group
            {
                Signature = DurationBands.Inherit(keep.Signature, gone.Signature) with
                {
                    PlugId = keep.Signature.PlugId ?? gone.Signature.PlugId,
                    Count = keep.Signature.Count + gone.Signature.Count,
                },
                P = (keep.P * keep.Weight + gone.P * gone.Weight) / w,
                Q = (keep.Q * keep.Weight + gone.Q * gone.Weight) / w,
                T = kw > 0 ? (keep.T * keep.KnownWeight + gone.T * gone.KnownWeight) / kw : 0,
                Weight = w,
                KnownWeight = kw,
                PlugEvents = plugs,
                Times = keep.Times.Concat(gone.Times).OrderBy(t => t).ToList(),
            };
            groups.Remove(gone);
            groups[groups.IndexOf(keep)] = merged;
        }
    }
}
