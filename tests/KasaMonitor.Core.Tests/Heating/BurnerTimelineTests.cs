using FluentAssertions;
using KasaMonitor.Core.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Heating;

public class BurnerTimelineTests
{
    private static readonly Guid Dev = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 4, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double minute) => T0.AddMinutes(minute);

    /// <summary>One reading at <paramref name="minute"/> after T0 (2 min cadence in most tests).</summary>
    private static HeatingReading R(double minute, bool? on, double? supply = 30, double? dhw = 40, double? gasHeating = 1.0,
        double? gasDhw = 0.5, double? mod = null) =>
        new(Dev, At(minute), null, null, supply, null, null, 22, dhw, 45, on, mod ?? (on == true ? 20 : 0), on == true ? 100 : 0, null,
            null, null, "heating", "normal", "eco", null, null, gasHeating, gasDhw);

    [Fact]
    public void OnlyTheDhwCounterGrew_HotWater_WithTheGasAndTheMeanModulation()
    {
        var s = BurnerTimeline.Build(new[]
        {
            R(0, false), R(2, true, mod: 30), R(4, true, gasDhw: 0.6, mod: 50), R(6, true, gasDhw: 0.6, mod: 40), R(8, false, gasDhw: 0.6),
        });

        s.Should().ContainSingle();
        s[0].Purpose.Should().Be(BurnerPurpose.Dhw);
        s[0].StartUtc.Should().Be(At(2));
        s[0].EndUtc.Should().Be(At(8), "a reading holds until the next one");
        s[0].Duration.Should().Be(TimeSpan.FromMinutes(6));
        s[0].GasDhwM3.Should().BeApproximately(0.1, 1e-9);
        s[0].GasHeatingM3.Should().Be(0);
        s[0].AvgModulationPct.Should().Be(40);
        s[0].Readings.Should().Be(3);
        s[0].Ongoing.Should().BeFalse();
    }

    [Fact]
    public void OnlyTheHeatingCounterGrew_Heating()
    {
        var s = BurnerTimeline.Build(new[] { R(0, false), R(2, true, gasHeating: 1.1), R(4, true, gasHeating: 1.2), R(6, false, gasHeating: 1.2) });

        s.Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Heating);
        s[0].GasHeatingM3.Should().BeApproximately(0.2, 1e-9);
    }

    [Fact]
    public void CounterGrowingUpToSixtyMinutesAfterTheRun_Counts_LaterItDoesNot()
    {
        var rightAfter = BurnerTimeline.Build(new[] { R(0, false), R(2, true), R(4, true), R(6, false, gasDhw: 0.6) });
        rightAfter.Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Dhw);

        // The run ends at minute 6 (first reading with the burner off): an increase at minute 66 still counts, at 68 it does not.
        static List<HeatingReading> Late(int minute)
        {
            var list = new List<HeatingReading> { R(0, false), R(2, true), R(4, true) };
            for (var m = 6; m < minute; m += 2) list.Add(R(m, false));
            list.Add(R(minute, false, gasHeating: 1.6));
            return list;
        }
        var within = BurnerTimeline.Build(Late(66));
        within.Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Heating);
        within[0].GasHeatingM3.Should().BeApproximately(0.6, 1e-9);
        within[0].EndUtc.Should().Be(At(6), "the lookahead attributes gas, it does not lengthen the bar");

        // Same temperatures everywhere: past 60 minutes the run falls to rule (b) → unknown.
        BurnerTimeline.Build(Late(68)).Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Unknown);
    }

    [Fact]
    public void CounterWindow_StopsBeforeTheNextRun_EachIncreaseBelongsToOneRun()
    {
        // Run 1 at 2–4, run 2 at 20–22; the heating counter grows at 18 (before run 2) and the hot water one at 22.
        var s = BurnerTimeline.Build(new[]
        {
            R(0, false), R(2, true), R(4, true), R(6, false), R(8, false), R(10, false), R(12, false), R(14, false), R(16, false),
            R(18, false, gasHeating: 1.2), R(20, true, gasHeating: 1.2), R(22, true, gasHeating: 1.2, gasDhw: 0.6),
            R(24, false, gasHeating: 1.2, gasDhw: 0.6),
        });

        s.Should().HaveCount(2);
        s[0].Purpose.Should().Be(BurnerPurpose.Heating);
        s[0].GasHeatingM3.Should().BeApproximately(0.2, 1e-9);
        s[0].GasDhwM3.Should().Be(0);
        s[1].Purpose.Should().Be(BurnerPurpose.Dhw);
        s[1].GasHeatingM3.Should().Be(0, "the heating increase at 18 belongs to run 1");
        s[1].GasDhwM3.Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public void BothCountersGrew_LongRun_SplitInProportion_HeatingFirst()
    {
        // 20 min run (2 → 22), heating +0.3, hot water +0.1 → heating 15 min, hot water 5 min.
        var readings = new List<HeatingReading> { R(0, false) };
        for (var m = 2; m <= 20; m += 2) readings.Add(R(m, true));
        readings.Add(R(22, false, gasHeating: 1.3, gasDhw: 0.6));

        var s = BurnerTimeline.Build(readings);

        s.Should().HaveCount(2);
        s[0].Purpose.Should().Be(BurnerPurpose.Heating);
        s[0].StartUtc.Should().Be(At(2));
        s[0].EndUtc.Should().Be(At(17));
        s[0].GasHeatingM3.Should().BeApproximately(0.3, 1e-9);
        s[1].Purpose.Should().Be(BurnerPurpose.Dhw);
        s[1].StartUtc.Should().Be(At(17));
        s[1].EndUtc.Should().Be(At(22));
        s[1].GasDhwM3.Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public void BothCountersGrew_ShortRun_Unknown()
    {
        var s = BurnerTimeline.Build(new[] { R(0, false), R(2, true), R(4, true), R(6, false, gasHeating: 1.1, gasDhw: 0.6) });

        s.Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Unknown);
        s[0].Duration.Should().Be(TimeSpan.FromMinutes(4));
    }

    /// <summary>Rule (b) on one run (no counter moves): the reading before it, then one reading with the burner on.</summary>
    private static BurnerPurpose RuleB(double supplyFrom, double supplyTo, double dhwFrom, double dhwTo) =>
        BurnerTimeline.Build(new[] { R(0, false, supply: supplyFrom, dhw: dhwFrom), R(2, true, supply: supplyTo, dhw: dhwTo) }).Single().Purpose;

    [Theory]
    [InlineData(30, 32, 40, 45, BurnerPurpose.Dhw)]       // hot water +5, supply +2: 3 °C more than the supply
    [InlineData(30, 30.5, 40, 42, BurnerPurpose.Dhw)]     // supply flat (+0.5 < 1), hot water +2
    [InlineData(30, 35, 40, 44, BurnerPurpose.Heating)]   // supply +5, hot water +4 ≤ supply (combi: it follows the supply)
    [InlineData(38.5, 47.7, 38.4, 45.1, BurnerPurpose.Heating)]   // 2 Nov 08:35 (+9.2 / +6.7): no longer "hot water"
    [InlineData(30, 35, 40, 41, BurnerPurpose.Heating)]   // the 0.8.4 rule called this hot water (+1 °C)
    [InlineData(30, 31, 40, 41.5, BurnerPurpose.Unknown)] // supply +1, hot water +1.5: neither
    [InlineData(30, 31.5, 40, 43, BurnerPurpose.Unknown)] // hot water +3 but only 1.5 over a rising supply; supply < 2
    [InlineData(30, 30, 40, 40, BurnerPurpose.Unknown)]   // nothing rose
    public void NoCounterGrew_RuleB_ForACombiBoiler(double supplyFrom, double supplyTo, double dhwFrom, double dhwTo, BurnerPurpose expected) =>
        RuleB(supplyFrom, supplyTo, dhwFrom, dhwTo).Should().Be(expected);

    [Fact]
    public void NoCounterGrew_FromTheReadingBeforeTheRun_OngoingRun_MissingValuesAreNoRise()
    {
        var s = BurnerTimeline.Build(new[] { R(0, false, supply: 30, dhw: 40), R(2, true, supply: 30, dhw: 43) });
        s.Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Dhw);
        s[0].Ongoing.Should().BeTrue();
        s[0].EndUtc.Should().Be(At(2));

        // No counters at all, no supply sensor: hot water +2 with a "flat" (missing) supply → hot water.
        BurnerTimeline.Build(new[] { R(0, false, gasHeating: null, gasDhw: null, supply: null, dhw: 40), R(2, true, gasHeating: null, gasDhw: null, supply: null, dhw: 42) })
            .Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Dhw);
    }

    [Fact]
    public void RealMorningOf2November_BothRunsAreHeating_TheSecondThroughTheLateCounter()
    {
        // The readings of 2 Nov 2026 05:00–07:15 UTC (5-minute cadence): heating counter 0 → 0.1 at 05:10, 0.1 → 0.7 at 07:10.
        var t = new DateTimeOffset(2026, 11, 2, 5, 0, 0, TimeSpan.Zero);
        HeatingReading Real(int minute, bool on, double supply, double dhw, double gasHeating, double mod) =>
            new(Dev, t.AddMinutes(minute), null, 20, supply, null, null, 22.5, dhw, 45, on, mod, 100, null, null, null, "heating", "normal", "eco",
                null, null, gasHeating, 0);
        var readings = new List<HeatingReading>
        {
            Real(0, false, 24.2, 23.4, 0, 0), Real(5, true, 44.4, 38.2, 0, 49.1), Real(10, true, 48.6, 45.4, 0.1, 38),
            Real(15, true, 49.1, 46.4, 0.1, 29.6), Real(20, true, 49.1, 46.4, 0.1, 23.9), Real(25, true, 49.1, 46.4, 0.1, 19.4),
            Real(30, true, 49.6, 47.4, 0.1, 16), Real(35, true, 49.6, 47.4, 0.1, 10.5), Real(40, true, 49.6, 47.4, 0.1, 10.5),
            Real(45, true, 49.6, 47.4, 0.1, 10.5), Real(50, true, 50.1, 48.4, 0.1, 10.5), Real(55, true, 50.6, 48.4, 0.1, 10.5),
            Real(60, true, 50.6, 48.4, 0.1, 10.5), Real(65, false, 46, 47.4, 0.1, 0), Real(70, false, 43.5, 44.4, 0.1, 0),
            Real(75, false, 42, 42.4, 0.1, 0), Real(80, false, 40.5, 41.4, 0.1, 0), Real(85, false, 39.5, 39.4, 0.1, 0),
            Real(90, false, 38.5, 38.4, 0.1, 0), Real(95, true, 45.7, 45.1, 0.1, 10.7), Real(100, true, 46.7, 45.1, 0.1, 10.7),
            Real(105, true, 47.7, 45.1, 0.1, 10.7), Real(110, false, 41.7, 43.1, 0.1, 0), Real(115, false, 40.2, 40.1, 0.1, 0),
            Real(120, false, 39.2, 39.1, 0.1, 0), Real(125, false, 38.2, 38.1, 0.1, 0), Real(130, false, 38.2, 37.1, 0.7, 0),
            Real(135, false, 38.7, 37.1, 0.7, 0),
        };

        var s = BurnerTimeline.Build(readings);

        s.Should().HaveCount(2);
        s.Should().OnlyContain(x => x.Purpose == BurnerPurpose.Heating);
        s[0].GasHeatingM3.Should().BeApproximately(0.1, 1e-9, "its window stops before the second run");
        s[1].StartUtc.Should().Be(t.AddMinutes(95));
        s[1].EndUtc.Should().Be(t.AddMinutes(110));
        s[1].GasHeatingM3.Should().BeApproximately(0.6, 1e-9, "the +0.6 at 07:10 UTC, 20 minutes after the run");
    }

    [Fact]
    public void MissingReadingUpToFiveMinutes_DoesNotBreakTheRun_ALongerGapDoes()
    {
        // Cadence 2 min (median) → a run survives gaps up to 7 min.
        var joined = BurnerTimeline.Build(new[] { R(0, false), R(2, true), R(4, true), R(10, true), R(12, true), R(14, false) });
        joined.Should().ContainSingle();
        joined[0].StartUtc.Should().Be(At(2));
        joined[0].EndUtc.Should().Be(At(14));

        var broken = BurnerTimeline.Build(new[] { R(0, false), R(2, true), R(4, true), R(6, true), R(14, true), R(16, true), R(18, false) });
        broken.Should().HaveCount(2);
        broken[0].EndUtc.Should().Be(At(8), "no reading after the gap: the last reading holds for one cadence");
        broken[1].StartUtc.Should().Be(At(14));
    }

    [Fact]
    public void FiveMinuteCadence_OneMissingReadingStillJoins()
    {
        BurnerTimeline.Cadence(new[] { R(0, false), R(5, true), R(10, true), R(15, true) }).Should().Be(TimeSpan.FromMinutes(5));
        BurnerTimeline.Build(new[] { R(0, false), R(5, true), R(10, true), R(20, true), R(25, false) }).Should().ContainSingle();
    }

    [Fact]
    public void DailyReset_IsNotANegativeIncrease()
    {
        // The heating counter resets (1.4 → 0) during the run and then grows by 0.1.
        var s = BurnerTimeline.Build(new[] { R(0, false, gasHeating: 1.4), R(2, true, gasHeating: 0), R(4, true, gasHeating: 0.1), R(6, false, gasHeating: 0.1) });

        s.Should().ContainSingle().Which.Purpose.Should().Be(BurnerPurpose.Heating);
        s[0].GasHeatingM3.Should().BeApproximately(0.1, 1e-9);
    }

    [Fact]
    public void Current_FollowsTheLatestReading()
    {
        BurnerTimeline.Current(Array.Empty<HeatingReading>()).Should().BeNull();
        BurnerTimeline.Current(new[] { R(0, null) }).Should().BeNull("an unknown burner state is ignored");
        BurnerTimeline.Current(new[] { R(0, true), R(2, false) }).Should().Be(BurnerPurpose.Off);
        BurnerTimeline.Current(new[] { R(0, false), R(2, true), R(4, true, gasHeating: 1.1) }).Should().Be(BurnerPurpose.Heating);
        BurnerTimeline.Current(new[] { R(0, false, dhw: 40), R(2, true, dhw: 42), R(4, null) }).Should().Be(BurnerPurpose.Dhw);
        BurnerTimeline.Current(new[] { R(0, false), R(2, true) }).Should().Be(BurnerPurpose.Unknown);
        // A run still going stays unknown until a counter moves; then (re-evaluated at every reading) it is decided.
        BurnerTimeline.Current(new[] { R(0, false), R(2, true), R(4, true) }).Should().Be(BurnerPurpose.Unknown);
        BurnerTimeline.Current(new[] { R(0, false), R(2, true), R(4, true), R(6, true, gasDhw: 0.6) }).Should().Be(BurnerPurpose.Dhw);
    }

    [Fact]
    public void Labels_AreTheMetricValues()
    {
        BurnerTimeline.Label(BurnerPurpose.Heating).Should().Be("heating");
        BurnerTimeline.Label(BurnerPurpose.Dhw).Should().Be("dhw");
        BurnerTimeline.Label(BurnerPurpose.Unknown).Should().Be("unknown");
        BurnerTimeline.Label(BurnerPurpose.Off).Should().Be("off");
    }
}
