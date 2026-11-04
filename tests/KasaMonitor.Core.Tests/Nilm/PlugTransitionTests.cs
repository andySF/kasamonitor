using FluentAssertions;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

/// <summary>
/// 0.13.2: the runs of a plug with a single appliance come from its own power. Measured on 68 h of a fridge: 240 real
/// transitions, of +44 W / −57 W (medians); the main meter has an event every ≈ 11 s and a threshold of 30 W, so its events
/// explain a transition with one candidate in 31 % of the cases.
/// </summary>
public class PlugTransitionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Fridge = Guid.Parse("00000000-0000-0000-0000-0000000f0001");
    private static readonly Guid Other = Guid.Parse("00000000-0000-0000-0000-0000000f0002");

    /// <summary>A series on the 1-second grid, from T0: (seconds, watts), a null watts = no data.</summary>
    private static List<PlugPowerPoint> Series(params (int Seconds, double? Watts)[] parts)
    {
        var points = new List<PlugPowerPoint>();
        var t = 0;
        foreach (var (seconds, watts) in parts)
        {
            for (var k = 0; k < seconds; k++, t++)
                if (watts is { } w) points.Add(new PlugPowerPoint(T0.AddSeconds(t), w));
        }
        return points;
    }

    private static MeterEvent E(long id, int second, double p, EventLabelSource source = EventLabelSource.None, Guid? plug = null) =>
        new(id, Guid.Empty, T0.AddSeconds(second), p, 0, 0, 1, 0, 300, null, null, source, plug);

    private static (Guid, PlugTransition) On(int second, double step, Guid? plug = null) => (plug ?? Fridge, new PlugTransition(T0.AddSeconds(second), step > 0, step));

    [Fact]
    public void TheEmptySeries_HasNoTransitionAndNoRun()
    {
        var (transitions, runs) = PlugTransitions.Replay(Array.Empty<PlugPowerPoint>());
        transitions.Should().BeEmpty();
        runs.Should().BeEmpty();
    }

    [Theory]
    [InlineData(2, 19.9, false)]
    [InlineData(2, 20, true)]
    [InlineData(2, 60, true)]
    [InlineData(25, 60, false)]   // already on: a higher level is not a change of state
    public void TheState_IsOnFrom20W(double before, double after, bool changes)
    {
        var transitions = PlugTransitions.Detect(Series((60, before), (60, after)));

        if (changes) transitions.Should().ContainSingle().Which.Should().Be(new PlugTransition(T0.AddSeconds(60), true, after - before));
        else transitions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 2)]
    [InlineData(30, 2)]
    public void APulse_CountsOnlyWhenItStillHolds5SAfterItsFirstSecond(int pulseSeconds, int expected)
    {
        PlugTransitions.Detect(Series((60, 2), (pulseSeconds, 60), (60, 2))).Should().HaveCount(expected);
    }

    [Fact]
    public void TheMoment_IsTheFirstSecondOfTheNewState_AndTheStepComesFromTheLevels_NotFromTheRamp()
    {
        // A ramp through the 3 s nearest to the change: outside both windows of the levels.
        var transitions = PlugTransitions.Detect(Series((60, 2), (3, 150), (60, 62), (60, 3)));

        transitions.Should().HaveCount(2);
        transitions[0].Should().Be(new PlugTransition(T0.AddSeconds(60), true, 60));
        transitions[1].TurnedOn.Should().BeFalse();
        transitions[1].TsUtc.Should().Be(T0.AddSeconds(123));
        transitions[1].StepW.Should().Be(-59);
    }

    [Fact]
    public void ARun_IsOnToOff_WithItsMedianLevel_AndTheIntegralOfThePlug()
    {
        var (_, runs) = PlugTransitions.Replay(Series((60, 2), (600, 60), (600, 80), (60, 2)));

        var run = runs.Should().ContainSingle().Subject;
        run.StartUtc.Should().Be(T0.AddSeconds(60));
        run.EndUtc.Should().Be(T0.AddSeconds(1260));
        run.PowerW.Should().Be(70, "the median of its seconds");
        run.EstimatedWh.Should().BeApproximately((600 * 60 + 600 * 80) / 3600.0, 1e-6);
        run.EndReason.Should().Be(RunEndReason.Paired);
    }

    [Fact]
    public void ARunStillOn_StaysOpen_WithTheLevelAfterItsSwitchOn()
    {
        var (_, runs) = PlugTransitions.Replay(Series((60, 2), (100, 60)));

        var run = runs.Should().ContainSingle().Subject;
        run.EndUtc.Should().BeNull();
        run.PowerW.Should().Be(60);
        run.EstimatedWh.Should().BeNull();
    }

    [Fact]
    public void AGapInTheData_IsNoTransition_AndClosesTheOpenRunAsUncertain_WithWhatWasMeasured()
    {
        // On before the gap, off after it: nothing says when it stopped.
        var (transitions, runs) = PlugTransitions.Replay(Series((60, 2), (120, 60), (200, null), (120, 2)));

        transitions.Should().ContainSingle("the switch-on; nothing across the gap").Which.TurnedOn.Should().BeTrue();
        var run = runs.Should().ContainSingle().Subject;
        run.EndUtc.Should().Be(T0.AddSeconds(179), "the last second with data");
        run.EndReason.Should().Be(RunEndReason.Timeout);
        run.EstimatedWh.Should().BeApproximately(120 * 60 / 3600.0, 1e-6);
    }

    [Fact]
    public void AfterAGap_TheStateStartsAgain_AndAnOnStateWithoutItsSwitchOnOpensNoRun()
    {
        var (transitions, runs) = PlugTransitions.Replay(Series((60, 2), (200, null), (120, 60), (120, 2), (120, 60), (60, 2)));

        transitions.Select(t => t.TurnedOn).Should().Equal(false, true, false);
        runs.Should().ContainSingle().Which.StartUtc.Should().Be(T0.AddSeconds(500));
    }

    [Fact]
    public void TheGrid_TakesTheStreamWhereItHasAValue_OtherwiseThePolls_AndHoldsBoth70S()
    {
        var stream = new[] { new PlugPowerPoint(T0, 50) };
        var polled = new[] { new PlugPowerPoint(T0.AddSeconds(-30), 2), new PlugPowerPoint(T0.AddSeconds(50), 7) };

        var grid = PlugTransitions.Grid(stream, polled, T0.AddSeconds(-10), T0.AddSeconds(200));

        grid.First(p => p.TsUtc == T0.AddSeconds(-10)).PowerW.Should().Be(2, "only the poll");
        grid.First(p => p.TsUtc == T0).PowerW.Should().Be(50, "the stream first");
        grid.First(p => p.TsUtc == T0.AddSeconds(70)).PowerW.Should().Be(50);
        grid.First(p => p.TsUtc == T0.AddSeconds(71)).PowerW.Should().Be(7, "the stream row is too old");
        grid.Last().TsUtc.Should().Be(T0.AddSeconds(120), "70 s after the last row");
        grid.Select(p => p.TsUtc).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
    }

    [Fact]
    public void TheWarmUp_GivesTheStateButNoTransition_AndNoRun()
    {
        var timeline = new PlugTimeline(T0.AddSeconds(100));
        var steps = new List<PlugStep>();
        var series = Series((60, 2), (100, 60), (100, 2));
        foreach (var p in series) steps.Add(timeline.Add(p.TsUtc, p.PowerW));
        for (var k = 1; k <= 20; k++) steps.Add(timeline.Add(series[^1].TsUtc.AddSeconds(k), 2));

        steps.Where(s => s.Transition is not null).Should().ContainSingle().Which.Transition!.Value.TurnedOn.Should().BeFalse("the switch-on is before the limit");
        steps.Should().OnlyContain(s => s.Opened == null && s.Closed == null, "a run it did not see start is not its run");
    }

    [Fact]
    public void ARunFromThePlug_IsStoredWithoutAnEvent_AndTheBuilderOfEventsDoesNotTakeItOver()
    {
        var run = new PlugRun(T0, null, 60, null).ToRun(7, 3);

        run.FromPlug.Should().BeTrue();
        run.StartEventId.Should().Be(ApplianceRun.NoEvent);
        run.EndEventId.Should().BeNull();

        var builder = new ApplianceRunBuilder(new[] { run, new ApplianceRun(8, 3, T0, null, 60, null, 11, null, 300) });
        builder.Open().Should().ContainSingle().Which.Id.Should().Be(8);
    }

    // ------------------------------------------------------------------ the signature of the runs

    private static ApplianceSignature Sig(long id) => new(id, "Frigider", "i", "fridge", 60, 0, 0, 1, T0, SignatureLabelSource.Plug, Fridge, false);

    [Fact]
    public void TheTargetOfTheRuns_IsTheOneWithMostConfirmations_ThenTheLowestId()
    {
        ExclusiveSignatures.RunTarget(Array.Empty<ApplianceSignature>(), Fridge, Array.Empty<MeterEvent>()).Should().BeNull();
        var own = new[] { Sig(5), Sig(3), Sig(9) };
        ExclusiveSignatures.RunTarget(own, Fridge, Array.Empty<MeterEvent>()).Should().Be(3, "no confirmation anywhere: the lowest id");

        MeterEvent Confirmed(long sig, Guid plug, EventLabelSource source = EventLabelSource.AutoPlug) => E(1, 0, 60, source, plug) with { SignatureId = sig };
        ExclusiveSignatures.RunTarget(own, Fridge, new[]
        {
            Confirmed(9, Fridge), Confirmed(9, Fridge), Confirmed(5, Fridge),
            Confirmed(3, Other), Confirmed(3, Other), Confirmed(3, Other),   // labelled by another plug
            Confirmed(3, Fridge, EventLabelSource.PlugShadow), Confirmed(3, Fridge, EventLabelSource.PlugShadow), Confirmed(3, Fridge, EventLabelSource.PlugShadow),
        }).Should().Be(9);
        ExclusiveSignatures.RunTarget(own, Fridge, new[] { Confirmed(9, Fridge), Confirmed(5, Fridge) }).Should().Be(5, "a tie: the lowest id");
    }

    [Fact]
    public void TheRow_CountsOnlyTheRunsFromThePlug_StartedInTheWindow()
    {
        ApplianceRun Run(int minute, long startEvent = ApplianceRun.NoEvent, double w = 60) => new(1, 3, T0.AddMinutes(minute), T0.AddMinutes(minute + 5), w, 5, startEvent, null);

        var summary = PlugRunSummary.Of(new[] { Run(-100), Run(0, w: 50), Run(20, w: 70), Run(40, startEvent: 99) }, T0, T0.AddHours(1));

        summary.Starts.Should().Be(2);
        summary.MeanPowerW.Should().Be(60);
        summary.LastStartUtc.Should().Be(T0.AddMinutes(20));
        summary.Repetition.Durations.P50.Should().Be(300);
        summary.Repetition.Intervals.P50.Should().Be(1200);

        var empty = PlugRunSummary.Of(Array.Empty<ApplianceRun>(), T0, T0);
        empty.Starts.Should().Be(0);
        empty.MeanPowerW.Should().BeNull();
        empty.LastStartUtc.Should().BeNull();
    }

    // ------------------------------------------------------------------ shadows

    [Fact]
    public void AShadow_IsTheOnlyCandidateOfATransition_AndItNamesThePlug()
    {
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, 97, 55) }).Should().Equal(new Dictionary<long, Guid> { [1] = Fridge });
        PlugShadows.Find(new[] { On(100, -60) }, new[] { E(1, 100, -48, EventLabelSource.Model) }).Should().ContainKey(1, "a prediction is not a confirmation");
    }

    [Fact]
    public void TwoCandidates_MarkNeither()
    {
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, 96, 55), E(2, 99, 65) }).Should().BeEmpty();
    }

    [Fact]
    public void TheOppositeSign_IsNoCandidate_AndDoesNotHideTheOtherOne()
    {
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, 98, -60) }).Should().BeEmpty();
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, 98, -60), E(2, 99, 60) }).Keys.Should().Equal(2L);
    }

    [Theory]
    [InlineData(94, 60, false)]
    [InlineData(95, 60, true)]
    [InlineData(100, 60, true)]
    [InlineData(101, 60, false)]   // after the transition
    [InlineData(99, 75, true)]
    [InlineData(99, 45, true)]
    [InlineData(99, 75.5, false)]
    [InlineData(99, 44.5, false)]
    public void TheWindow_Is5SBeforeToTheTransition_Within15W(int second, double p, bool shadow)
    {
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, second, p) }).ContainsKey(1).Should().Be(shadow);
    }

    [Theory]
    [InlineData(EventLabelSource.AutoPlug)]
    [InlineData(EventLabelSource.User)]
    [InlineData(EventLabelSource.DurationRule)]
    public void AConfirmedEvent_IsNeverOne_AndAsACandidateItKeepsTheOtherOut(EventLabelSource source)
    {
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, 99, 60, source, Other) }).Should().BeEmpty();
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, 99, 60, source, Other), E(2, 98, 60) }).Should().BeEmpty();
    }

    [Fact]
    public void OneEvent_ForTwoTransitions_IsAShadowOnce_OfTheFirst()
    {
        var found = PlugShadows.Find(new[] { On(102, 60, Other), On(100, 60) }, new[] { E(1, 99, 60) });

        found.Should().ContainSingle().Which.Value.Should().Be(Fridge);
    }

    [Fact]
    public void ARecalculation_FindsTheSameShadow()
    {
        PlugShadows.Find(new[] { On(100, 60) }, new[] { E(1, 99, 60, EventLabelSource.PlugShadow, Fridge) }).Keys.Should().Equal(1L);
    }

    [Fact]
    public void AShadow_ConfirmsNothing()
    {
        var shadow = E(1, 99, 60, EventLabelSource.PlugShadow, Fridge) with { SignatureId = 3 };

        ExclusiveSignatures.ConfirmedBy(shadow, Fridge).Should().BeFalse();
        ExclusiveSignatures.Belongs(shadow, Fridge).Should().BeFalse();
        ExclusiveSignatures.ConfirmedCentre(3, Fridge, new[] { shadow }).Should().BeNull();
    }

    [Fact]
    public void TheNewSource_HasACodeAndANameOfItsOwn_AndComesBackFromTheDatabase()
    {
        NilmSources.Code(EventLabelSource.PlugShadow).Should().Be("plug-shadow");
        NilmSources.Name(EventLabelSource.PlugShadow).Should().Be("umbră a prizei");
        NilmSources.EventFromInt((int)EventLabelSource.PlugShadow).Should().Be(EventLabelSource.PlugShadow);
    }
}
