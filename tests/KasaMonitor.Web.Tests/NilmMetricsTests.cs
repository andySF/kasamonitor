using System.Text.Json;
using FluentAssertions;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Workers.Nilm;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class NilmMetricsTests
{
    private static async Task<string> ScrapeAsync()
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    [Fact]
    public async Task ExportedAppliances_PowerWhileRunning_EnergyCumulative_SeriesRemovedWhenNoLongerExported()
    {
        NilmMetrics.Update(new NilmMetricSnapshot(new[]
        {
            new NilmMetricItem(1, "ZZ Boiler băi", "boiler", SignatureLabelSource.User, 2000, 750),
            new NilmMetricItem(2, "ZZ Necunoscut #12", "other", SignatureLabelSource.Cluster, null, 40),
        }, 42, 1500));

        var text = await ScrapeAsync();
        text.Should().Contain("kasa_appliance_power_w{appliance=\"ZZ Boiler bai\",category=\"boiler\",source=\"user\"} 2000");
        text.Should().NotContain("kasa_appliance_power_w{appliance=\"ZZ Necunoscut 12\"", "not running");
        text.Should().Contain("kasa_appliance_energy_wh_total{appliance=\"ZZ Boiler bai\",category=\"boiler\"} 750");
        text.Should().Contain("kasa_appliance_energy_wh_total{appliance=\"ZZ Necunoscut 12\",category=\"other\"} 40");
        text.Should().MatchRegex("kasa_nilm_events_total [1-9][0-9]*");
        text.Should().Contain("kasa_nilm_unidentified_wh_total");

        // Renamed (and stopped): the old series disappear, the new name starts with the same cumulative value.
        NilmMetrics.Update(new NilmMetricSnapshot(new[] { new NilmMetricItem(1, "ZZ Boiler", "boiler", SignatureLabelSource.User, null, 760) }, 42, 1500));
        text = await ScrapeAsync();
        text.Should().NotContain("ZZ Boiler bai");
        text.Should().NotContain("ZZ Necunoscut 12");
        text.Should().Contain("kasa_appliance_energy_wh_total{appliance=\"ZZ Boiler\",category=\"boiler\"} 760");
        text.Should().NotContain("kasa_appliance_power_w{appliance=\"ZZ Boiler\"");
        NilmMetrics.Update(NilmMetricSnapshot.Empty);
        (await ScrapeAsync()).Should().NotContain("ZZ Boiler");
    }

    [Fact]
    public async Task TwoSignaturesWithTheSameLabel_WhenOneLeaves_TheSeriesRestartsAtTheLowerSum()
    {
        NilmMetrics.Update(new NilmMetricSnapshot(new[]
        {
            new NilmMetricItem(11, "ZZ Lampă", "lighting", SignatureLabelSource.User, null, 100),
            new NilmMetricItem(12, "ZZ Lampa", "lighting", SignatureLabelSource.User, null, 50),
        }, 0, 0));
        (await ScrapeAsync()).Should().Contain("kasa_appliance_energy_wh_total{appliance=\"ZZ Lampa\",category=\"lighting\"} 150");
        NilmMetrics.Update(new NilmMetricSnapshot(new[] { new NilmMetricItem(11, "ZZ Lampă", "lighting", SignatureLabelSource.User, null, 100) }, 0, 0));
        (await ScrapeAsync()).Should().Contain("kasa_appliance_energy_wh_total{appliance=\"ZZ Lampa\",category=\"lighting\"} 100",
            "recreated (a counter reset) instead of staying stuck at 150");
        NilmMetrics.Update(NilmMetricSnapshot.Empty);
    }

    [Fact]
    public void Label_IsNormalised_AndShort()
    {
        NilmMetrics.Label("Mașină de spălat (baie) #2").Should().Be("Masina de spalat baie 2");
        NilmMetrics.Label(new string('a', 100)).Should().HaveLength(NilmMetrics.MaxLabelLength);
        NilmMetrics.Label("###").Should().Be("appliance");
    }

    [Fact]
    public void OnlyNamedOrFrequentDetectedAppliancesAreExported()
    {
        ApplianceSignature S(int count, SignatureLabelSource src, Guid? plug = null, bool hidden = false) =>
            new(1, "x", "", "other", 30, 0, 1, count, null, src, plug, hidden);
        NilmService.IsExported(S(3, SignatureLabelSource.User)).Should().BeTrue();
        NilmService.IsExported(S(NilmService.MinEventsForMetrics, SignatureLabelSource.Cluster)).Should().BeTrue();
        NilmService.IsExported(S(NilmService.MinEventsForMetrics - 1, SignatureLabelSource.Cluster)).Should().BeFalse();
        NilmService.IsExported(S(50, SignatureLabelSource.User, Guid.NewGuid())).Should().BeFalse("measured by a plug: it has its kasa_* series");
        NilmService.IsExported(S(50, SignatureLabelSource.User, hidden: true)).Should().BeFalse();
    }

    [Fact]
    public void Catalog_HasTheFourFamilies_AndTheDashboardHasTheirRow()
    {
        NilmMetrics.Catalog.Select(c => c.Name).Should().Equal("kasa_appliance_power_w", "kasa_appliance_energy_wh_total",
            "kasa_nilm_events_total", "kasa_nilm_unidentified_wh_total");

        var json = KasaMonitor.Web.Integration.IntegrationConfigGenerator.DashboardJson("prometheus");
        using var doc = JsonDocument.Parse(json);
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray().ToList();
        panels.Should().Contain(p => p.GetProperty("type").GetString() == "row" && p.GetProperty("title").GetString() == "Consumatori detectați");
        panels.Should().Contain(p => p.GetProperty("type").GetString() == "row" && p.GetProperty("title").GetString()!.Contains("Central"),
            "the heating row of 0.6.0 is kept");
        panels.Select(p => p.GetProperty("id").GetInt32()).Should().OnlyHaveUniqueItems();
        var exprs = panels.Where(p => p.TryGetProperty("targets", out _))
            .SelectMany(p => p.GetProperty("targets").EnumerateArray()).Select(t => t.GetProperty("expr").GetString()).ToList();
        foreach (var m in NilmMetrics.Catalog) exprs.Should().Contain(e => e!.Contains(m.Name));
    }
}
