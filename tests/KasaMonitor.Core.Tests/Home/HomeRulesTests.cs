using FluentAssertions;
using KasaMonitor.Core.Home;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.Home;

public class StandbyRuleTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly Guid Main = Guid.NewGuid();
    private static readonly Guid Fridge = Guid.NewGuid();
    private static readonly Guid Router = Guid.NewGuid();

    private static LiveMinuteAggregate M(Guid plug, DateTimeOffset minute, double w) => new(plug, minute, w, w, w, null, w / 60, 60, 60);

    /// <summary>Minutes of one night window with the main meter at <paramref name="mainW"/> and the plugs as given.</summary>
    private static IEnumerable<LiveMinuteAggregate> Night((DateTimeOffset From, DateTimeOffset To) w, Func<int, double> mainW, params (Guid Id, double W)[] plugs)
    {
        var i = 0;
        for (var t = w.From; t < w.To; t = t.AddMinutes(1), i++)
        {
            yield return M(Main, t, mainW(i));
            foreach (var (id, pw) in plugs) yield return M(id, t, pw);
        }
    }

    [Fact]
    public void NightWindows_AreTheLast7Nights_0100To0500Local_TonightOnlyOnceEnded()
    {
        // 02.11.2026 03:00 local (EET): tonight's window has not ended.
        var now = new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero);
        var w = StandbyRule.NightWindows(now, Ro);
        w.Should().HaveCount(7);
        w[0].FromUtc.Should().Be(new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero), "01.11 01:00 EET");
        w[0].ToUtc.Should().Be(new DateTimeOffset(2026, 11, 1, 3, 0, 0, TimeSpan.Zero));
        w.Should().OnlyContain(x => x.ToUtc <= now);
        // 25.10.2026 (DST ends 04:00 → 03:00): the window is 5 real hours, still 01:00–05:00 on the wall clock.
        StandbyRule.NightWindows(new DateTimeOffset(2026, 10, 28, 10, 0, 0, TimeSpan.Zero), Ro).Should().Contain(x => x.FromUtc == new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero) && x.ToUtc == new DateTimeOffset(2026, 10, 25, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Floor_IsTheMedianOfMainMinusPlugs_NeverTheMainAlone_NoDoubleCountWithThePlugs()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var windows = StandbyRule.NightWindows(now, Ro);
        // Main 300 W at night, of which the fridge 60 W and the router 20 W (plugs): the rest = 220 W, with a few spikes.
        var minutes = windows.SelectMany(w => Night(w, i => i % 50 == 0 ? 2000 : 300, (Fridge, 60), (Router, 20))).ToList();

        var floor = StandbyRule.Compute(minutes, Main, new[] { Fridge, Router }, windows);

        floor.Should().NotBeNull();
        floor!.Watts.Should().BeApproximately(220, 1e-9, "the median ignores the spikes and the plugs' own standby stays in the plugs");
        floor.Nights.Should().Be(7);
        floor.Minutes.Should().Be(7 * 240);
    }

    [Fact]
    public void Floor_WithFewerThan7Nights_UsesTheNightsThatExist()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var windows = StandbyRule.NightWindows(now, Ro);
        var minutes = windows.Take(2).SelectMany(w => Night(w, _ => 150, (Fridge, 50))).ToList();

        var floor = StandbyRule.Compute(minutes, Main, new[] { Fridge }, windows)!;

        floor.Watts.Should().Be(100);
        floor.Nights.Should().Be(2);
        StandbyRule.Compute(Array.Empty<LiveMinuteAggregate>(), Main, new[] { Fridge }, windows).Should().BeNull("no night yet");
    }

    [Fact]
    public void Floor_SkipsMinutesWhereAStreamingPlugHasAGap_AndIsNeverNegative()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var windows = StandbyRule.NightWindows(now, Ro).Take(1).ToList();
        var w = windows[0];
        var minutes = Night(w, _ => 100, (Fridge, 40)).ToList();
        // The fridge's stream has a gap in the first 60 minutes: those minutes would show 100 W of "standby".
        minutes.RemoveAll(m => m.PlugId == Fridge && m.MinuteUtc < w.FromUtc.AddMinutes(60));
        StandbyRule.Compute(minutes, Main, new[] { Fridge }, windows)!.Should().Be(new StandbyFloor(60, 1, 180));

        var negative = Night(w, _ => 50, (Fridge, 80)).ToList();
        StandbyRule.Compute(negative, Main, new[] { Fridge }, windows)!.Watts.Should().Be(0);
    }

    [Fact]
    public void APlugWithoutStreamThatNight_SkipsTheNight_APlugWithoutStreamAtAll_IsSubtractedFromItsPolls()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var windows = StandbyRule.NightWindows(now, Ro).Take(2).ToList();
        // Night 0: the fridge streamed; night 1: the fridge was offline (no minute) — its 40 W would become "standby".
        var minutes = Night(windows[0], _ => 100, (Fridge, 40)).Concat(Night(windows[1], _ => 100)).ToList();
        var floor = StandbyRule.Compute(minutes, Main, new[] { Fridge }, windows)!;
        floor.Should().Be(new StandbyFloor(60, 1, 240), "night 1 is skipped, not counted with the fridge in the rest");

        // Fewer than half of the main meter's minutes: skipped as well.
        var sparse = Night(windows[0], _ => 100, (Fridge, 40)).Where(m => m.PlugId != Fridge || m.MinuteUtc < windows[0].FromUtc.AddMinutes(100)).ToList();
        StandbyRule.Compute(sparse, Main, new[] { Fridge }, windows).Should().BeNull();

        // The router has no live stream: its mean polled power (30 W) is subtracted; unknown polls skip the night.
        var router = Night(windows[0], _ => 100, (Fridge, 40)).ToList();
        StandbyRule.Compute(router, Main, new[] { Fridge }, windows, wi => wi == 0 ? 30 : null)!.Watts.Should().Be(30);
        StandbyRule.Compute(router, Main, new[] { Fridge }, windows, _ => null).Should().BeNull();
    }
}

public class NowBreakdownTests
{
    private static NowPart Plug(string name, double w) => new(name, name, NowKind.Plug, w);
    private static NowPart Det(string name, double w) => new("sig-" + name, name, NowKind.Detected, w);

    [Fact]
    public void Parts_AddUpToTheMeter_StandbyAndDetectedComeOutOfTheRest()
    {
        var b = NowBreakdownRule.Build(520, new[] { Plug("Camera tehnica", 150), Plug("UPS", 135), Plug("Frigider", 45) },
            new[] { Det("Boiler", 60) }, standbyFloorW: 95);

        b.PartsW.Should().BeApproximately(520, 1e-9);
        b.RestW.Should().Be(190);
        b.Parts.Single(p => p.Kind == NowKind.Standby).Watts.Should().Be(95);
        b.Parts.Single(p => p.Kind == NowKind.Detected).Watts.Should().Be(60);
        b.Parts.Single(p => p.Kind == NowKind.Rest).Watts.Should().Be(35);
        b.DetectedScaled.Should().BeFalse();
    }

    [Fact]
    public void Standby_NeverExceedsTheRest_DetectedScaledIntoWhatIsLeft_NoDoubleCounting()
    {
        // Rest now = 100 W, floor 120 W: standby shows 100 W, nothing left for the detected ones (scaled to 0).
        var b = NowBreakdownRule.Build(400, new[] { Plug("A", 300) }, new[] { Det("Boiler", 50) }, standbyFloorW: 120);
        b.Parts.Single(p => p.Kind == NowKind.Standby).Watts.Should().Be(100);
        b.Parts.Single(p => p.Kind == NowKind.Detected).Watts.Should().Be(0);
        b.Parts.Single(p => p.Kind == NowKind.Rest).Watts.Should().Be(0);
        b.PartsW.Should().BeApproximately(400, 1e-9);
        b.DetectedScaled.Should().BeTrue();

        // Detected 2 × 60 W into 80 W: 40 W each.
        var c = NowBreakdownRule.Build(380, new[] { Plug("A", 300) }, new[] { Det("X", 60), Det("Y", 60) }, standbyFloorW: null);
        c.Parts.Where(p => p.Kind == NowKind.Detected).Select(p => p.Watts).Should().Equal(40, 40);
        c.Parts.Should().NotContain(p => p.Kind == NowKind.Standby, "the floor is not known yet");
        foreach (var (meter, floor) in new[] { (380.0, 0.0), (380.0, 3.0), (302.0, 100.0) })
        {
            // A floor of 0 or 3 W, or a 100 W floor with only 2 W of rest now: no standby part, nothing taken from the rest.
            var d = NowBreakdownRule.Build(meter, new[] { Plug("A", 300) }, Array.Empty<NowPart>(), floor);
            d.Parts.Should().NotContain(p => p.Kind == NowKind.Standby);
            d.PartsW.Should().BeApproximately(meter, 1e-9);
            d.Parts.Single(p => p.Kind == NowKind.Rest).Watts.Should().BeApproximately(meter - 300, 1e-9);
        }
        c.PartsW.Should().BeApproximately(380, 1e-9);
    }

    [Fact]
    public void PlugsAboveTheMeter_RestIsZero_PartsAreThePlugs_WithoutMeterOnlyPlugs()
    {
        var b = NowBreakdownRule.Build(500, new[] { Plug("A", 300), Plug("B", 220) }, Array.Empty<NowPart>(), 80);
        b.PlugsAboveMeter.Should().BeTrue();
        b.RestW.Should().Be(0);
        b.PartsW.Should().Be(520);

        var none = NowBreakdownRule.Build(null, new[] { Plug("A", -3) }, new[] { Det("X", 10) }, 80);
        none.Parts.Should().ContainSingle().Which.Watts.Should().Be(0, "negative readings count as 0");
    }
}

public class BillEstimateTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly ReportPeriod November = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));

    [Fact]
    public void Projection_IsUsedSoFar_PlusMeanPerElapsedDay_TimesDaysLeft()
    {
        // 03.11.2026 00:00 local: exactly 2 days elapsed, 16,9 kWh used → 16,9 + 8,45 × 28 = 253,5 kWh.
        var now = new DateTimeOffset(2026, 11, 2, 22, 0, 0, TimeSpan.Zero);
        var p = BillEstimateRule.Project(16.9, November, now, Ro)!;
        p.Projected.Should().BeApproximately(253.5, 1e-9);
        p.ElapsedDays.Should().BeApproximately(2, 1e-9);
        p.DaysInMonth.Should().Be(30);
        p.LowConfidence.Should().BeTrue("fewer than 7 days");

        var later = BillEstimateRule.Project(100, November, new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.Zero), Ro)!;
        later.LowConfidence.Should().BeFalse();
        later.Projected.Should().BeApproximately(300, 1e-9);
        later.DaysLeft.Should().BeApproximately(20, 1e-9);
        (later.SoFar + later.PerDay * later.DaysLeft).Should().BeApproximately(later.Projected, 1e-9, "the shown calculation multiplies out");

        // October 2026 has 31 days + 1 h (DST ends on the 25th): the days left use the real length.
        var october = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 10, 1));
        var o = BillEstimateRule.Project(10, october, new DateTimeOffset(2026, 10, 10, 21, 0, 0, TimeSpan.Zero), Ro)!;
        (o.SoFar + o.PerDay * o.DaysLeft).Should().BeApproximately(o.Projected, 1e-9);
    }

    [Fact]
    public void NoProjection_InTheFirstHours_OutsideTheMonth_OrWithoutData()
    {
        BillEstimateRule.Project(1, November, new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero), Ro).Should().BeNull("1 h into the month");
        BillEstimateRule.Project(1, November, new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero), Ro).Should().BeNull();
        BillEstimateRule.Project(null, November, new DateTimeOffset(2026, 11, 5, 0, 0, 0, TimeSpan.Zero), Ro).Should().BeNull();
    }

    [Fact]
    public void Estimate_TotalIsTheSumOfWhatIsKnown()
    {
        var p = new MonthProjection(10, 150, 2, 30, true);
        new BillEstimate(p, 166.5m, null, null).TotalRon.Should().Be(166.5m);
        new BillEstimate(p, 166.5m, p, 100m).TotalRon.Should().Be(266.5m);
        new BillEstimate(null, null, null, null).TotalRon.Should().BeNull();
        new BillEstimate(p, 1m, null, null).LowConfidence.Should().BeTrue();
        new BillEstimate(p, 166.5m, null, null, GasExpected: true).TotalRon.Should().BeNull("a boiler but no gas: never gas = 0");
        new BillEstimate(null, null, p, 100m).TotalRon.Should().BeNull("gas alone is not a total");
    }
}

public class EnergyFlowRuleTests
{
    private static FlowItem P(string name, double kwh) => new(name, name, FlowKind.Plug, kwh);
    private static FlowItem D(string name, double kwh) => new("sig-" + name, name, FlowKind.Detected, kwh);

    private static readonly FlowItem[] Plugs =
    {
        P("Hol mic", 1.9), P("Camera tehnica", 1.6), P("UPS Birou", 1.4), P("Frigider", 0.9), P("Birou", 0.7),
        P("Bucatarie", 0.6), P("TV", 0.3), P("Sufragerie AC", 0),
    };

    [Fact]
    public void Flows_AddUpToTheMeter_TopFivePlugsPlusOthers_DetectedAndUndetected()
    {
        var f = EnergyFlowRule.Build(8.2, Plugs, new[] { D("Centrala electric", 0.4) });

        f.FlowsKwh.Should().BeApproximately(8.2, 1e-9);
        f.Rows.Sum(r => r.Kwh).Should().BeApproximately(8.2, 1e-9);
        f.Flows.Select(x => x.Name).Should().Equal("Hol mic", "Camera tehnica", "UPS Birou", "Frigider", "Birou", "Alte 3 prize", "Centrala electric", "Nedetectat");
        f.Flows.Single(x => x.Key == EnergyFlowRule.OtherPlugsKey).Kwh.Should().BeApproximately(0.9, 1e-9);
        f.UndetectedKwh.Should().BeApproximately(0.4, 1e-9);
        f.Rows.Last().Kind.Should().Be(FlowKind.Undetected);
        f.PlugsAboveMeter.Should().BeFalse();
        f.Flows.Single(x => x.Key == EnergyFlowRule.OtherPlugsKey).Children!.Select(x => x.Name).Should().Equal("Bucatarie", "TV", "Sufragerie AC");
        f.HasDetected.Should().BeTrue();
    }

    [Fact]
    public void DetectedAboveWhatIsLeft_AreScaled_PlugsAboveTheMeter_SourceIsThePlugs()
    {
        var f = EnergyFlowRule.Build(8.0, Plugs, new[] { D("A", 0.5), D("B", 0.5) });   // 0,6 kWh left for 1,0 kWh detected
        f.DetectedScaled.Should().BeTrue();
        f.DetectedKwh.Should().BeApproximately(0.6, 1e-9);
        f.UndetectedKwh.Should().Be(0);
        f.FlowsKwh.Should().BeApproximately(8.0, 1e-9);

        var over = EnergyFlowRule.Build(7.0, Plugs, new[] { D("A", 0.5) });
        over.PlugsAboveMeter.Should().BeTrue();
        over.SourceKwh.Should().BeApproximately(7.4, 1e-9);
        over.FlowsKwh.Should().BeApproximately(over.SourceKwh, 1e-9);
        over.Flows.Should().NotContain(x => x.Kind == FlowKind.Detected);
        over.HasDetected.Should().BeFalse("scaled to nothing: the „reduced proportionally” note must not appear");
        over.DetectedEstimatedKwh.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void TinyDetectedAppliances_AreCountedInUndetected_ASingleOtherIsShownByName()
    {
        var detected = Enumerable.Range(1, 40).Select(i => D("Necunoscut #" + i, 0.002)).Append(D("Boiler", 0.3));
        var f = EnergyFlowRule.Build(9.0, Plugs.Take(6), detected);
        f.Rows.Where(r => r.Kind == FlowKind.Detected).Select(r => r.Name).Should().Equal("Boiler");
        f.FlowsKwh.Should().BeApproximately(9.0, 1e-9);
        f.UndetectedKwh.Should().BeApproximately(9.0 - 7.1 - 0.3, 1e-9);
        f.Flows.Select(x => x.Name).Should().Contain("Bucatarie", "6 plugs: the sixth is shown by name, not as „Alte 1 prize”");
    }
}

public class BubbleLayoutTests
{
    private static readonly Dictionary<string, double> Watts = new()
    {
        ["Camera tehnica"] = 150, ["UPS Birou"] = 135, ["standby"] = 95, ["Birou"] = 48, ["Frigider"] = 45, ["Hol mic"] = 40,
        ["TV"] = 12, ["Switch"] = 6, ["Bucatarie"] = 4, ["AC"] = 0, ["rest"] = 0,
    };

    [Fact]
    public void Layout_IsDeterministic_AndCirclesNeverOverlap()
    {
        var order = BubbleLayout.Order(null, Watts);
        var a = BubbleLayout.Layout(order, Watts, 600, 250);
        var b = BubbleLayout.Layout(BubbleLayout.Order(null, new Dictionary<string, double>(Watts)), Watts, 600, 250);
        a.Bubbles.Should().BeEquivalentTo(b.Bubbles, o => o.WithStrictOrdering());
        order.Take(3).Should().Equal("Camera tehnica", "UPS Birou", "standby");

        foreach (var p in a.Bubbles)
        foreach (var q in a.Bubbles.Where(q => string.CompareOrdinal(q.Key, p.Key) > 0))
        {
            var d = Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y));
            d.Should().BeGreaterThanOrEqualTo(p.SlotR + q.SlotR - 0.05, $"{p.Key} and {q.Key} must not overlap");
        }
        a.Bubbles.Should().OnlyContain(c => c.X - c.SlotR >= -0.5 && c.Y - c.SlotR >= -0.5 && c.Y + c.SlotR <= 250.5);
        a.Fits(Watts).Should().BeTrue();
        a.Radius(150).Should().BeGreaterThan(a.Radius(45));
        (a.Radius(150) * a.Radius(150) / (a.Radius(48) * a.Radius(48))).Should().BeApproximately(150.0 / 48, 1e-6, "area ∝ W");
    }

    [Fact]
    public void Order_HasHysteresis_NewKeysAppended_LayoutKeptWhileValuesFit()
    {
        var order = BubbleLayout.Order(null, Watts);
        var wobble = new Dictionary<string, double>(Watts) { ["Frigider"] = 50 };   // 50 W vs Birou 48 W: within 10 % + 2 W
        BubbleLayout.Order(order, wobble).Should().Equal(order);

        var jump = new Dictionary<string, double>(Watts) { ["Frigider"] = 140 };
        BubbleLayout.Order(order, jump).Take(3).Should().Equal("Camera tehnica", "UPS Birou", "Frigider");   // not past UPS: 140 < 135 × 1,1 + 2

        var added = new Dictionary<string, double>(Watts) { ["sig-1"] = 2000 };
        BubbleLayout.Order(order, added).Should().StartWith(new[] { "sig-1" }, "a clearly larger newcomer moves to the front");

        var layout = BubbleLayout.Layout(order, Watts, 600, 250);
        layout.Fits(new Dictionary<string, double>(Watts) { ["Camera tehnica"] = 160 }).Should().BeTrue("within the slot's slack");
        layout.Fits(new Dictionary<string, double>(Watts) { ["Camera tehnica"] = 400 }).Should().BeFalse("outgrew its slot: lay out again");
    }

    [Fact]
    public void Pack_PlacesEachCircleTouchingAnother_WithTheGap()
    {
        var pos = BubbleLayout.Pack(new double[] { 40, 30, 20 });
        pos[0].Should().Be((0, 0));
        var d = Math.Sqrt(pos[1].X * pos[1].X + pos[1].Y * pos[1].Y);
        d.Should().BeApproximately(40 + 30 + BubbleLayout.Gap, 1e-6);
    }
}

public class LocalClockAndProfileTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public void YesterdaySameTime_IsTheWallClockTime_AlsoAcrossDst()
    {
        // 02.11.2026 15:35 EET → 01.11.2026 15:35 EET.
        LocalClock.YesterdaySameTimeUtc(new DateTimeOffset(2026, 11, 2, 13, 35, 0, TimeSpan.Zero), Ro)
            .Should().Be(new DateTimeOffset(2026, 11, 1, 13, 35, 0, TimeSpan.Zero));
        // 26.10.2026 10:00 EET (UTC+2) → 25.10 10:00, already EET after the change at 04:00: 24 h.
        LocalClock.YesterdaySameTimeUtc(new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero), Ro)
            .Should().Be(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));
        // 25.10.2026 10:00 EET → 24.10 10:00 EEST (UTC+3): 25 h earlier, same wall clock.
        LocalClock.YesterdaySameTimeUtc(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero), Ro)
            .Should().Be(new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero));
        // 30.03.2026 03:30 EEST → 29.03 03:30 does not exist (03:00 → 04:00): the first valid time after it, 04:00 EEST.
        LocalClock.YesterdaySameTimeUtc(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero), Ro)
            .Should().Be(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void TypicalProfile_IsTheMeanPerLocalHour_OfTheWellCoveredHours()
    {
        var main = Guid.NewGuid();
        var hours = new List<PlugHourWh>
        {
            new(main, new DateTimeOffset(2026, 10, 30, 16, 0, 0, TimeSpan.Zero), 400),          // 18:00 EET
            new(main, new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), 600),
            new(main, new DateTimeOffset(2026, 11, 1, 16, 0, 0, TimeSpan.Zero), 100, 20),       // gap: left out
            new(main, new DateTimeOffset(2026, 11, 1, 17, 0, 0, TimeSpan.Zero), 270, 54),       // 19:00, scaled to 60 min
        };
        var p = TypicalProfile.Build(hours, Ro);
        p.Should().HaveCount(24);
        p[18].Should().BeApproximately(0.5, 1e-9);
        p[19].Should().BeApproximately(0.3, 1e-9);
        p[3].Should().BeNull();
    }
}

public class SameFractionTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public void Position_IsTheCurrentBucketAndItsElapsedShare_PreviousIsCutAtTheSamePoint()
    {
        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));
        // 02.11.2026 12:00 EET = day 2 (index 1), half elapsed.
        var pos = SameFraction.Position(month.Buckets(Ro), new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero))!.Value;
        pos.Index.Should().Be(1);
        pos.Fraction.Should().BeApproximately(0.5, 1e-9);
        var october = Enumerable.Repeat<double?>(10, 31).ToList();
        SameFraction.Previous(october, pos).Should().Be(15, "the whole 1 October + half of 2 October, not the first 2 whole days");
        SameFraction.Position(month.Buckets(Ro), new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero)).Should().BeNull();
        SameFraction.Previous(new double?[] { null, null }, (1, 0.5)).Should().BeNull();
        SameFraction.Previous(new double?[] { 3 }, (5, 0.5)).Should().Be(3, "beyond the previous period's end: all of it");
    }
}

public class EnergyPricesTests
{
    private static readonly KasaMonitor.Core.Costing.EnergyPrices WithGas = new(1.30m, 10.55, 0.31m, HasGas: true);
    private static readonly KasaMonitor.Core.Costing.EnergyPrices NoGas = new(1.30m, 10.55, 0.31m, HasGas: false);

    [Fact]
    public void Total_NeedsEveryComponentThatShouldExist_NeverCountsAMissingOneAsZero()
    {
        WithGas.Total(10, 1).Should().Be(13.0m + (decimal)(1 * 10.55) * 0.31m);
        WithGas.Total(10, null).Should().BeNull("a boiler but no gas value");
        WithGas.Total(null, 1).Should().BeNull("gas alone is not a total");
        NoGas.Total(10, null).Should().Be(13.0m, "no boiler: electricity is the total");
        WithGas.Electricity(null).Should().BeNull();
        WithGas.Gas(2).Should().Be((decimal)(2 * 10.55) * 0.31m);
    }
}

public class StandbyDstTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public void NightWindow_OfTheSpringDstDay_IsThreeRealHours()
    {
        // 29.03.2026: 03:00 → 04:00, so 01:00–05:00 local is 3 h (22:00 → 02:00 UTC... 01:00 EET = 23:00 UTC, 05:00 EEST = 02:00 UTC).
        var w = StandbyRule.NightWindows(new DateTimeOffset(2026, 3, 29, 12, 0, 0, TimeSpan.Zero), Ro, 1).Single();
        w.FromUtc.Should().Be(new DateTimeOffset(2026, 3, 28, 23, 0, 0, TimeSpan.Zero));
        w.ToUtc.Should().Be(new DateTimeOffset(2026, 3, 29, 2, 0, 0, TimeSpan.Zero));
        (w.ToUtc - w.FromUtc).Should().Be(TimeSpan.FromHours(3));
    }
}
