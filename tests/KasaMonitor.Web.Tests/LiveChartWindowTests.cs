using System.Text.Json;
using ApexCharts;
using ApexCharts.Internal;
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Web.Ui;
using Xunit;

namespace KasaMonitor.Web.Tests;

/// <summary>The sliding window behind each line of the live chart of /contor (logic only, not the rendering).</summary>
public class LiveChartWindowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KeepsTenMinutes_AtMost600Points_InTheSameListInstance()
    {
        var w = new LiveChartWindow();
        var list = w.Points;

        for (var i = 0; i < 900; i++) w.Add(T0.AddSeconds(i), i).Should().BeTrue();

        w.Points.Should().BeSameAs(list);   // the chart keeps its Items reference; only UpdateSeriesAsync is called
        w.Points.Should().HaveCount(LiveChartWindow.MaxPoints);
        w.Points[0].TimeUtc.Should().Be(T0.AddSeconds(300).UtcDateTime);
        w.Points[^1].PowerW.Should().Be(899);
        w.Points[^1].TimeUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Gaps_ShrinkTheWindowByTime_AndOutOfOrderPointsAreIgnored()
    {
        var w = new LiveChartWindow();
        w.AddRange(new (DateTimeOffset, decimal?)[] { (T0, 0), (T0.AddSeconds(1), 1), (T0.AddSeconds(2), 2) });
        w.Points.Select(p => p.PowerW).Should().Equal(0, 1, 2);

        w.Add(T0.AddSeconds(1), 9).Should().BeFalse();
        w.Add(T0.AddMinutes(10).AddSeconds(1.5), 1234.6m).Should().BeTrue();   // 10 min later: the first two are gone

        w.Points.Select(p => p.PowerW).Should().Equal(2, 1234.6m);
    }

    [Fact]
    public void Null_IsKeptAsAPoint()
    {
        var w = new LiveChartWindow();
        w.Add(T0, null).Should().BeTrue();
        w.Points.Should().ContainSingle().Which.PowerW.Should().BeNull();
    }
}

/// <summary>The common 1 s grid of the live chart: one x for all the lines, the value held, a gap after 30 s.</summary>
public class LiveChartGridTests
{
    private static readonly Guid Meter = Guid.NewGuid();
    private static readonly Guid PlugA = Guid.NewGuid();
    private static readonly Guid PlugB = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private static LiveChartGrid NewGrid() => new(new[] { Meter, PlugA, PlugB });

    private static LiveSample Sample(Guid plug, DateTimeOffset ts, double w) => new(plug, ts, w);

    private static decimal?[] Values(LiveChartGrid g, Guid id) => g.Points(id).Select(p => p.PowerW).ToArray();

    private static void AllSeriesShareTheSameX(LiveChartGrid g)
    {
        var xs = g.Points(Meter).Select(p => p.TimeUtc).ToList();
        g.Points(PlugA).Select(p => p.TimeUtc).Should().Equal(xs);
        g.Points(PlugB).Select(p => p.TimeUtc).Should().Equal(xs);
    }

    [Fact]
    public void Tick_AddsTheSameWholeSecondToEverySeries_WhoeverReported()
    {
        var g = NewGrid();

        g.Observe(Sample(Meter, T0.AddSeconds(0.2), 500)).Should().BeTrue();
        g.Tick(T0.AddSeconds(1.7)).Should().BeTrue();
        g.Observe(Sample(PlugA, T0.AddSeconds(2.4), 60)).Should().BeTrue();
        g.Tick(T0.AddSeconds(2.7)).Should().BeTrue();

        AllSeriesShareTheSameX(g);
        g.Points(Meter).Select(p => p.TimeUtc).Should().Equal(T0.AddSeconds(1).UtcDateTime, T0.AddSeconds(2).UtcDateTime);
        g.Points(Meter)[0].TimeUtc.Kind.Should().Be(DateTimeKind.Utc);
        Values(g, Meter).Should().Equal(500m, 500m);
        Values(g, PlugA).Should().Equal(null, 60m);   // no reading yet: a gap, not a zero
        Values(g, PlugB).Should().Equal(new decimal?[] { null, null });
    }

    [Fact]
    public void Tick_TwiceInOneSecond_AddsOnePoint()
    {
        var g = NewGrid();

        g.Tick(T0.AddSeconds(1.01)).Should().BeTrue();
        g.Tick(T0.AddSeconds(1.99)).Should().BeFalse();

        g.Points(Meter).Should().HaveCount(1);
        AllSeriesShareTheSameX(g);
    }

    [Fact]
    public void ThePlugSeriesKeepsTheirOwnValues_APlugSampleNeverLandsInTheMeterSeries()
    {
        var g = NewGrid();
        g.Observe(Sample(Meter, T0, 2000));
        g.Observe(Sample(PlugA, T0, 760));   // the reported bug: the meter line jumped to the plug's 760 W
        g.Tick(T0.AddSeconds(1));

        g.Observe(Sample(PlugA, T0.AddSeconds(1.5), 770));
        g.Tick(T0.AddSeconds(2));

        Values(g, Meter).Should().Equal(2000m, 2000m);
        Values(g, PlugA).Should().Equal(760m, 770m);
    }

    [Fact]
    public void Observe_RoundsToOneDecimal_IgnoresOutOfOrderAndForeignSamples()
    {
        var g = NewGrid();

        g.Observe(Sample(PlugA, T0.AddSeconds(5), 1234.56)).Should().BeTrue();
        g.Observe(Sample(PlugA, T0.AddSeconds(4), 9)).Should().BeFalse();
        g.Observe(Sample(Guid.NewGuid(), T0.AddSeconds(5), 9)).Should().BeFalse();
        g.Tick(T0.AddSeconds(6));

        Values(g, PlugA).Should().Equal(1234.6m);
    }

    [Fact]
    public void TheLastValueIsHeldFor30Seconds_ThenAGap_ThenItComesBack()
    {
        var g = NewGrid();
        g.Observe(Sample(PlugA, T0, 60));

        g.Tick(T0.AddSeconds(30));   // exactly 30 s old: still held
        g.Tick(T0.AddSeconds(31));   // older than 30 s: a gap
        g.Observe(Sample(PlugA, T0.AddSeconds(31.5), 80));
        g.Tick(T0.AddSeconds(32));

        Values(g, PlugA).Should().Equal(60m, null, 80m);
        AllSeriesShareTheSameX(g);
    }

    [Fact]
    public void KeepsTenMinutes_AtMost600Points_InTheSameListInstances()
    {
        var g = NewGrid();
        var lists = new[] { g.Points(Meter), g.Points(PlugA), g.Points(PlugB) };

        for (var i = 0; i < 900; i++)
        {
            g.Observe(Sample(Meter, T0.AddSeconds(i), i));
            g.Tick(T0.AddSeconds(i));
        }

        g.Points(Meter).Should().BeSameAs(lists[0]);
        g.Points(PlugA).Should().BeSameAs(lists[1]);
        g.Points(PlugB).Should().BeSameAs(lists[2]);
        g.Points(Meter).Should().HaveCount(LiveChartWindow.MaxPoints);
        g.Points(Meter)[0].TimeUtc.Should().Be(T0.AddSeconds(300).UtcDateTime);
        g.Points(Meter)[^1].PowerW.Should().Be(899);
        AllSeriesShareTheSameX(g);
    }

    [Fact]
    public void Seed_ResamplesOnTheSameGrid_WithTheSameRule()
    {
        var g = NewGrid();
        var recent = new Dictionary<Guid, LiveSample[]>
        {
            [Meter] = new[] { Sample(Meter, T0.AddSeconds(1.4), 2), Sample(Meter, T0, 1), Sample(Meter, T0.AddSeconds(2), 3) },   // unordered: sorted first
            [PlugA] = new[] { Sample(PlugA, T0.AddSeconds(1), 60) },
        };

        g.Seed(id => recent.GetValueOrDefault(id) ?? Array.Empty<LiveSample>(), T0.AddSeconds(33.5));

        AllSeriesShareTheSameX(g);
        g.Points(Meter).Should().HaveCount(34);   // 18:00:00 … 18:00:33, whole seconds
        g.Points(Meter)[0].TimeUtc.Should().Be(T0.UtcDateTime);
        g.Points(Meter)[^1].TimeUtc.Should().Be(T0.AddSeconds(33).UtcDateTime);
        var meter = Values(g, Meter);
        meter[..4].Should().Equal(1m, 1m, 3m, 3m);   // at 2 s the newest sample wins over the one of 1.4 s
        meter[32].Should().Be(3m);                   // 30 s after the last sample: still held
        meter[33].Should().BeNull();                 // 31 s after: a gap
        var plug = Values(g, PlugA);
        plug[0].Should().BeNull();                   // before its first sample
        plug[1].Should().Be(60m);
        plug[31].Should().Be(60m);
        plug[32].Should().BeNull();
        Values(g, PlugB).Should().OnlyContain(v => v == null);
    }

    [Fact]
    public void Seed_StartsAtTheFirstWholeSecondAfterTheSample_AndAtMostTenMinutesBack()
    {
        var late = NewGrid();
        late.Seed(id => id == Meter ? new[] { Sample(Meter, T0.AddSeconds(0.5), 1) } : Array.Empty<LiveSample>(), T0.AddSeconds(2));
        late.Points(Meter).Select(p => p.TimeUtc).Should().Equal(T0.AddSeconds(1).UtcDateTime, T0.AddSeconds(2).UtcDateTime);

        var full = NewGrid();
        var samples = Enumerable.Range(0, 900).Select(i => Sample(Meter, T0.AddSeconds(i), i)).ToArray();
        full.Seed(id => id == Meter ? samples : Array.Empty<LiveSample>(), T0.AddSeconds(899));
        full.Points(Meter).Should().HaveCount(LiveChartWindow.MaxPoints);
        full.Points(Meter)[0].TimeUtc.Should().Be(T0.AddSeconds(300).UtcDateTime);
        full.Points(Meter)[0].PowerW.Should().Be(300);
        AllSeriesShareTheSameX(full);
    }

    [Fact]
    public void Seed_WithoutSamples_OnlyTheCurrentSecond_AndASampleOfTheCurrentSecondFeedsTheFirstTick()
    {
        var empty = NewGrid();
        empty.Seed(_ => Array.Empty<LiveSample>(), T0);
        empty.Points(Meter).Should().ContainSingle().Which.PowerW.Should().BeNull();   // only the current second: all aligned

        var g = NewGrid();
        g.Seed(id => id == PlugA ? new[] { Sample(PlugA, T0.AddSeconds(5.6), 60) } : Array.Empty<LiveSample>(), T0.AddSeconds(5.9));
        g.Tick(T0.AddSeconds(6.1));
        Values(g, PlugA)[^1].Should().Be(60m);
    }

    [Fact]
    public void Tick_AfterSeed_InTheSameSecond_AddsNoSecondPoint()
    {
        var g = NewGrid();
        g.Seed(id => id == Meter ? new[] { Sample(Meter, T0, 1) } : Array.Empty<LiveSample>(), T0.AddSeconds(5.1));
        var count = g.Points(Meter).Count;

        g.Tick(T0.AddSeconds(5.9)).Should().BeFalse();

        g.Points(Meter).Should().HaveCount(count);
    }

    [Fact]
    public void Pending_ATickDuringASendLosesNothing_AndStaysAligned()
    {
        var g = NewGrid();
        g.Seed(id => id == Meter ? new[] { Sample(Meter, T0, 1) } : Array.Empty<LiveSample>(), T0);
        g.Pending().Values.Should().OnlyContain(p => p.Count == 0);   // the seed goes with the first render

        g.Tick(T0.AddSeconds(1));
        var firstSend = g.Pending();
        firstSend[Meter].Select(p => p.TimeUtc).Should().Equal(T0.AddSeconds(1).UtcDateTime);

        // the send is in progress: two more ticks, the second and the third are not lost
        g.Observe(Sample(PlugA, T0.AddSeconds(1.5), 60));
        g.Tick(T0.AddSeconds(2));
        g.Tick(T0.AddSeconds(3));
        g.MarkSent(firstSend[Meter][^1].TimeUtc);

        var next = g.Pending();
        var expected = new[] { T0.AddSeconds(2).UtcDateTime, T0.AddSeconds(3).UtcDateTime };
        foreach (var id in new[] { Meter, PlugA, PlugB }) next[id].Select(p => p.TimeUtc).Should().Equal(expected);
        next[PlugA].Select(p => p.PowerW).Should().Equal(60m, 60m);

        g.MarkSent(expected[^1]);
        g.Pending().Values.Should().OnlyContain(p => p.Count == 0);
    }

    [Fact]
    public void Pending_AFailedSendIsNotMarked_SoItGoesWithTheNextOne()
    {
        var g = NewGrid();
        g.Tick(T0);
        g.MarkSent(null);   // nothing reached the chart
        g.Tick(T0.AddSeconds(1));

        g.Pending()[Meter].Select(p => p.TimeUtc).Should().Equal(T0.UtcDateTime, T0.AddSeconds(1).UtcDateTime);
    }

    [Fact]
    public void FullPush_IsDueEveryTrimEveryTicks_AndStaysDueUntilItSucceeded()
    {
        var g = NewGrid();
        for (var i = 0; i < LiveChartGrid.TrimEvery - 1; i++) g.Tick(T0.AddSeconds(i));
        g.FullPushDue.Should().BeFalse();

        g.Tick(T0.AddSeconds(LiveChartGrid.TrimEvery));
        g.FullPushDue.Should().BeTrue();

        g.Tick(T0.AddSeconds(LiveChartGrid.TrimEvery + 1));   // a send in progress caught that tick
        g.FullPushDue.Should().BeTrue();

        var last = T0.AddSeconds(LiveChartGrid.TrimEvery + 1).UtcDateTime;
        g.MarkSent(last, fullPush: true);
        g.FullPushDue.Should().BeFalse();
        g.Pending().Values.Should().OnlyContain(p => p.Count == 0);
    }

    [Fact]
    public void UniqueNames_KeepsTheNames_AndSeparatesEqualOnes()
    {
        LiveChartGrid.UniqueNames(new[] { "Casă (W)", "Ups (W)", "Tv (W)" }).Should().Equal("Casă (W)", "Ups (W)", "Tv (W)");
        LiveChartGrid.UniqueNames(new[] { "Ups (W)", "Ups (W)", "Ups (W) #2", "Ups (W)" })
            .Should().OnlyHaveUniqueItems().And.HaveCount(4).And.StartWith("Ups (W)");
    }

    [Fact]
    public void Colours_FollowThePosition_AndCycle_TheMeterKeepsItsOwn()
    {
        LiveChartColors.Main.Should().Be("#0072B2");
        Enumerable.Range(0, 7).Select(LiveChartColors.Overlay).Should()
            .Equal("#E69F00", "#009E73", "#D55E00", "#CC79A7", "#56B4E9", "#F0E442", "#E69F00");
    }

    [Fact]
    public void ANullValueIsSerializedAsANullY_WhichIsAGapInApexCharts()
    {
        // Without "y" apexcharts.js would skip the value but keep the x, shifting the rest of the line.
        var point = new DataPoint<LivePoint> { X = T0.UtcDateTime, Y = null };

        var json = JsonSerializer.Serialize(point, ChartSerializer.GetOptions());

        json.Should().Contain("\"y\":null");
    }
}
