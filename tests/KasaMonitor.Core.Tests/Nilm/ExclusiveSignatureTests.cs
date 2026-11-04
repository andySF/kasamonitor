using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

/// <summary>
/// 0.13.1: a signature of a plug with a single appliance holds only what that plug confirmed or the user labelled. Measured
/// on a fridge of ≈ 60 W: 86 real transitions in 24 h, 1,969 events in its signature, a mean step of 39 W.
/// </summary>
public class ExclusiveSignatureTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Fridge = Guid.Parse("00000000-0000-0000-0000-0000000f0001");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000f0002");
    private static readonly IReadOnlySet<Guid> Exclusive = new HashSet<Guid> { Fridge };

    private static ApplianceSignature Sig(long id, double p = 60, double q = 0, Guid? plug = null, string? name = null, int count = 10,
        SignatureLabelSource source = SignatureLabelSource.Plug, int? min = null, int? max = null, long? splitFrom = null, bool hidden = false) =>
        new(id, name ?? $"S{id}", "i", "other", p, q, 0, count, T0, source, plug, hidden, min, max, splitFrom);

    private static MeterEvent E(double p = 60, long? sig = 1, EventLabelSource source = EventLabelSource.None, Guid? plug = null, string? label = null) =>
        new(1, Guid.Empty, T0, p, 0, 0, 1, 0, 300, sig, label, source, plug);

    private static MeterEvent Confirmed(double p = 60, long? sig = 1) => E(p, sig, EventLabelSource.AutoPlug, Fridge, "Frigider");

    private static AnchorCentre Anchor(long id, double p, Guid? plug, bool exclusive) =>
        new(new SignatureCentre(id, p, 0, 0, false), plug, exclusive);

    [Fact]
    public void AnUnlabelledEventNearTheCentre_StaysOutOfTheExclusiveAnchor_ButEntersAPlainOne()
    {
        var e = new EventFeatures(9, 60, 0, double.NaN);
        SignatureClusterer.NearestAnchor(e, null, new[] { Anchor(1, 60, Fridge, exclusive: true) }).Should().BeNull();
        SignatureClusterer.NearestAnchor(e, null, new[] { Anchor(1, 60, Fridge, exclusive: false) }).Should().Be(1, "a plug with several consumers keeps the old rule");
        SignatureClusterer.Assign(e, null, new[] { Anchor(1, 60, Fridge, exclusive: true) }, new[] { new SignatureCentre(5, 62, 0, 0, false) })
            .Should().Be(5, "it goes on to the anonymous signatures, as usual");
        SignatureClusterer.NearestAnchor(e, null, new[] { Anchor(1, 60, Fridge, exclusive: true), Anchor(2, 66, null, exclusive: false) })
            .Should().Be(2, "another anchor within its radius, though farther");
    }

    [Fact]
    public void AConfirmedEvent_EntersItsOwnExclusiveAnchor_AnotherPlugsDoesNot()
    {
        var e = new EventFeatures(9, 60, 0, double.NaN);
        var anchors = new[] { Anchor(1, 60, Fridge, exclusive: true) };
        SignatureClusterer.NearestAnchor(e, Fridge, anchors).Should().Be(1);
        SignatureClusterer.NearestAnchor(e, Other, anchors).Should().BeNull();
        SignatureClusterer.Assign(e, Other, anchors, Array.Empty<SignatureCentre>()).Should().BeNull();
    }

    [Fact]
    public void AnAnchor_IsExclusiveOnlyWhenItsPlugHasASingleAppliance()
    {
        Plug P(Guid id, bool single) => new(id, "aabbccddee00", IPAddress.Loopback, "Priza", false, "", "", "", "", T0, T0, false, null, SingleAppliance: single, ApplianceName: "Frigider");
        var plugs = ExclusiveSignatures.Plugs(new[] { P(Fridge, true), P(Other, false) });
        plugs.Should().BeEquivalentTo(new[] { Fridge });
        AnchorCentre.Of(Sig(1, plug: Fridge), plugs).Exclusive.Should().BeTrue();
        AnchorCentre.Of(Sig(1, plug: Other), plugs).Exclusive.Should().BeFalse();
        AnchorCentre.Of(Sig(1, source: SignatureLabelSource.User), plugs).Exclusive.Should().BeFalse("named by the user, no plug");
    }

    [Theory]
    [InlineData(EventLabelSource.AutoPlug, true, true)]
    [InlineData(EventLabelSource.AutoPlug, false, false)]
    [InlineData(EventLabelSource.User, false, true)]
    [InlineData(EventLabelSource.Model, false, false)]
    [InlineData(EventLabelSource.None, false, false)]
    [InlineData(EventLabelSource.None, true, false)]
    [InlineData(EventLabelSource.DurationRule, true, true)]
    [InlineData(EventLabelSource.DurationRule, false, true)]
    public void WhatBelongsInTheSignature(EventLabelSource source, bool byThePlug, bool belongs) =>
        ExclusiveSignatures.Belongs(E(source: source, plug: byThePlug ? Fridge : Other), Fridge).Should().Be(belongs);

    [Fact]
    public void ConfirmedByThePlug_IsItsLabel_OrTheDurationRuleThatKeptIt()
    {
        ExclusiveSignatures.ConfirmedBy(Confirmed(), Fridge).Should().BeTrue();
        ExclusiveSignatures.ConfirmedBy(E(source: EventLabelSource.DurationRule, plug: Fridge), Fridge).Should().BeTrue();
        ExclusiveSignatures.ConfirmedBy(E(source: EventLabelSource.DurationRule), Fridge).Should().BeFalse("only the user's split can leave such an event there");
        ExclusiveSignatures.ConfirmedBy(E(source: EventLabelSource.User), Fridge).Should().BeFalse();
        ExclusiveSignatures.ConfirmedBy(E(source: EventLabelSource.AutoPlug, plug: Other), Fridge).Should().BeFalse();
    }

    [Fact]
    public void AModelPrediction_NeverAssignsToAnExclusiveSignature()
    {
        var all = new[]
        {
            Sig(1, plug: Fridge, name: "Frigider"),
            Sig(2, plug: Other, name: "Fierbător"),
            Sig(3, name: "Pompă", source: SignatureLabelSource.Cluster, count: 50),
            Sig(4, name: "Pompă", source: SignatureLabelSource.User, count: 2),
        };
        ExclusiveSignatures.ModelTarget("Frigider", all, Exclusive).Should().Be(((long?)null, false), "as if not labelled");
        ExclusiveSignatures.ModelTarget("Priza UPS", all, Exclusive).Should().Be(((long?)null, true), "no signature: it keeps the label and goes through the anchors");
        ExclusiveSignatures.ModelTarget("Fierbător", all, Exclusive).Should().Be(((long?)2, true), "a plug with several consumers");
        ExclusiveSignatures.ModelTarget("Pompă", all, Exclusive).Should().Be(((long?)4, true), "as before: the user's, then the larger");
        ExclusiveSignatures.ModelTarget("Frigider", all.Append(Sig(5, name: "Frigider", source: SignatureLabelSource.User)), Exclusive)
            .Should().Be(((long?)5, true), "another signature with that name, not the plug's");
    }

    [Fact]
    public void WaitingInTheSideThatWasSplit_DoesNotOpenAnExclusiveSignature()
    {
        var parent = Sig(1, 1430, plug: Fridge, min: 40);
        var child = Sig(2, 1430, max: 40, splitFrom: 1, source: SignatureLabelSource.User);
        var all = new[] { parent, child };
        ExclusiveSignatures.Canonical(2, E(1430, null), all, Exclusive).Should().Be(2, "not confirmed: it stays where the anchors put it");
        ExclusiveSignatures.Canonical(2, Confirmed(1430, null), all, Exclusive).Should().Be(1);
        ExclusiveSignatures.Canonical(2, E(1430, null), all, new HashSet<Guid>()).Should().Be(1, "a plain plug: as in 0.13.0");
        ExclusiveSignatures.Canonical(1, E(1430, null), all, Exclusive).Should().Be(1);
    }

    [Fact]
    public void TheDurationRule_MovesARunIntoAnExclusiveSignature_OnlyWhenThePlugConfirmedBothEvents()
    {
        var source = Sig(1, 1430, plug: Fridge, min: 40);
        var target = Sig(2, 1430, plug: Fridge, max: 40, splitFrom: 1);
        var run = new ApplianceRun(7, 1, T0, T0.AddSeconds(15), 1430, 6, 1, 2, 300, RunEndReason.Paired);
        var all = new[] { source, target };

        DurationBands.Target(run, Confirmed(1430), Confirmed(-1430), source, all, Exclusive).Should().Be(target);
        DurationBands.Target(run, E(1430), E(-1430), source, all, Exclusive).Should().BeNull();
        DurationBands.Target(run, Confirmed(1430), E(-1430), source, all, Exclusive).Should().BeNull("one event is not the run");
        DurationBands.Target(run, E(1430), Confirmed(-1430), source, all, Exclusive).Should().BeNull();
        DurationBands.Target(run, E(1430), E(-1430), source, all).Should().Be(target, "a plain plug: as in 0.13.0");
        var free = Sig(3, 1430, max: 40, source: SignatureLabelSource.User);
        DurationBands.Target(run, E(1430), E(-1430), source, new[] { source, target, free }, Exclusive).Should().Be(free, "the exclusive one does not count among the candidates");
    }

    private static MergeCandidate C(ApplianceSignature s, int plugEvents = 0, int events = 10) =>
        new(s, new SignatureProfile(s.Id, events, events / 2, events / 2, s.MeanDeltaP, 1, s.MeanDeltaQ, null, 0, events / 2, null, null, null, new int[24],
            plugEvents > 0 ? s.PlugId ?? Fridge : null, null, plugEvents, T0, T0, 0, null,
            plugEvents > 0 ? new Dictionary<Guid, int> { [s.PlugId ?? Fridge] = plugEvents } : null));

    [Fact]
    public void TheAutomaticMerge_LeavesAnExclusiveSignatureAlone_ExceptWithAnotherOfItsPlugWithConfirmedEvents()
    {
        var fridge = C(Sig(1, plug: Fridge, name: "Frigider"), plugEvents: 90, events: 90);   // large enough for the 60 % of the plug rule
        var anonymous = C(Sig(2, source: SignatureLabelSource.Cluster));
        var named = C(Sig(3, name: "Frigider", source: SignatureLabelSource.User));
        var possible = C(Sig(4, source: SignatureLabelSource.Cluster), plugEvents: 8);
        var own = C(Sig(5, plug: Fridge, name: "Frigider"), plugEvents: 10, events: 10);
        var ownEmpty = C(Sig(6, plug: Fridge, name: "Frigider", count: 0), events: 0);

        SignatureMerger.Reason(fridge, anonymous).Should().Be(MergeReason.Centres, "without the rule, the same centre merges");
        SignatureMerger.Reason(fridge, anonymous, Exclusive).Should().BeNull("rule a");
        SignatureMerger.Reason(anonymous, fridge, Exclusive).Should().BeNull("either way round");
        SignatureMerger.Reason(fridge, named, Exclusive).Should().BeNull("the same name is not the same plug");
        SignatureMerger.Reason(fridge, possible, Exclusive).Should().BeNull("„posibil” the same plug still holds what the plug did not confirm");
        SignatureMerger.Reason(fridge, own, Exclusive).Should().Be(MergeReason.Centres);
        SignatureMerger.Reason(fridge, ownEmpty, Exclusive).Should().BeNull("nothing confirmed in it: its centre says nothing");
        SignatureMerger.Plan(new[] { fridge, anonymous, named }, Exclusive).Select(s => s.SurvivorId).Should().NotContain(1);
        SignatureMerger.Plan(new[] { fridge, anonymous, named }, Exclusive).Select(s => s.AbsorbedId).Should().NotContain(1);
        SignatureMerger.Plan(new[] { fridge, own }, Exclusive).Should().ContainSingle();
    }

    [Fact]
    public void TheCentreOfWhatThePlugConfirmed_LeavesOutTheRest()
    {
        var events = new[] { Confirmed(60), Confirmed(-62), E(37), E(-37), E(38, source: EventLabelSource.Model, label: "Priza UPS"), Confirmed(500, sig: 2) };
        ExclusiveSignatures.ConfirmedCentre(1, Fridge, events)!.Value.MeanDeltaP.Should().Be(61);
        ExclusiveSignatures.ConfirmedCentre(1, Other, events).Should().BeNull();
        ExclusiveSignatures.ConfirmedCentre(1, Other, events.Append(E(50, source: EventLabelSource.User))).Should().NotBeNull("a label of the user counts");
    }

    [Fact]
    public void AConfirmation_FindsTheSignatureBehindADriftedCentre_OrAnEmptyOne_NeverANewOneForThatAlone()
    {
        var none = new HashSet<long>();
        var at60 = new EventFeatures(9, 60, 0, double.NaN);
        var drifted = Sig(1, 39, plug: Fridge);
        var history = new[] { Confirmed(60), Confirmed(-60), E(37), E(-37), E(37), E(-37) };

        ExclusiveSignatures.PlugSignature(at60, Fridge, new[] { drifted }, history, none).Should().Be((1L, false), "its confirmed events are at 60 W");
        ExclusiveSignatures.PlugSignature(at60, Fridge, new[] { drifted }, new[] { E(37), E(-37) }, none).Should().Be((1L, true), "nothing confirmed: it takes this one as its first");
        ExclusiveSignatures.PlugSignature(at60, Fridge, new[] { Sig(1, 39, plug: Fridge, count: 0) }, Array.Empty<MeterEvent>(), none).Should().Be((1L, true), "emptied");
        ExclusiveSignatures.PlugSignature(at60, Fridge, new[] { drifted }, Array.Empty<MeterEvent>(), new HashSet<long> { 1 }).Should().BeNull("created for another component of this same batch");
    }

    [Fact]
    public void AnotherComponentOfThePlug_StillGetsItsOwnSignature()
    {
        var heater = Sig(1, 2000, plug: Fridge);
        var history = new[] { Confirmed(2000), Confirmed(-2000) };
        ExclusiveSignatures.PlugSignature(new EventFeatures(9, 250, 0, double.NaN), Fridge, new[] { heater }, history, new HashSet<long>()).Should().BeNull();

        var motor = Sig(2, 39, plug: Fridge);
        ExclusiveSignatures.PlugSignature(new EventFeatures(9, 250, 0, double.NaN), Fridge, new[] { heater, motor }, history, new HashSet<long>())
            .Should().Be((2L, true), "the empty one, not a third");
    }
}
