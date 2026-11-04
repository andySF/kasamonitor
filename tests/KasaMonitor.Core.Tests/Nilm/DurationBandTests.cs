using FluentAssertions;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

/// <summary>
/// The duration band of a signature (0.13.0): an espresso machine (pulses of ≈ 1.43 kW, 10–20 s) and a kettle (≈ 1.45 kW
/// continuous, 1–7 min), measured on one plug, differ only by how long a run lasts.
/// </summary>
public class DurationBandTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Plug = Guid.Parse("00000000-0000-0000-0000-00000000b0c1");
    private static readonly Guid OtherPlug = Guid.Parse("00000000-0000-0000-0000-00000000b0c2");

    private static ApplianceSignature Sig(long id, double p = 1430, double q = 90, int? min = null, int? max = null, Guid? plug = null,
        bool hidden = false, long? splitFrom = null, string? name = null) =>
        new(id, name ?? $"S{id}", "i", "other", p, q, 1, 10, T0, SignatureLabelSource.User, plug, hidden, min, max, splitFrom);

    private static MeterEvent On(double p = 1430, double q = 90, EventLabelSource source = EventLabelSource.None) =>
        new(1, Guid.Empty, T0, p, q, 0, 1, 0, 300, 1, null, source);

    private static MeterEvent Off(double p = 1430, double q = 90, EventLabelSource source = EventLabelSource.None) =>
        new(2, Guid.Empty, T0.AddSeconds(15), -p, -q, 0, 1, 0, 1730, 1, null, source);

    private static ApplianceRun Run(double seconds, RunEndReason reason = RunEndReason.Paired, long signature = 1) =>
        new(7, signature, T0, T0.AddSeconds(seconds), 1430, 1430 * seconds / 3600, 1, 2, 300, reason);

    [Theory]
    [InlineData(null, null, 5, true)]
    [InlineData(45, null, 44.9, false)]
    [InlineData(45, null, 45, true)]
    [InlineData(null, 40, 40, true)]
    [InlineData(null, 40, 40.5, false)]
    [InlineData(10, 40, 9, false)]
    public void Band_IsInclusive_AndAbsentMeansNoLimit(int? min, int? max, double seconds, bool inside) =>
        DurationBands.Contains(Sig(1, min: min, max: max), seconds).Should().Be(inside);

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(45, null, null)]
    [InlineData(null, 40, null)]
    [InlineData(0, 40, null)]
    [InlineData(40, 40, "min")]
    [InlineData(50, 40, "min")]
    [InlineData(-1, null, "negativ")]
    [InlineData(null, -5, "negativ")]
    public void Band_IsValidated(int? min, int? max, string? error)
    {
        var message = DurationBands.Invalid(min, max);
        if (error is null) message.Should().BeNull();
        else message.Should().Contain(error);
    }

    [Fact]
    public void ARunOutOfTheBandOfItsSignature_GoesToTheOnlyOtherSignatureWhoseBandHoldsIt()
    {
        var kettle = Sig(1, min: 45);
        var espresso = Sig(2, max: 40);
        DurationBands.Target(Run(15), On(), Off(), kettle, new[] { kettle, espresso }).Should().Be(espresso);
        DurationBands.Target(Run(90, signature: 2), On(), Off(), espresso, new[] { kettle, espresso }).Should().Be(kettle, "both ways");
    }

    [Fact]
    public void ARunInsideTheBand_OrASignatureWithoutOne_StaysWhereItIs()
    {
        var espresso = Sig(2, max: 40);
        var kettle = Sig(1, min: 45);
        DurationBands.Target(Run(90), On(), Off(), kettle, new[] { kettle, espresso }).Should().BeNull("inside its own band");
        var noBand = Sig(1);
        DurationBands.Target(Run(15), On(), Off(), noBand, new[] { noBand, espresso }).Should().BeNull("no band: never out of it");
    }

    [Fact]
    public void TheEdgeOfASplit_BelongsToBoth_SoARunOfExactlyThatLengthStays()
    {
        var kettle = Sig(1, min: 40);
        var espresso = Sig(2, max: 40);
        DurationBands.Target(Run(40), On(), Off(), kettle, new[] { kettle, espresso }).Should().BeNull();
        DurationBands.Target(Run(40, signature: 2), On(), Off(), espresso, new[] { kettle, espresso }).Should().BeNull();
    }

    [Fact]
    public void AnEventLabelledByTheUser_HoldsTheWholeRunWhereItIs()
    {
        var kettle = Sig(1, min: 45);
        var espresso = Sig(2, max: 40);
        DurationBands.Target(Run(15), On(source: EventLabelSource.User), Off(), kettle, new[] { kettle, espresso }).Should().BeNull();
        DurationBands.Target(Run(15), On(), Off(source: EventLabelSource.User), kettle, new[] { kettle, espresso }).Should().BeNull();
        DurationBands.Target(Run(15), On(source: EventLabelSource.AutoPlug), Off(source: EventLabelSource.Model), kettle, new[] { kettle, espresso })
            .Should().Be(espresso, "only the user's label holds it");
    }

    [Fact]
    public void TwoSignaturesThatCouldTakeIt_MeanItStays()
    {
        var kettle = Sig(1, min: 45);
        DurationBands.Target(Run(15), On(), Off(), kettle, new[] { kettle, Sig(2, max: 40), Sig(3, min: 10, max: 20) }).Should().BeNull();
    }

    [Theory]
    [InlineData(RunEndReason.HouseBelowLevel)]
    [InlineData(RunEndReason.Timeout)]
    public void AnUncertainEnd_IsNeverMoved(RunEndReason reason)
    {
        var kettle = Sig(1, min: 45);
        DurationBands.Target(Run(15, reason), On(), Off(), kettle, new[] { kettle, Sig(2, max: 40) }).Should().BeNull();
    }

    [Fact]
    public void ARunStillOpen_OrWithoutAnEndEvent_IsNeverMoved()
    {
        var kettle = Sig(1, min: 45);
        var open = Run(15) with { EndUtc = null, EndEventId = null };
        DurationBands.Target(open, On(), null, kettle, new[] { kettle, Sig(2, max: 40) }).Should().BeNull();
    }

    [Fact]
    public void AHiddenTarget_OrOneWithoutABand_OrOutOfItsBand_OrOnAnotherPlug_IsNoCandidate()
    {
        var kettle = Sig(1, min: 45, plug: Plug);
        Target(Sig(2, max: 40, plug: Plug, hidden: true)).Should().BeNull("hidden");
        Target(Sig(2, plug: Plug)).Should().BeNull("no band");
        Target(Sig(2, max: 10, plug: Plug)).Should().BeNull("15 s is outside its band");
        Target(Sig(2, max: 40, plug: OtherPlug)).Should().BeNull("another plug");
        Target(Sig(2, max: 40, plug: Plug)).Should().NotBeNull("the same plug");
        Target(Sig(2, max: 40)).Should().NotBeNull("no plug at all");

        ApplianceSignature? Target(ApplianceSignature t) => DurationBands.Target(Run(15), On(), Off(), kettle, new[] { kettle, t });
    }

    [Fact]
    public void ATargetFarAway_IsNoCandidate_UnlessItWasSplitOffThatSignature()
    {
        // ±1.5 eps around 1430 W ≈ 176 W, ΔQ ±194 VAR.
        var kettle = Sig(1, min: 45, p: 1430);
        var far = Sig(2, max: 40, p: 2000);
        DurationBands.Target(Run(15), On(), Off(), kettle, new[] { kettle, far }).Should().BeNull("2 kW is another appliance");

        var split = far with { SplitFromId = kettle.Id };
        DurationBands.Target(Run(15), On(), Off(), kettle, new[] { kettle, split }).Should().Be(split, "judged against the signature it was split from");
        DurationBands.Target(Run(15), On(p: 1700), Off(p: 1700), kettle, new[] { kettle, split }).Should().BeNull("too far from both");

        var reactive = Sig(2, max: 40, q: 900);
        DurationBands.Target(Run(15), On(), Off(), kettle, new[] { kettle, reactive }).Should().BeNull("ΔQ counts too");
    }

    [Fact]
    public void TheSameCentre_IsNoObstacle_AndAnUnknownTransientIsNotCompared()
    {
        var kettle = Sig(1, min: 45) with { MeanTransient = 5 };
        var espresso = Sig(2, max: 40, splitFrom: 1) with { MeanTransient = 1 };
        var on = On() with { TransientRatio = 1, RiseSeconds = 0 };
        DurationBands.Target(Run(15), on, Off(), kettle, new[] { kettle, espresso }).Should().Be(espresso);
    }

    [Fact]
    public void ARunAcrossMidnight_TakesItsShareOfEachDayToTheNewSignature()
    {
        var kettle = Sig(1, min: 45);
        var espresso = Sig(2, max: 40);
        var midnight = new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        var run = new ApplianceRun(7, 1, midnight.AddSeconds(-10), midnight.AddSeconds(20), 1440, 12, 1, 2, 300, RunEndReason.Paired);

        var target = DurationBands.Target(run, On(), Off(), kettle, new[] { kettle, espresso });

        target.Should().Be(espresso);
        var moved = run with { SignatureId = target!.Id };
        moved.SignatureId.Should().Be(2);
        ApplianceEnergy.InRange(moved, midnight.AddDays(-1), midnight, midnight.AddDays(1)).Should().BeApproximately(4, 1e-9, "10 s of 30 s before midnight");
        ApplianceEnergy.InRange(moved, midnight, midnight.AddDays(1), midnight.AddDays(1)).Should().BeApproximately(8, 1e-9, "20 s of 30 s after it");
    }

    [Fact]
    public void Bands_AreDisjointWhenTheyOnlyTouch_NeverWithoutTwoBands()
    {
        DurationBands.Disjoint(Sig(1, min: 40), Sig(2, max: 40)).Should().BeTrue("the two sides of a split");
        DurationBands.Disjoint(Sig(1, min: 45), Sig(2, max: 40)).Should().BeTrue();
        DurationBands.Disjoint(Sig(2, max: 40), Sig(1, min: 45)).Should().BeTrue();
        DurationBands.Disjoint(Sig(1, min: 30), Sig(2, max: 40)).Should().BeFalse("they overlap");
        DurationBands.Disjoint(Sig(1, min: 10, max: 20), Sig(2, min: 15, max: 30)).Should().BeFalse();
        DurationBands.Disjoint(Sig(1, min: 40), Sig(2)).Should().BeFalse("no band: no limit");
        DurationBands.Disjoint(Sig(1), Sig(2)).Should().BeFalse();
    }

    private static MergeCandidate C(ApplianceSignature s) => new(s, null);

    [Fact]
    public void TheMerger_LeavesDisjointBandsApart_WhateverTheRule()
    {
        var kettle = Sig(1, min: 40) with { LabelSource = SignatureLabelSource.Cluster };
        var espresso = Sig(2, max: 40) with { LabelSource = SignatureLabelSource.Cluster };
        SignatureMerger.Reason(C(kettle with { MinRunSeconds = null }), C(espresso)).Should().Be(MergeReason.Centres, "without the second band the same centre merges");
        SignatureMerger.Reason(C(kettle), C(espresso)).Should().BeNull("rule a, same centre");

        var sameName = (Sig(1, min: 40, name: "Bucătărie"), Sig(2, max: 40, name: "Bucătărie"));
        SignatureMerger.Reason(C(sameName.Item1), C(sameName.Item2)).Should().BeNull("rule c, same name");
        SignatureMerger.Reason(C(sameName.Item1 with { MinRunSeconds = null }), C(sameName.Item2)).Should().Be(MergeReason.SameName);

        var plugA = kettle with { PlugId = Plug, MeanDeltaP = 1000 };
        var plugB = espresso with { PlugId = Plug, MeanDeltaP = 1120 };
        SignatureMerger.Reason(C(plugA), C(plugB)).Should().BeNull("rule b, same plug");

        SignatureMerger.Plan(new[] { C(kettle), C(espresso) }).Should().BeEmpty();
    }

    [Fact]
    public void TheMerger_DifferentNames_StayApartAsBefore_AndAMergedGroupKeepsABand()
    {
        SignatureMerger.Reason(C(Sig(1, min: 10, max: 60, name: "A")), C(Sig(2, min: 20, max: 90, name: "B"))).Should().BeNull();

        // „x” (no band) joins the kettle first (closer); the group then has the kettle's band, so the espresso machine stays out.
        var kettle = Sig(1, p: 1450, min: 40) with { LabelSource = SignatureLabelSource.Cluster };
        var espresso = Sig(2, p: 1300, max: 40) with { LabelSource = SignatureLabelSource.Cluster };
        var x = Sig(3, p: 1380) with { LabelSource = SignatureLabelSource.Cluster, Count = 1 };
        SignatureMerger.Plan(new[] { C(kettle), C(espresso), C(x) }).Should().ContainSingle()
            .Which.Should().Match<MergeStep>(m => m.AbsorbedId == 3);
    }

    [Fact]
    public void Inherit_TheSurvivorKeepsWhatItHas_AndTakesWhatItLacks()
    {
        DurationBands.Inherit(Sig(1, min: 45), Sig(2, max: 40)).Should().Match<ApplianceSignature>(s => s.MinRunSeconds == 45 && s.MaxRunSeconds == null);
        DurationBands.Inherit(Sig(1), Sig(2, max: 40)).MaxRunSeconds.Should().Be(40);
        DurationBands.Inherit(Sig(1), Sig(2, splitFrom: 9)).SplitFromId.Should().Be(9);
        DurationBands.Inherit(Sig(1, splitFrom: 2), Sig(2)).SplitFromId.Should().BeNull("not split from itself after the merge");
        DurationBands.Inherit(Sig(1), Sig(2, splitFrom: 1)).SplitFromId.Should().BeNull();
    }

    [Fact]
    public void Inherit_MergingASignatureWithTheSideSplitOffIt_UndoesTheSplit()
    {
        var kettle = Sig(1, min: 40);
        var espresso = Sig(2, max: 40, splitFrom: 1);
        foreach (var merged in new[] { DurationBands.Inherit(kettle, espresso), DurationBands.Inherit(espresso, kettle) })
        {
            merged.MinRunSeconds.Should().BeNull();
            merged.MaxRunSeconds.Should().BeNull();
            merged.SplitFromId.Should().BeNull();
        }
        DurationBands.Inherit(Sig(1, min: 40), Sig(3, max: 40)).MinRunSeconds.Should().Be(40, "two signatures never split keep the rule of the survivor");
    }

    [Fact]
    public void Canonical_ANewEventNearASplitPair_GoesToTheSignatureItWasSplitFrom_SoSwitchOnAndOffPair()
    {
        var kettle = Sig(1, min: 40);
        var espresso = Sig(2, max: 40, splitFrom: 1);
        var all = new[] { kettle, espresso };
        var e = new EventFeatures(1, 1430, 90, 1);
        DurationBands.Canonical(2, e, all).Should().Be(1);
        DurationBands.Canonical(1, e, all).Should().Be(1);
        DurationBands.Canonical(2, new EventFeatures(1, 2500, 90, 1), all).Should().Be(2, "too far from the parent");
        DurationBands.Canonical(2, e, new[] { espresso }).Should().Be(2, "the parent is gone");
        DurationBands.Canonical(2, e, new[] { kettle with { Hidden = true }, espresso }).Should().Be(2, "a hidden parent attracts nothing");
        DurationBands.Canonical(2, e, new[] { kettle with { MinRunSeconds = null }, espresso }).Should().Be(2, "the band was cleared: nothing would move it back");
        DurationBands.Canonical(2, e, new[] { kettle, espresso with { MaxRunSeconds = null } }).Should().Be(2);
        DurationBands.Canonical(99, e, all).Should().Be(99, "an unknown id stays");
    }

    private static void Check(DurationSpread s, int count, double p10, double p50, double p90)
    {
        s.Count.Should().Be(count);
        s.P10.Should().BeApproximately(p10, 1e-9);
        s.P50.Should().BeApproximately(p50, 1e-9);
        s.P90.Should().BeApproximately(p90, 1e-9);
    }

    private static (List<ApplianceRun> Runs, Dictionary<long, MeterEvent> Events) History(params (double Seconds, RunEndReason Reason, EventLabelSource Source)[] runs)
    {
        var list = new List<ApplianceRun>();
        var events = new Dictionary<long, MeterEvent>();
        var id = 1L;
        foreach (var (seconds, reason, source) in runs)
        {
            var start = T0.AddHours(list.Count);
            var on = id++;
            var off = id++;
            events[on] = new MeterEvent(on, Guid.Empty, start, 1430, 90, 0, 1, 0, 300, 1, null, source);
            events[off] = new MeterEvent(off, Guid.Empty, start.AddSeconds(seconds), -1430, -90, 0, 1, 0, 1730, 1);
            list.Add(new ApplianceRun(list.Count + 1, 1, start, start.AddSeconds(seconds), 1430, 1430 * seconds / 3600, on, off, 300, reason));
        }
        return (list, events);
    }

    [Fact]
    public void PlanSplit_TakesThePairedRunsUpToTheLimit_AndLeavesTheOthersAndTheUsersLabels()
    {
        var (runs, events) = History(
            (10, RunEndReason.Paired, EventLabelSource.None),
            (20, RunEndReason.Paired, EventLabelSource.AutoPlug),
            (30, RunEndReason.Paired, EventLabelSource.None),
            (40, RunEndReason.Paired, EventLabelSource.None),
            (15, RunEndReason.Paired, EventLabelSource.User),
            (12, RunEndReason.HouseBelowLevel, EventLabelSource.None),
            (21600, RunEndReason.Timeout, EventLabelSource.None),
            (60, RunEndReason.Paired, EventLabelSource.None),
            (300, RunEndReason.Paired, EventLabelSource.None));
        runs.Add(new ApplianceRun(50, 1, T0, null, 1430, null, 99, null));

        var plan = DurationBands.PlanSplit(runs, events, 40);

        plan.Moved.Select(r => r.Id).Should().Equal(1, 2, 3, 4);
        plan.Events.Should().Be(8);
        plan.SkippedUserRuns.Should().Be(1);
        plan.EnergyWh.Should().BeApproximately(1430 * 100 / 3600.0, 1e-9);
        Check(plan.MovedDurations, 4, 13, 25, 37);
        plan.RemainingDurations.Count.Should().Be(3, "what stays: the one held by the user's label, and the two long ones");
        plan.RemainingDurations.P50.Should().Be(60);
    }

    [Fact]
    public void PlanSplit_ARunWhoseEventIsNoLongerInTheSignature_IsLeftAlone()
    {
        var (runs, events) = History((10, RunEndReason.Paired, EventLabelSource.None));
        events.Remove(runs[0].EndEventId!.Value);
        DurationBands.PlanSplit(runs, events, 40).Moved.Should().BeEmpty();
    }

    [Fact]
    public void SplitBands_SetTheLimitOnBothSides_ButKeepABandTheSignatureAlreadyHas()
    {
        DurationBands.AfterSplit(Sig(1), Sig(2), 40).Should().Match<(ApplianceSignature S, ApplianceSignature T)>(x =>
            x.S.MinRunSeconds == 40 && x.S.MaxRunSeconds == null && x.T.MaxRunSeconds == 40 && x.T.MinRunSeconds == null && x.T.SplitFromId == 1);

        var (s, _) = DurationBands.AfterSplit(Sig(1, min: 120, max: 600), Sig(2), 40);
        s.MinRunSeconds.Should().Be(120);
        s.MaxRunSeconds.Should().Be(600);

        DurationBands.AfterSplit(Sig(1), Sig(2, min: 10, max: 90), 40).T.Should().Match<ApplianceSignature>(t => t.MinRunSeconds == 10 && t.MaxRunSeconds == 40);
        DurationBands.AfterSplit(Sig(1), Sig(2, min: 50), 40).T.MinRunSeconds.Should().BeNull("a minimum over the new maximum cannot stay");
        DurationBands.AfterSplit(Sig(1, splitFrom: 2), Sig(2), 40).T.SplitFromId.Should().BeNull("not both ways");
        DurationBands.AfterSplit(Sig(1, plug: Plug), Sig(2), 40).T.PlugId.Should().Be(Plug, "the energy stays on the plug's side of the balance");
        DurationBands.AfterSplit(Sig(1, plug: Plug), Sig(2, plug: Plug), 40).T.PlugId.Should().Be(Plug);
    }

    [Fact]
    public void ExistingTarget_ByNameAndPower_TheClosestOne_NeverItselfHiddenOrOnAnotherPlug()
    {
        var s = Sig(1, plug: Plug, name: "Fierbător");
        var near = Sig(2, p: 1400, name: "Espressor");
        var nearer = Sig(3, p: 1425, name: "espressor");
        var far = Sig(4, p: 287, name: "Espressor");
        var hidden = Sig(5, p: 1430, name: "Espressor", hidden: true);
        var other = Sig(6, p: 1430, name: "Espressor", plug: OtherPlug);
        DurationBands.ExistingTarget("Espressor", 1430, 40, s, new[] { s, near, nearer, far, hidden, other }).Should().Be(nearer);
        DurationBands.ExistingTarget("Espressor", 1430, 40, s, new[] { s, far }).Should().BeNull("287 W is a pump");
        DurationBands.ExistingTarget("Fierbător", 1430, 40, s, new[] { s }).Should().BeNull();
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 40, true)]
    [InlineData(null, 30, true)]
    [InlineData(0, 30, true)]
    [InlineData(null, 41, false)]
    [InlineData(null, 100, false)]
    [InlineData(50, 100, false)]
    [InlineData(5, 40, false)]
    [InlineData(50, null, false)]
    [InlineData(0, null, false)]
    public void ExistingTarget_OnlyOneWhoseBandTheSplitKeepsOrWidens(int? min, int? max, bool joins)
    {
        var s = Sig(1, name: "Fierbător");
        var t = Sig(2, name: "Espressor", min: min, max: max);
        DurationBands.ExistingTarget("Espressor", 1430, 40, s, new[] { s, t }).Should().Be(joins ? t : null);
    }

    [Fact]
    public void TheEventSource_HasItsCodeAndItsRomanianName()
    {
        NilmSources.Code(EventLabelSource.DurationRule).Should().Be("duration");
        NilmSources.Name(EventLabelSource.DurationRule).Should().Be("după durată");
        NilmSources.EventFromInt(4).Should().Be(EventLabelSource.DurationRule);
    }
}

/// <summary>Durations, repetition and sessions of the „fișa” (0.13.0): information only, never used by the classification.</summary>
public class RunRepetitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T0 = Now.AddDays(-1);

    private static ApplianceRun Run(double startSeconds, double seconds, double wh = 5, RunEndReason reason = RunEndReason.Paired) =>
        new(1, 1, T0.AddSeconds(startSeconds), T0.AddSeconds(startSeconds + seconds), 1430, wh, 1, 2, 300, reason);

    private static void Check(DurationSpread s, int count, double p10, double p50, double p90)
    {
        s.Count.Should().Be(count);
        s.P10.Should().BeApproximately(p10, 1e-9);
        s.P50.Should().BeApproximately(p50, 1e-9);
        s.P90.Should().BeApproximately(p90, 1e-9);
    }

    [Fact]
    public void Nothing_GivesEmptyNumbers()
    {
        var r = SignatureProfiles.Repetition(Array.Empty<ApplianceRun>(), Now);
        r.Durations.Should().Be(new DurationSpread(0, null, null, null));
        r.Intervals.Should().Be(new DurationSpread(0, null, null, null));
        r.Sessions.Should().Be(0);
        r.PulsesPerSessionP50.Should().BeNull();
        r.EnergyPerSessionWhP50.Should().BeNull();
    }

    [Fact]
    public void Durations_ArePercentiles_OfTheClosedRunsWithASeenEnd()
    {
        var runs = new[] { Run(0, 10), Run(1000, 20), Run(2000, 30), Run(3000, 40), Run(4000, 50), Run(5000, 9999, reason: RunEndReason.Timeout) };
        runs = runs.Append(new ApplianceRun(9, 1, T0, null, 1430, null, 1, null)).ToArray();

        var r = SignatureProfiles.Repetition(runs, Now);

        Check(r.Durations, 5, 14, 30, 46);
    }

    [Fact]
    public void OnlyTheLast30Days_Count()
    {
        var r = SignatureProfiles.Repetition(new[] { Run(0, 10), Run(-31 * 86400, 500) }, Now);
        r.Durations.Count.Should().Be(1);
        r.Durations.P50.Should().Be(10);
    }

    [Fact]
    public void Intervals_BetweenConsecutiveStarts_LeaveOutGapsOverSixHours()
    {
        // Starts at 0, 120, 240, then a pause of 8 h (left out), then exactly 6 h later (kept).
        var starts = new[] { 0.0, 120, 240, 240 + 8 * 3600, 240 + 14 * 3600 };
        var r = SignatureProfiles.Repetition(starts.Select(s => Run(s, 5)), Now);
        Check(r.Intervals, 3, 120, 120, 120 + 0.8 * (6 * 3600 - 120));
    }

    [Fact]
    public void Sessions_JoinRunsAtMost120SecondsApart_FromAnEndToTheNextStart()
    {
        var runs = new[]
        {
            // Session 1: three pulses, 40 s and exactly 120 s apart.
            Run(0, 15, wh: 6), Run(55, 15, wh: 6), Run(190, 15, wh: 6),
            // Session 2 (121 s after the end of the last pulse): one pulse.
            Run(326, 15, wh: 6),
            // Session 3: two pulses.
            Run(5000, 15, wh: 10), Run(5030, 15, wh: 2),
        };

        var r = SignatureProfiles.Repetition(runs, Now);

        r.Sessions.Should().Be(3);
        r.PulsesPerSessionP50.Should().Be(2, "1, 2, 3");
        r.EnergyPerSessionWhP50.Should().Be(12, "6, 12, 18");
    }

    [Fact]
    public void Sessions_ARunInsideALongerOne_DoesNotCutTheSession()
    {
        var r = SignatureProfiles.Repetition(new[] { Run(0, 600), Run(10, 5), Run(650, 5) }, Now);
        r.Sessions.Should().Be(1, "the third starts 50 s after the end of the long one, not of the short one");
    }

    [Fact]
    public void TheSheet_CarriesIt()
    {
        var p = SignatureProfiles.Of(1, Array.Empty<MeterEvent>(), new[] { Run(0, 10), Run(100, 20) }, Array.Empty<Guid>(), Now, TimeZoneInfo.Utc);
        p.Repetition.Should().NotBeNull();
        p.Repetition!.Durations.Count.Should().Be(2);
        p.Repetition.Sessions.Should().Be(1);
    }
}
