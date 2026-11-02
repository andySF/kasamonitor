using FluentAssertions;
using KasaMonitor.Core.Home;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class HomeSvgTests
{
    [Fact]
    public void Coordinates_AreInvariant_NiceMaxRoundsUp()
    {
        HomeSvg.N(12.3456).Should().Be("12.35");
        HomeSvg.N(-0.5).Should().Be("-0.5");
        HomeSvg.NiceMax(1.56).Should().Be(2);
        HomeSvg.NiceMax(0.21).Should().Be(0.25);
        HomeSvg.NiceMax(7.3).Should().Be(10);
        HomeSvg.NiceMax(0).Should().Be(1);
        HomeSvg.TopRoundedBar(10, 20, 8, 100).Should().StartWith("M10,100V24").And.EndWith("Z").And.NotContain(",,");
    }

    [Fact]
    public void Sankey_NodesKeepAMinimumSlot_DoNotOverlap_RibbonsFillTheSource()
    {
        var flows = EnergyFlowRule.Build(7.8,
            new[] { "Hol mic:1.9", "Camera:1.6", "UPS:1.4", "Frigider:0.9", "Birou:0.7", "Bucatarie:0.6", "TV:0.3", "AC:0" }
                .Select(x => x.Split(':')).Select(x => new FlowItem(x[0], x[0], FlowKind.Plug, double.Parse(x[1], System.Globalization.CultureInfo.InvariantCulture))),
            new[] { new FlowItem("sig-1", "Centrala electric", FlowKind.Detected, 0.4) }).Flows;

        var g = HomeSvg.Sankey(flows, 7.8, 215);

        g.Nodes.Should().HaveCount(flows.Count);
        for (var i = 1; i < g.Nodes.Count; i++)
            g.Nodes[i].Y.Should().BeGreaterThanOrEqualTo(g.Nodes[i - 1].Y + Math.Max(g.Nodes[i - 1].H, 15) + 6 - 1e-9);
        g.Nodes.Last().Y.Should().BeLessThanOrEqualTo(215, "the labels fit the box");
        g.Nodes.Sum(n => n.H).Should().BeApproximately(g.SourceH, 1e-6, "the ribbons fill the meter node exactly");
        g.Nodes[0].SourceY.Should().Be(g.SourceY);
        (g.Nodes[0].H / g.Nodes[3].H).Should().BeApproximately(1.9 / 0.9, 1e-9, "height ∝ kWh");
    }
}

public class HomeFormatTests
{
    [Fact]
    public void Delta_LessIsGood_MoreIsBad_WithPercent()
    {
        HomeFormat.Delta(7.8, 9.1, v => HomeFormat.Kwh(v), "kWh").Should().Be(("▼ 1,30 kWh (−14 %)", "good"), "kWh: 2 decimals under 10");
        HomeFormat.Delta(1.5, 0.7, v => HomeFormat.M3(v), "m³").Should().Be(("▲ 0,80 m³ (+114 %)", "bad"));
        HomeFormat.Delta(10.4, 10, v => HomeFormat.Kwh(v), "kWh").Should().Be(("▲ 0,40 kWh (+4,0 %)", "bad"), "a percent under 10 with one decimal, as on the reports");
        HomeFormat.Kwh(12.34).Should().Be("12,3");
        HomeFormat.Kwh(3.6).Should().Be("3,60");
        HomeFormat.M3(29.4).Should().Be("29,40");
        HomeFormat.W(520.6).Should().Be("521");
        HomeFormat.Delta(1, 1, v => HomeFormat.Kwh(v), "kWh")!.Value.Css.Should().Be("flat");
        HomeFormat.Delta(1, null, v => HomeFormat.Kwh(v), "kWh").Should().BeNull();
        HomeFormat.FirstDays(new DateOnly(2026, 11, 1), 2).Should().Be("1–2 nov");
    }
}

public class HomeMetricsTests
{
    private static async Task<string> ScrapeAsync()
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task StandbyAndBillEstimate_Exported_UnknownValuesHaveNoSeries()
    {
        var p = new MonthProjection(10, 150, 2, 30, true);
        HomeMetrics.Update(new HomeMetricSnapshot(95.5, new BillEstimate(p, 166.5m, p, 100m)));
        var text = await ScrapeAsync();
        text.Should().Contain("kasa_house_standby_w 95.5");
        text.Should().Contain("kasa_bill_estimate_ron{kind=\"electricity\"} 166.5");
        text.Should().Contain("kasa_bill_estimate_ron{kind=\"gas\"} 100");
        text.Should().Contain("kasa_bill_estimate_ron{kind=\"total\"} 266.5");

        HomeMetrics.Update(new HomeMetricSnapshot(null, new BillEstimate(p, 166.5m, null, null)));
        text = await ScrapeAsync();
        text.Should().NotContain("kasa_house_standby_w 95.5");
        text.Should().NotContain("kasa_bill_estimate_ron{kind=\"gas\"}");
        text.Should().Contain("kasa_bill_estimate_ron{kind=\"total\"} 166.5");
        HomeMetrics.Catalog.Select(c => c.Name).Should().Equal("kasa_house_standby_w", "kasa_bill_estimate_ron");
    }
}

public class HomeComparisonTests
{
    private static readonly KasaMonitor.Core.Costing.EnergyPrices Prices = new(1.00m, 10, 0.5m, HasGas: true);
    private static readonly KasaMonitor.Core.Reports.ReportPeriod Today =
        KasaMonitor.Core.Reports.ReportPeriod.Containing(KasaMonitor.Core.Reports.ReportGranularity.Day, new DateOnly(2026, 11, 2));

    private static KasaMonitor.Core.Reports.HouseReport House(double? total, double? previous, bool current = true) =>
        new(Today, Array.Empty<KasaMonitor.Core.Reports.ReportBucket>(), "Contor", Array.Empty<double?>(), Array.Empty<double?>(),
            Array.Empty<double?>(), Array.Empty<KasaMonitor.Core.Metering.RestKind?>(), total, null, null, previous, null,
            Array.Empty<KasaMonitor.Core.Reports.SubMeterTotal>(), null, IsCurrent: current);

    private static HomeGas Gas(double? m3, double? prev) => new(m3, null, null, prev, null, Array.Empty<double?>(), Array.Empty<double?>());

    [Fact]
    public void Today_ComparesLiveWithLive_AndCostOnlyBetweenCompleteTotals()
    {
        var same = new HomeSameTime(DateTimeOffset.UnixEpoch, 4.0, 5.0, 9.0, new GasDayComparison(1.0, 2.0, 3.0, -50));
        var d = new HomePeriod(Today, DateTimeOffset.UnixEpoch, House(4.2, 9.0), null, Gas(1.0, 3.0), same, null);
        var c = HomeComparison.Build(d, isToday: true, Prices);
        c.Kwh.Should().Be(4.2, "the tile shows the day's total");
        c.CurrentKwh.Should().Be(4.0, "the Δ uses the live sum, the same source as yesterday's");
        c.PreviousKwh.Should().Be(5.0);
        c.PreviousM3.Should().Be(2.0);
        c.CurrentCostRon.Should().Be(4.0m + 5.0m);
        c.PreviousCostRon.Should().Be(5.0m + 10.0m);
        c.Versus.Should().Be("față de ieri la aceeași oră");
    }

    [Fact]
    public void Partial_HasNoPreviousElectricity_AndThusNoCostDelta()
    {
        var same = new HomeSameTime(DateTimeOffset.UnixEpoch, 4.0, 5.0, 9.0, new GasDayComparison(1.0, 2.0, 3.0, -50), Partial: true);
        var c = HomeComparison.Build(new HomePeriod(Today, DateTimeOffset.UnixEpoch, House(4.2, 9.0), null, Gas(1.0, 3.0), same, null), true, Prices);
        c.Partial.Should().BeTrue();
        c.PreviousKwh.Should().BeNull();
        c.PreviousCostRon.Should().BeNull("yesterday's gas alone is not a cost to compare with today's electricity + gas");
    }

    [Fact]
    public void GasMissingToday_NoTotal_NoCostDelta_PastPeriod_WholePrevious_InProgress_SameShare()
    {
        var past = new HomePeriod(Today, DateTimeOffset.UnixEpoch, House(4.2, 9.0, current: false), null, Gas(null, 3.0), null, null);
        var c = HomeComparison.Build(past, false, Prices);
        c.CostRon.Should().BeNull("a boiler but no gas today: no total");
        c.CurrentCostRon.Should().BeNull();
        c.PreviousKwh.Should().Be(9.0, "a past period: the whole previous one");
        c.Versus.Should().Be("față de ziua precedentă");

        var week = KasaMonitor.Core.Reports.ReportPeriod.Containing(KasaMonitor.Core.Reports.ReportGranularity.Week, new DateOnly(2026, 11, 2));
        var inProgress = new HomePeriod(week, DateTimeOffset.UnixEpoch, House(20, 70) with { Period = week }, null, Gas(2, 9), null, null, 25, 3);
        var w = HomeComparison.Build(inProgress, false, Prices);
        w.PreviousKwh.Should().Be(25, "the same share of the previous week");
        w.WholePreviousKwh.Should().Be(70);
        w.Versus.Should().Be("față de aceeași parte din săptămâna precedentă");
    }
}

public class HomeSvgAlignTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public void PreviousDay_IsMatchedByLocalHour_OnTheDayAfterTheAutumnDstChange()
    {
        // 26.10.2026 (24 h) after 25.10.2026 (25 h, 03:00 twice): yesterday's value of 05:00 stays on 05:00.
        var today = KasaMonitor.Core.Reports.ReportPeriod.Containing(KasaMonitor.Core.Reports.ReportGranularity.Day, new DateOnly(2026, 10, 26));
        var buckets = today.Buckets(Ro);
        var prevBuckets = today.Previous().Buckets(Ro);
        prevBuckets.Should().HaveCount(25);
        var prev = prevBuckets.Select(b => (double?)b.LocalStart.Hour + (b.RepeatedHour ? 0.5 : 0)).ToList();
        var aligned = HomeSvg.AlignPrevious(buckets, prevBuckets, prev, KasaMonitor.Core.Reports.ReportGranularity.Day);
        aligned.Should().HaveCount(24);
        aligned[5].Should().Be(5, "by hour, not by index (index 5 of yesterday is the repeated 03:00 + 1)");
        aligned[3].Should().Be(3);
        HomeSvg.AxisDecimals(0.25).Should().Be(2);
        HomeSvg.AxisDecimals(0.5).Should().Be(1);
        HomeSvg.AxisDecimals(1.25).Should().Be(2);
        HomeSvg.AxisDecimals(2).Should().Be(0);
    }
}
