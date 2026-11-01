using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Tests.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Metering;

public class LiveStreamLogicTests
{
    private static readonly Guid Plug = Guid.Parse("6f1c3c1e-0000-0000-0000-0000000000aa");
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private static LiveSample S(int second, double w, double? v = 230) => new(Plug, T0.AddSeconds(second), w, null, v);

    [Fact]
    public void Aggregator_OneMinute_MinAvgMax_AndEnergyAsSumOverSeconds()
    {
        var agg = new LiveMinuteAggregator();
        for (var i = 0; i < 60; i++) agg.Add(S(i, i < 30 ? 1000 : 2000)).Should().BeNull();

        var done = agg.Add(S(60, 500));   // first second of the next minute closes the previous one

        done.Should().NotBeNull();
        done!.MinuteUtc.Should().Be(T0);
        done.SampleCount.Should().Be(60);
        done.MinW.Should().Be(1000);
        done.MaxW.Should().Be(2000);
        done.AvgW.Should().Be(1500);
        done.AvgVoltageV.Should().Be(230);
        done.EnergyWh.Should().BeApproximately(25.0, 1e-9);   // 30 s × 1000 W + 30 s × 2000 W = 90 000 J = 25 Wh
    }

    [Fact]
    public void Aggregator_MissingSeconds_AreNotExtrapolated()
    {
        var agg = new LiveMinuteAggregator();
        foreach (var second in new[] { 0, 1, 2, 30, 59 }) agg.Add(S(second, 3600, v: second == 30 ? null : 230));

        var done = agg.TakeCompleted(T0.AddMinutes(1).AddSeconds(5)).Should().ContainSingle().Subject;

        done.SampleCount.Should().Be(5);
        done.EnergyWh.Should().BeApproximately(5.0, 1e-9);   // Σ W / 3600: 5 received seconds × 3600 W, the 55 missing add nothing
        done.AvgVoltageV.Should().Be(230);                    // a sample without voltage does not pull the average down
    }

    [Fact]
    public void Aggregator_TakeCompleted_KeepsTheCurrentMinuteOpen_AndALateSampleBecomesAPartialAggregate()
    {
        var agg = new LiveMinuteAggregator();
        agg.Add(S(10, 100));
        agg.TakeCompleted(T0.AddSeconds(50)).Should().BeEmpty();            // same minute: still open
        agg.TakeCompleted(T0.AddSeconds(61)).Should().ContainSingle();       // the clock moved on: 18:00 closed (59 samples in real life)
        agg.Add(S(70, 200));
        var closed = agg.Add(S(59, 999));                                    // a slow answer for 18:00:59 arrives after that
        closed!.MinuteUtc.Should().Be(T0.AddMinutes(1));                     // closes the open 18:01 bucket
        var late = agg.TakeAll().Should().ContainSingle().Subject;
        late.MinuteUtc.Should().Be(T0);                                       // a 1-sample partial of 18:00, merged by the repository
        late.SampleCount.Should().Be(1);
    }

    [Fact]
    public void Aggregator_WeightsSamplesByTheSecondsTheyStandFor()
    {
        var agg = new LiveMinuteAggregator();
        agg.Add(new LiveSample(Plug, T0, 100, Seconds: 10));   // a quiet plug read every 10 s
        agg.Add(new LiveSample(Plug, T0.AddSeconds(10), 100, Seconds: 10));
        agg.Add(new LiveSample(Plug, T0.AddSeconds(11), 1000, Seconds: 1));   // kettle: back to 1 s
        var m = agg.TakeAll().Single();
        m.CoveredSeconds.Should().Be(21);
        m.EnergyWh.Should().BeApproximately((100 * 20 + 1000) / 3600.0, 1e-9);
        m.AvgW.Should().BeApproximately(3000 / 21.0, 1e-9);
    }

    [Fact]
    public void Merge_CombinesTwoPartsOfAMinute()
    {
        var a = new LiveMinuteAggregate(Plug, T0, 1000, 1000, 1000, 230, 24 * 1000 / 3600.0, 24, 24);
        var b = new LiveMinuteAggregate(Plug, T0, 2000, 2000, 2000, null, 28 * 2000 / 3600.0, 28, 28);
        var m = a.Merge(b);
        m.SampleCount.Should().Be(52);
        m.CoveredSeconds.Should().Be(52);
        m.MinW.Should().Be(1000);
        m.MaxW.Should().Be(2000);
        m.AvgW.Should().BeApproximately((24 * 1000 + 28 * 2000) / 52.0, 1e-9);
        m.EnergyWh.Should().BeApproximately(80_000 / 3600.0, 1e-9);
        m.AvgVoltageV.Should().Be(230);
    }

    [Theory]
    [InlineData(100, 100.9, false)]    // < 1 W
    [InlineData(100, 101.5, true)]     // > 1 W and > 1 %
    [InlineData(2000, 2015, false)]    // > 1 W but < 1 %
    [InlineData(2000, 2021, true)]
    [InlineData(0, 1.2, true)]
    public void ChangeRule_MoreThanMax1WAnd1Percent(double before, double now, bool changed) =>
        ChangeRule.IsSignificant(before, now).Should().Be(changed);

    [Fact]
    public void ChangeFilter_WritesChangesAndAWitnessEvery60s()
    {
        var f = new ChangeFilter();
        var written = Enumerable.Range(0, 130).Where(i => f.ShouldWrite(S(i, i is >= 30 and < 33 ? 600 + 10 * i : 500))).ToList();
        // first, the three changing seconds and the return to 500 W, then the witness 60 s after the last written row
        written.Should().Equal(0, 30, 31, 32, 33, 93);
    }

    [Fact]
    public void AdaptiveInterval_Plug_1sWhileChanging_ThenSlowsTo10s_BackAt1sOnChangeOrBoost()
    {
        var a = new AdaptiveInterval(fixedRate: false);
        var t = T0;
        var gaps = new List<int>();
        for (var i = 0; i < 80; i++)
        {
            var next = a.Next(t, 40, DateTimeOffset.MinValue);
            gaps.Add(next);
            t = t.AddSeconds(next);
        }
        gaps.Take(60).Should().OnlyContain(g => g == 1);                     // the first 60 s after the first reading
        gaps.Skip(60).Take(10).Should().Equal(2, 3, 4, 5, 6, 7, 8, 9, 10, 10);
        a.Next(t, 45, DateTimeOffset.MinValue).Should().Be(1);                  // a change: back to 1 s at once
        var b = new AdaptiveInterval(fixedRate: false);
        b.Next(T0, 40, DateTimeOffset.MinValue);
        b.Next(T0.AddSeconds(100), 40, T0.AddSeconds(160)).Should().Be(1);     // house event active
        new AdaptiveInterval(fixedRate: true).Next(T0.AddHours(1), 40, DateTimeOffset.MinValue).Should().Be(1);   // main meter
    }

    [Fact]
    public void LiveClock_TicksAtTheSecond_AndRoundsToTheNearestSecond()
    {
        LiveClock.DelayToNextSecond(T0.AddMilliseconds(100)).Should().Be(TimeSpan.FromMilliseconds(900));
        LiveClock.DelayToNextSecond(T0.AddMilliseconds(999)).Should().Be(TimeSpan.FromMilliseconds(1));
        LiveClock.DelayToNextSecond(T0).Should().Be(TimeSpan.FromSeconds(1));
        LiveClock.RoundToSecond(T0.AddMilliseconds(-3)).Should().Be(T0);              // a timer a few ms early keeps its second
        LiveClock.RoundToSecond(T0.AddMilliseconds(490)).Should().Be(T0);             // late by up to ~0.5 s as well
        LiveClock.RoundToSecond(T0.AddMilliseconds(500)).Should().Be(T0.AddSeconds(1));
        // Consecutive ticks a few ms early/late never share a second (the .500-tick design did: .499 and .500).
        var ticks = new[] { -4, 3, -1, 12, -15 }.Select((ms, i) => LiveClock.RoundToSecond(T0.AddSeconds(i).AddMilliseconds(ms))).ToList();
        ticks.Should().OnlyHaveUniqueItems().And.Equal(Enumerable.Range(0, 5).Select(i => T0.AddSeconds(i)));
    }

    [Fact]
    public void SlidingWindow_DropsOlderThanTheSpan_AndCapsThePoints()
    {
        var w = new SlidingWindow<int>(TimeSpan.FromMinutes(10), 600);
        for (var i = 0; i < 700; i++) w.Add(T0.AddSeconds(i), i);

        var items = w.Snapshot();
        items.Should().HaveCount(600);                       // 10 min at 1 s
        items[0].Value.Should().Be(100);
        items[^1].Value.Should().Be(699);

        w.Add(T0.AddSeconds(699 + 3600), 1).Should().BeTrue(); // an hour later: everything older than 10 min is gone
        w.Snapshot().Should().ContainSingle();
        w.Add(T0, 2).Should().BeFalse();                       // out of order: ignored
    }

    [Fact]
    public void SlidingWindow_CapacityWinsOverSpan()
    {
        var w = new SlidingWindow<int>(TimeSpan.FromMinutes(10), 5);
        for (var i = 0; i < 10; i++) w.Add(T0.AddSeconds(i), i);
        w.Snapshot().Select(x => x.Value).Should().Equal(5, 6, 7, 8, 9);
    }

    [Fact]
    public void HouseBalance_RestIsMainMinusOnlinePlugs()
    {
        PlugLiveState Live(PlugRole role, long mw, bool online = true) =>
            new(Guid.NewGuid(), "m", "a", online, new EmeterReading(mw, null, null, null), T0, "", PlugProtocols.Legacy, 0, role);
        var states = new[]
        {
            Live(PlugRole.Plug, 100_000), Live(PlugRole.Plug, 50_000), Live(PlugRole.Plug, 999_000, online: false),
            Live(PlugRole.MainMeter, 1_000_000), Live(PlugRole.SubMeter, 400_000),
        };

        HouseBalance.PlugsW(states).Should().Be(150);
        HouseBalance.RestW(1000, states).Should().Be(850);
        HouseBalance.RestW(null, states).Should().BeNull();
    }

    [Fact]
    public void HouseBalance_MainW_PrefersAFreshStreamSample()
    {
        var polled = new PlugLiveState(Plug, "m", "a", true, new EmeterReading(800_000, null, null, null), T0, "", PlugProtocols.ShellyRpc, 0, PlugRole.MainMeter);
        var fresh = new LiveSample(Plug, T0.AddSeconds(10), 900);

        HouseBalance.MainW(polled, fresh, T0.AddSeconds(12), TimeSpan.FromSeconds(5)).Should().Be(900);
        HouseBalance.MainW(polled, fresh, T0.AddSeconds(30), TimeSpan.FromSeconds(5)).Should().Be(800);   // stale: the 10 s poll
        HouseBalance.MainW(null, null, T0, TimeSpan.FromSeconds(5)).Should().BeNull();
    }

    [Fact]
    public void Shelly_DefaultRoles_MeterChannelsAreSubMeters_PlugsArePlugs()
    {
        var em = ShellySupport.ToPlugs(IPAddress.Loopback, ShellyDeviceInfo.Parse(ShellyFixtures.EmG3DeviceInfo), ShellyFixtures.EmG3Status, null);
        em.Should().HaveCount(2).And.OnlyContain(p => p.Role == PlugRole.SubMeter);

        ShellySupport.DefaultRole(ShellyMeterKind.Em).Should().Be(PlugRole.SubMeter);
        ShellySupport.DefaultRole(ShellyMeterKind.Switch).Should().Be(PlugRole.Plug);
        ShellySupport.DefaultRole(ShellyMeterKind.Pm1).Should().Be(PlugRole.Plug);
        new DiscoveredPlug(IPAddress.Loopback, new PlugInfo("aa", "x", "KP115", "", "", "", true, 0, TimeSpan.Zero)).Role.Should().Be(PlugRole.Plug);
    }

    [Fact]
    public void Shelly_ComponentStatus_ParsesTheOneSecondAnswers()
    {
        // EM1.GetStatus as measured on the EM Gen3 (fw 1.6.99 / 2.1.1).
        var em1 = ShellyFixtures.Parse("""{"id":0,"voltage":229.7,"current":3.156,"act_power":570.3,"aprt_power":728.6,"pf":0.82,"freq":50.0,"calibration":"factory"}""");
        var r = ShellyStatusParser.ParseComponent(ShellyMeterKind.Em1, em1)!;
        r.PowerMw.Should().Be(570_300);
        r.VoltageMv.Should().Be(229_700);
        r.CurrentMa.Should().Be(3_156);
        r.PowerFactor.Should().Be(0.82);
        r.ApparentPowerVa.Should().Be(728.6);
        r.FrequencyHz.Should().Be(50.0);
        r.TotalWh.Should().BeNull();
        r.RelayOn.Should().BeNull();

        var sw = ShellyFixtures.Parse("""{"id":0,"source":"init","output":true,"apower":12.5,"voltage":231.0,"current":0.06,"aenergy":{"total":1234.5}}""");
        var s = ShellyStatusParser.ParseComponent(ShellyMeterKind.Switch, sw)!;
        s.PowerMw.Should().Be(12_500);
        s.RelayOn.Should().BeTrue();

        ShellyStatusParser.ComponentStatusMethod(ShellyMeterKind.Em1).Should().Be("EM1.GetStatus");
        ShellyStatusParser.ComponentStatusMethod(ShellyMeterKind.Switch).Should().Be("Switch.GetStatus");
        ShellyStatusParser.ComponentStatusMethod(ShellyMeterKind.None).Should().BeNull();
    }

    [Fact]
    public void Shelly_Relays_FromStatusAndNotifyStatus()
    {
        ShellyStatusParser.Relays(ShellyFixtures.EmG3Status).Should().Equal((0, true));
        var notify = ShellyFixtures.Parse("""{"ts":1793560000.5,"switch:0":{"id":0,"output":false,"source":"WS_in"}}""");
        ShellyStatusParser.Relays(notify).Should().Equal((0, false));
        ShellyStatusParser.Relays(ShellyFixtures.Parse("""{"ts":1,"em1data:0":{"id":0,"total_act_energy":10}}""")).Should().BeEmpty();
    }
}
