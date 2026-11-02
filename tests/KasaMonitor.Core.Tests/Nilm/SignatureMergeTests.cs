using FluentAssertions;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

/// <summary>Rule 1 of 0.8.2: a transient is unknown (never compared) only when the step took ≥ 1 s and the ratio is ≤ 1.5.</summary>
public class TransientRuleTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    private static MeterEvent E(long id, double p, double q, double ratio, double rise) => new(id, Guid.Empty, T.AddMinutes(id), p, q, 0, ratio, rise, 500);

    [Fact]
    public void Transient_IsKnownWhenTheStepTookNoTime_OrThePeakIsAnInrush()
    {
        E(1, 1990, 118, 1.0, 0).TransientKnown.Should().BeTrue();
        E(2, 1990, 118, 1.0, 1).TransientKnown.Should().BeFalse("the step spread over two samples");
        E(3, 1990, 118, 0.026, 5).TransientKnown.Should().BeFalse("a wobble started the transition: the peak was not seen");
        E(5, 120, 60, 4.9, 2).TransientKnown.Should().BeTrue("a motor's inrush: partial sampling can only lower a peak, 4.9 is real");
        E(6, 120, 60, 1.5, 2).TransientKnown.Should().BeFalse("1.5 or less with a rise: may be a missed peak");
        E(4, -1990, -118, 1.0, 0).TransientKnown.Should().BeFalse("a switch-off has no inrush");
        double.IsNaN(EventFeatures.Of(E(2, 1990, 118, 1.0, 1)).Transient).Should().BeTrue();
        EventFeatures.Of(E(1, 1990, 118, 1.0, 0)).Transient.Should().Be(1.0);
        E(2, 1990, 118, 0.34, 2).TransientRatio.Should().Be(0.34, "the stored value stays as measured");
    }

    [Fact]
    public void AnUnknownTransient_IsNotCompared_AndDoesNotEnterTheMean()
    {
        var seen = EventFeatures.Of(E(1, 1992, 118, 1.0, 0));
        var between = EventFeatures.Of(E(2, 1967, 117, 0.0, 2));
        SignatureClusterer.Distance(seen, between).Should().BeLessThan(1, "only |ΔP| and ΔQ are compared");
        SignatureClusterer.Distance(new EventFeatures(1, 1992, 118, 1.0), new EventFeatures(2, 1967, 117, 0.0)).Should().BeGreaterThan(1,
            "with the raw ratios 1 and 0 the two would be far apart");
        var (_, _, t) = SignatureClusterer.Means(new[] { seen, between });
        t.Should().Be(1.0);
        SignatureClusterer.Means(new[] { between }).MeanTransient.Should().Be(0, "none known");
        new SignatureCentre(1, 1990, 120, 0, false).HasTransient.Should().BeFalse();
    }

    /// <summary>
    /// The washing machine's heater on 2 Nov (real events of signatures #53 and #55): 3 of 6 switch-ons fell between two
    /// samples (ratio ≈ 0, rise 2 s). With the transient unknown they form one cluster.
    /// </summary>
    [Fact]
    public void WashingMachineHeater_TwelveRealEvents_OneCluster()
    {
        var events = new[]
        {
            E(415, 2008, 122, 1.001, 0), E(416, -2002, -134, 0, 2), E(418, 1967, 117, 0, 2), E(428, -1994, -108, 0.999, 0),
            E(431, 1978, 99, 1.018, 0), E(433, -1929, -156, 1.008, 1), E(438, 1963, 115, 1.013, 0), E(445, -1945, -132, 1.038, 11),
            E(461, 2007, 175, 0.008, 2), E(464, -1958, -110, 1.033, 6), E(469, 1993, 126, 0.985, 1), E(471, -1991, -101, 1.007, 0),
        };
        var labels = SignatureClusterer.Cluster(events.Select(EventFeatures.Of).ToList());
        labels.Should().OnlyContain(l => l == labels[0]);
        labels[0].Should().BeGreaterThanOrEqualTo(0);
    }
}

/// <summary>Rule 2 of 0.8.2: labelled signatures (user, plug) first, with 1.5 × eps.</summary>
public class AnchorPriorityTests
{
    [Fact]
    public void TheKettlesStartsOf2Nov_GoToTheLabelledKettle_NotToTheNearerAnonymousGroups()
    {
        var kettle = new SignatureCentre(28, 1520, 105, 1.0, true);           // „Fierbător”, labelled by the user
        var others = new[] { new SignatureCentre(59, 1368, 73, 1.0, true), new SignatureCentre(88, 1422, 104, 0, false) };
        // As measured: 639 and 651 fell between two samples (ratio 0.026 / 0.012, rise 5 s / 2 s).
        var raw = new[] { new EventFeatures(639, 1411, 113, 0.026), new EventFeatures(644, 1425, 63, 0.999), new EventFeatures(651, 1439, 87, 0.012) };
        var starts = new[] { raw[0] with { Transient = double.NaN }, raw[1], raw[2] with { Transient = double.NaN } };
        var all = others.Append(kettle).ToList();
        foreach (var e in raw)
            SignatureClusterer.Nearest(e, all).Should().NotBe(28, "0.8.1: ratio ≈ 0 against 1.0, or a nearer anonymous group");
        var anchors = new[] { new AnchorCentre(kettle, null) };
        foreach (var e in starts)
            SignatureClusterer.Assign(e, null, anchors, others).Should().Be(28);
        SignatureClusterer.Nearest(starts[1], all).Should().Be(88, "the anonymous group is nearer, the labelled one still wins");
        SignatureClusterer.Assign(new EventFeatures(1, 1000, 100, 1), null, anchors, others).Should().BeNull();
        SignatureClusterer.Assign(new EventFeatures(2, 1370, 70, 1), null, Array.Empty<AnchorCentre>(), others).Should().Be(59, "no anchor: nearest anonymous");
    }

    [Fact]
    public void AnEventLabelledByAPlug_GoesToTheAnchorOfThatPlug_NeverToAnAnchorOfAnotherPlug()
    {
        var x = Guid.NewGuid();
        var y = Guid.NewGuid();
        var lampX = new AnchorCentre(new SignatureCentre(1, 40, 0, 0, false), x);
        var lampY = new AnchorCentre(new SignatureCentre(2, 44, 0, 0, false), y);
        var lamp = new AnchorCentre(new SignatureCentre(3, 40, 0, 0, false), null);
        var e = new EventFeatures(9, 40, 0, double.NaN);
        SignatureClusterer.NearestAnchor(e, null, new[] { lampX, lampY }).Should().Be(1, "no plug label: the nearest");
        SignatureClusterer.NearestAnchor(e, y, new[] { lampX, lampY }).Should().Be(2, "labelled by Y: Y's anchor first, though farther");
        SignatureClusterer.NearestAnchor(e, y, new[] { lampX }).Should().BeNull("never to the anchor of another plug");
        SignatureClusterer.NearestAnchor(e, y, new[] { lampX, lamp }).Should().Be(3, "an anchor without a plug is fine");
        SignatureClusterer.Assign(e, y, new[] { lampX }, new[] { new SignatureCentre(5, 41, 0, 0, false) }).Should().Be(5);
    }

    [Fact]
    public void Anchor_IsALabelOfTheUserOrALinkToAPlug()
    {
        ApplianceSignature S(SignatureLabelSource src, Guid? plug) => new(1, "x", "", "other", 100, 0, 0, 3, null, src, plug, false);
        S(SignatureLabelSource.User, null).IsAnchor.Should().BeTrue();
        S(SignatureLabelSource.Cluster, Guid.NewGuid()).IsAnchor.Should().BeTrue();
        S(SignatureLabelSource.Plug, Guid.NewGuid()).IsAnchor.Should().BeTrue();
        S(SignatureLabelSource.Cluster, null).IsAnchor.Should().BeFalse();
        (S(SignatureLabelSource.User, null) with { Hidden = true }).IsAnchor.Should().BeFalse("a hidden signature attracts nothing");
        (S(SignatureLabelSource.User, null) with { Hidden = true }).IsLabelled.Should().BeTrue();
    }
}

/// <summary>Rule 4 of 0.8.2: the „fișa” of a signature.</summary>
public class SignatureProfileTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Hol = Guid.NewGuid();
    private static readonly Guid Ups = Guid.NewGuid();

    [Fact]
    public void Profile_FromEventsAndRuns()
    {
        var start = new DateTimeOffset(Now.Date, TimeSpan.Zero);
        MeterEvent E(int id, int hour, double p, double q, double ratio, double rise, Guid? plug = null) =>
            new(id, Guid.Empty, start.AddHours(hour).AddMinutes(id), p, q, 0, ratio, rise, 500, 7,
                plug is null ? null : "Hol mic", plug is null ? EventLabelSource.None : EventLabelSource.AutoPlug, plug);
        var events = new[]
        {
            E(1, 8, 2000, 120, 1.0, 0, Hol), E(2, 8, -1980, -110, 1.0, 0, Hol), E(3, 9, 1990, 130, 0.0, 2),
            E(4, 9, -2010, -120, 1.0, 1), E(5, 9, 2020, 118, 1.2, 0, Ups),
        };
        var runs = new[]
        {
            new ApplianceRun(1, 7, start.AddHours(8), start.AddHours(8).AddMinutes(10), 2000, 333, 1, 2),
            new ApplianceRun(2, 7, start.AddHours(9), start.AddHours(9).AddMinutes(20), 2000, 666, 3, 4),
            new ApplianceRun(3, 7, start.AddHours(10), start.AddHours(16), 2000, 0, 5, null, 0, RunEndReason.Timeout),
        };
        var p = SignatureProfiles.Of(7, events, runs, Array.Empty<Guid>(), Now, TimeZoneInfo.Utc);

        p.Events.Should().Be(5);
        p.OnEvents.Should().Be(3);
        p.OffEvents.Should().Be(2);
        p.MeanDeltaP.Should().Be(2000);
        p.StdDeltaP.Should().BeApproximately(Math.Sqrt((0 + 400 + 100 + 100 + 400) / 5.0), 1e-9);
        p.MeanDeltaQ.Should().BeApproximately(119.6, 1e-9);
        p.StepPowerFactor.Should().BeApproximately(2000 / Math.Sqrt(2000 * 2000 + 119.6 * 119.6), 1e-9);
        p.TransientKnown.Should().Be(2, "the switch-on at 9:03 fell between two samples");
        p.MeanTransient.Should().BeApproximately(1.1, 1e-9);
        p.Runs.Should().Be(2, "the uncertain run (timeout) has no seen end");
        p.MedianRunSeconds.Should().Be(900);
        p.P10RunSeconds.Should().Be(660);
        p.P90RunSeconds.Should().Be(1140);
        p.Hours[8].Should().Be(2);
        p.Hours[9].Should().Be(3);
        p.Hours.Sum().Should().Be(5);
        p.TopPlugId.Should().Be(Hol);
        p.TopPlugEvents.Should().Be(2);
        p.TopPlugShare.Should().Be(0.4);
        p.FirstSeenUtc.Should().Be(events[0].TsUtc);
        p.LastSeenUtc.Should().Be(events[4].TsUtc);
        p.Last7DaysWh.Should().Be(999);
        p.Centre.HasTransient.Should().BeTrue();

        SignatureProfiles.Of(7, events, runs, new[] { Hol }, Now, TimeZoneInfo.Utc).TopPlugId.Should().Be(Ups, "an excluded plug does not count");
        SignatureProfiles.Build(events, runs, Array.Empty<Guid>(), Now, TimeZoneInfo.Utc).Should().ContainKey(7).WhoseValue.Events.Should().Be(5);
        SignatureProfiles.Percentile(Array.Empty<double>(), 0.5).Should().BeNull();
    }
}

/// <summary>Rule 3 of 0.8.2: the automatic merge.</summary>
public class SignatureMergerTests
{
    private static readonly Guid Hol = Guid.NewGuid();
    private static readonly Guid Kitchen = Guid.NewGuid();

    private static ApplianceSignature Sig(long id, double p, double q, double t, int count, string? name = null,
        SignatureLabelSource src = SignatureLabelSource.Cluster, Guid? plug = null, bool hidden = false) =>
        new(id, name ?? SignatureNaming.Unknown(id), "", "other", p, q, t, count, null, src, plug, hidden);

    private static SignatureProfile Profile(long id, double p, double q, double? t, int events, Guid? topPlug = null, int known = 1) =>
        new(id, events, events / 2, events - events / 2, p, 0, q, t, t is null ? 0 : known, 0, null, null, null, new int[24],
            topPlug, null, topPlug is null ? 0 : 3, null, null, 0);

    private static MergeCandidate C(ApplianceSignature s, SignatureProfile? p = null) => new(s, p);

    [Fact]
    public void WashingMachine_53And55_PossiblyTheSamePlug_AreMerged_TheOneWithMoreEventsSurvives()
    {
        var s53 = Sig(53, 1992, 118, 1.0, 6);
        var s55 = Sig(55, 1964, 132, 0.34, 7);
        // Their centres are close once the unknown transients are left out; „posibil Hol mic” for both as well.
        var steps = SignatureMerger.Plan(new[] { C(s53, Profile(53, 1992, 118, 1.0, 6, Hol)), C(s55, Profile(55, 1964, 132, 1.02, 7, Hol)) });
        steps.Should().ContainSingle().Which.Should().Be(new MergeStep(55, 53, MergeReason.Centres));

        // Rule b alone: same plug, |ΔP| within 15 %, centres apart (ΔQ).
        var b = SignatureMerger.Plan(new[] { C(s53, Profile(53, 1992, 118, null, 6, Hol)), C(s55, Profile(55, 1800, 400, null, 7, Hol)) });
        b.Should().ContainSingle().Which.Reason.Should().Be(MergeReason.SamePlug);
        SignatureMerger.Plan(new[] { C(s53, Profile(53, 1992, 118, null, 6, Hol)), C(s55, Profile(55, 1600, 400, null, 7, Hol)) })
            .Should().BeEmpty("20 % apart");
        SignatureMerger.Plan(new[] { C(s53, Profile(53, 1992, 118, null, 6, Hol)), C(s55, Profile(55, 1800, 400, null, 7, Kitchen)) })
            .Should().BeEmpty("„posibil” different plugs");
    }

    [Fact]
    public void SameUserName_IsMergedWhenTheCentresAreClose_DifferentUserNamesOrDifferentLinkedPlugs_Never()
    {
        var a = Sig(1, 1500, 100, 1, 2, "Fierbător", SignatureLabelSource.User);
        var b = Sig(2, 1380, 90, 1, 9, " fierbător ", SignatureLabelSource.User);
        SignatureMerger.Plan(new[] { C(a), C(b) }).Should().ContainSingle().Which.Should().Be(new MergeStep(2, 1, MergeReason.SameName));
        var motor = Sig(8, 280, 230, 0, 9, "Mașina de spălat", SignatureLabelSource.User);
        var heater = Sig(9, 1960, 120, 1, 9, "Mașina de spălat", SignatureLabelSource.User);
        SignatureMerger.Plan(new[] { C(motor), C(heater) }).Should().BeEmpty("the same name on two distant components: two rows");
        SignatureMerger.Plan(new[] { C(a with { PlugId = Hol }), C(b with { PlugId = Kitchen }) }).Should().BeEmpty("linked to different plugs, even with one name");

        var kettle = Sig(3, 1500, 100, 1, 2, "Fierbător", SignatureLabelSource.User);
        var teapot = Sig(4, 1500, 100, 1, 9, "Ceainic", SignatureLabelSource.User);
        SignatureMerger.Plan(new[] { C(kettle), C(teapot) }).Should().BeEmpty("a user label is never lost");

        var lampA = Sig(5, 40, 0, 1, 9, plug: Hol);
        var lampB = Sig(6, 40, 0, 1, 9, plug: Kitchen);
        SignatureMerger.Plan(new[] { C(lampA), C(lampB) }).Should().BeEmpty("measured by two different plugs");
        SignatureMerger.Plan(new[] { C(lampA), C(Sig(7, 40, 0, 1, 3, hidden: true)) }).Should().BeEmpty("hidden ones stay as they are");
    }

    [Fact]
    public void Survivor_UserLabelThenPlugThenEvents()
    {
        var user = Sig(1, 100, 0, 1, 1, "Lampă", SignatureLabelSource.User);
        var plug = Sig(2, 100, 0, 1, 5, plug: Hol);
        var big = Sig(3, 100, 0, 1, 50);
        var small = Sig(4, 100, 0, 1, 5);
        SignatureMerger.Survivor(user, plug).Id.Should().Be(1);
        SignatureMerger.Survivor(big, plug).Id.Should().Be(2);
        SignatureMerger.Survivor(small, big).Id.Should().Be(3);
        SignatureMerger.Survivor(Sig(9, 100, 0, 1, 5), small).Id.Should().Be(4, "equal: the older id");

        var steps = SignatureMerger.Plan(new[] { C(big), C(user), C(small) });
        steps.Should().HaveCount(2);
        steps.Should().OnlyContain(s => s.SurvivorId == 1, "everything ends in the user's signature");
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Event times: every <paramref name="everyMinutes"/> from <paramref name="fromMinute"/>, <paramref name="count"/> of them.</summary>
    private static List<DateTimeOffset> Times(int fromMinute, int count, int everyMinutes = 2) =>
        Enumerable.Range(0, count).Select(i => T0.AddMinutes(fromMinute + i * everyMinutes)).ToList();

    [Fact]
    public void RuleD_DrumMotorPulses_SameQOverP_Within25Percent_TogetherInTime_AreMerged()
    {
        // The drum motor of 2 Nov: 215 / 267 / 304 W, ΔQ/ΔP ≈ 0.80–0.83, pulses during the same washing cycle.
        var steps = SignatureMerger.Plan(new[]
        {
            SignatureCandidate(65, 267, 214, Times(0, 36))!,
            SignatureCandidate(68, 304, 250, Times(1, 30))!,
            SignatureCandidate(72, 215, 175, Times(3, 9, 5))!,
        });
        steps.Should().HaveCount(2);
        steps.Should().OnlyContain(s => s.Reason == MergeReason.VariableLoad && s.SurvivorId == 65);

        // Not together in time (another day): not merged.
        SignatureMerger.Plan(new[] { SignatureCandidate(65, 267, 214, Times(0, 36))!, SignatureCandidate(68, 304, 250, Times(24 * 60, 30))! })
            .Should().BeEmpty();
        // Another ΔQ/ΔP (0.80 against 0.30), or the opposite sign, or 30 % apart: not merged.
        SignatureMerger.Plan(new[] { SignatureCandidate(65, 267, 214, Times(0, 36))!, SignatureCandidate(68, 304, 91, Times(1, 30))! }).Should().BeEmpty();
        SignatureMerger.Plan(new[] { SignatureCandidate(65, 267, 214, Times(0, 36))!, SignatureCandidate(68, 304, -250, Times(1, 30))! }).Should().BeEmpty();
        SignatureMerger.Plan(new[] { SignatureCandidate(65, 267, 214, Times(0, 36))!, SignatureCandidate(68, 380, 304, Times(1, 30))! }).Should().BeEmpty();
        // Linked to different plugs: never.
        SignatureMerger.Plan(new[]
        {
            SignatureCandidate(65, 267, 214, Times(0, 36), Guid.NewGuid())!, SignatureCandidate(68, 304, 250, Times(1, 30), Guid.NewGuid())!,
        }).Should().BeEmpty();
    }

    [Fact]
    public void RuleD_NeverForAnAnchor_ResistiveOrSmallLoads_ChanceCoOccurrence_OrPossiblyDifferentPlugs()
    {
        var motor = SignatureCandidate(65, 267, 214, Times(0, 36))!;
        var other = SignatureCandidate(68, 304, 250, Times(1, 30))!;
        SignatureMerger.Plan(new[] { motor, other with { Signature = other.Signature with { Name = "Motor", LabelSource = SignatureLabelSource.User } } })
            .Should().BeEmpty("an anchor (the user's) is never merged by rule d");
        // The washer's heater and the kettle (ΔQ/ΔP ≈ 0.07, resistive), together in time: not rule d.
        SignatureMerger.Plan(new[] { SignatureCandidate(53, 1963, 137, Times(0, 13))!, SignatureCandidate(59, 1600, 110, Times(1, 10))! }).Should().BeEmpty();
        // Small loads (≈ 100 W, ΔQ/ΔP 0.5): below 150 W.
        SignatureMerger.Plan(new[] { SignatureCandidate(8, 117, 60, Times(0, 36))!, SignatureCandidate(11, 100, 52, Times(1, 30))! }).Should().BeEmpty();
        // A dense load all day long: together with everything, but no more than by chance (lift ≈ 1).
        var dense = SignatureCandidate(65, 267, 214, Times(0, 720))!;
        var sparse = SignatureCandidate(68, 304, 250, Times(300, 30, 20))!;
        var (share, lift) = SignatureMerger.CoOccurrenceLift(dense.Profile!.EventTimes!, sparse.Profile!.EventTimes!);
        share.Should().Be(1);
        lift.Should().BeLessThan(SignatureMerger.MinLift);
        SignatureMerger.Plan(new[] { dense, sparse }).Should().BeEmpty();
        SignatureMerger.CoOccurrenceLift(motor.Profile!.EventTimes!, other.Profile!.EventTimes!).Lift.Should().BeGreaterThanOrEqualTo(SignatureMerger.MinLift);
        // „posibil X” against „posibil Y” (each with ≥ 2 events and ≥ 30 %): not rule d.
        SignatureMerger.Plan(new[]
        {
            motor with { Profile = motor.Profile with { PlugEvents = new Dictionary<Guid, int> { [Hol] = 12 } } },
            other with { Profile = other.Profile with { PlugEvents = new Dictionary<Guid, int> { [Kitchen] = 10 } } },
        }).Should().BeEmpty();
    }

    [Fact]
    public void RuleB_NeedsAClearPossiblePlug_AndAMergeNeverUnlinksTheSurvivor()
    {
        var a = C(Sig(1, 1000, 0, 0, 10), Profile(1, 1000, 0, null, 10) with { PlugEvents = new Dictionary<Guid, int> { [Hol] = 1 } });
        var b = C(Sig(2, 1100, 200, 0, 10), Profile(2, 1100, 200, null, 10) with { PlugEvents = new Dictionary<Guid, int> { [Hol] = 1 } });
        SignatureMerger.Plan(new[] { a, b }).Should().BeEmpty("one event from the plug each: not a clear „posibil”");
        var a3 = a with { Profile = a.Profile! with { PlugEvents = new Dictionary<Guid, int> { [Hol] = 3 } } };
        var b3 = b with { Profile = b.Profile! with { PlugEvents = new Dictionary<Guid, int> { [Hol] = 3 } } };
        SignatureMerger.Plan(new[] { a3, b3 }).Should().ContainSingle().Which.Reason.Should().Be(MergeReason.SamePlug);

        // Linked to Hol with 8 of 10 events; an anonymous one of 30 events right next to it: 8/40 < 60 %, not merged.
        var linked = C(Sig(3, 500, 0, 0, 10, plug: Hol), Profile(3, 500, 0, null, 10) with { PlugEvents = new Dictionary<Guid, int> { [Hol] = 8 } });
        var big = C(Sig(4, 505, 0, 0, 30), Profile(4, 505, 0, null, 30));
        SignatureMerger.Plan(new[] { linked, big }).Should().BeEmpty();
        var small = C(Sig(5, 505, 0, 0, 2), Profile(5, 505, 0, null, 2));
        SignatureMerger.Plan(new[] { linked, small }).Should().ContainSingle().Which.SurvivorId.Should().Be(3, "8/12 ≥ 60 %");
    }

    [Fact]
    public void CoOccurrence_ShareOfTheSmallerListWithAnEventOfTheOtherWithinTenMinutes()
    {
        var a = Times(0, 10, 1);                                 // 08:00 … 08:09
        var b = new List<DateTimeOffset> { T0.AddMinutes(15), T0.AddMinutes(25), T0.AddMinutes(40), T0.AddMinutes(60) };
        SignatureMerger.CoOccurrence(a, b).Should().Be(0.25, "of the 4 (the smaller list) only 08:15 has events within 10 min (08:05–08:09)");
        SignatureMerger.CoOccurrence(b, a).Should().Be(0.25);
        SignatureMerger.CoOccurrence(a, a).Should().Be(1);
        SignatureMerger.CoOccurrence(a, Array.Empty<DateTimeOffset>()).Should().Be(0);
    }

    private static MergeCandidate? SignatureCandidate(long id, double p, double q, List<DateTimeOffset> times, Guid? plug = null) =>
        new(Sig(id, p, q, 0, times.Count, plug: plug), Profile(id, p, q, null, times.Count) with { EventTimes = times });

    [Fact]
    public void MotorPulses_AGroupWithinEps_MergesStepByStep_AFarOneStays()
    {
        var steps = SignatureMerger.Plan(new[]
        {
            C(Sig(65, 272, 215, 0, 13), Profile(65, 272, 215, null, 13)),
            C(Sig(69, 275, 244, 0, 14), Profile(69, 275, 244, null, 14)),
            C(Sig(70, 258, 251, 0, 5), Profile(70, 258, 251, null, 5)),
            C(Sig(28, 1520, 105, 1, 2, "Fierbător", SignatureLabelSource.User), Profile(28, 1520, 105, 1, 2)),
        });
        steps.Select(s => s.AbsorbedId).Should().BeEquivalentTo(new long[] { 65, 70 });
        steps.Should().OnlyContain(s => s.SurvivorId == 69);
        SignatureMerger.Reason(C(Sig(65, 272, 215, 0, 13)), C(Sig(69, 275, 244, 0, 14))).Should().Be(MergeReason.Centres);
        SignatureMerger.Reason(C(Sig(65, 272, 215, 0, 13)), C(Sig(72, 219, 216, 0, 4))).Should().BeNull("20 % apart");
    }
}
