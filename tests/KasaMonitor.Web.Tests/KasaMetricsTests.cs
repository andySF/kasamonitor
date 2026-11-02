using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Workers;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class KasaMetricsTests
{
    private static async Task<string> ScrapeAsync()
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static IEnumerable<string> LinesFor(string scrape, string mac) =>
        scrape.Split('\n').Where(l => !l.StartsWith('#') && l.Contains($"mac=\"{mac}\""));

    [Fact]
    public async Task Series_CarryModelAndProtocolLabels()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);

        bc.Publish(new PlugLiveState(Guid.NewGuid(), "m0000000001", "Birou", true,
            new EmeterReading(50_000, 230_000, 200, 100), DateTimeOffset.UtcNow, "KP115(EU)", PlugProtocols.Legacy));

        var lines = LinesFor(await ScrapeAsync(), "m0000000001").ToList();
        lines.Should().Contain(l => l.StartsWith("kasa_power_load{") && l.Contains("alias=\"Birou\"")
                                    && l.Contains("model=\"KP115\"") && l.Contains("protocol=\"legacy\"") && l.EndsWith(" 50"));
        lines.Should().Contain(l => l.StartsWith("kasa_voltage{") && l.EndsWith(" 230"));
        lines.Should().Contain(l => l.StartsWith("kasa_online{") && l.EndsWith(" 1"));
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SingleAppliancePlug_GetsItsOwnApplianceSeries_TheKasaSeriesKeepTheirLabels()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var reading = new EmeterReading(48_000, 230_000, 210, 100);

        bc.Publish(new PlugLiveState(id, "aabbccddee10", "Priza AC", true, reading, DateTimeOffset.UtcNow, "KP115(EU)", ApplianceName: "AC sufragerie"));
        var scrape = await ScrapeAsync();
        var kasa = LinesFor(scrape, "aabbccddee10").ToList();
        kasa.Should().Contain(l => l.StartsWith("kasa_power_load{") && l.Contains("alias=\"Priza AC\"") && l.EndsWith(" 48"));
        kasa.Should().NotContain(l => l.Contains("appliance="), "the kasa_* series do not change (no duplicate series)");
        scrape.Split('\n').Should().Contain("kasa_appliance_plug_power_w{appliance=\"AC sufragerie\",alias=\"Priza AC\"} 48");

        // Renamed appliance: the old series goes; cleared flag: no appliance series at all, the kasa_* one stays.
        bc.Publish(new PlugLiveState(id, "aabbccddee10", "Priza AC", true, reading, DateTimeOffset.UtcNow, "KP115(EU)", ApplianceName: "Aer condiționat"));
        scrape = await ScrapeAsync();
        scrape.Should().Contain("kasa_appliance_plug_power_w{appliance=\"Aer conditionat\",alias=\"Priza AC\"} 48").And.NotContain("appliance=\"AC sufragerie\"");
        bc.Publish(new PlugLiveState(id, "aabbccddee10", "Priza AC", true, reading, DateTimeOffset.UtcNow, "KP115(EU)"));
        scrape = await ScrapeAsync();
        scrape.Should().NotContain("alias=\"Priza AC\"}").And.NotContain("appliance=\"Aer conditionat\"");
        LinesFor(scrape, "aabbccddee10").Should().Contain(l => l.StartsWith("kasa_power_load{"));
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task KlapPlug_ExportsVoltageAndCurrentWhenReported_AndOmitsThemWhenNot()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);

        bc.Publish(new PlugLiveState(Guid.NewGuid(), "m0000000002", "Frigider", true,
            new EmeterReading(109_269, 231_150, 754, 458), DateTimeOffset.UtcNow, "P115", PlugProtocols.Klap));
        bc.Publish(new PlugLiveState(Guid.NewGuid(), "m0000000003", "Fara emeter", true,
            new EmeterReading(1_000, null, null, 1), DateTimeOffset.UtcNow, "P115", PlugProtocols.Klap));

        var scrape = await ScrapeAsync();
        var klap = LinesFor(scrape, "m0000000002").ToList();
        klap.Should().Contain(l => l.StartsWith("kasa_current{") && l.Contains("protocol=\"klap\"") && l.EndsWith(" 0.754"));
        klap.Should().Contain(l => l.StartsWith("kasa_voltage{") && l.EndsWith(" 231.15"));
        klap.Should().Contain(l => l.StartsWith("kasa_power_total{") && l.EndsWith(" 458"));

        var noEmeter = LinesFor(scrape, "m0000000003").ToList();
        noEmeter.Should().Contain(l => l.StartsWith("kasa_power_load{"));
        noEmeter.Should().NotContain(l => l.StartsWith("kasa_voltage{") || l.StartsWith("kasa_current{"));
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RemovedPlug_DisappearsFromMetrics()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        bc.Publish(new PlugLiveState(id, "m0000000004", "Sufeagerie AC", false, null, DateTimeOffset.UtcNow, "KP115(EU)"));
        LinesFor(await ScrapeAsync(), "m0000000004").Should().NotBeEmpty();

        bc.Remove(id);

        LinesFor(await ScrapeAsync(), "m0000000004").Should().BeEmpty();
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RenamedPlug_DropsOldSeries()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var reading = new EmeterReading(1_000, 230_000, 10, 5);
        bc.Publish(new PlugLiveState(id, "m0000000005", "Vechi", true, reading, DateTimeOffset.UtcNow, "KP115"));
        bc.Publish(new PlugLiveState(id, "m0000000005", "Nou", true, reading, DateTimeOffset.UtcNow, "KP115"));

        var lines = LinesFor(await ScrapeAsync(), "m0000000005").ToList();
        lines.Should().NotContain(l => l.Contains("alias=\"Vechi\""));
        lines.Should().Contain(l => l.Contains("alias=\"Nou\""));
        await metrics.StopAsync(CancellationToken.None);
    }
}
