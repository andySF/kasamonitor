using System.Text.Json;
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Web.Integration;
using KasaMonitor.Workers;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class ShellyMetricsTests
{
    private static async Task<string> ScrapeAsync()
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<string> LinesFor(string scrape, string mac) =>
        scrape.Split('\n').Where(l => !l.StartsWith('#') && l.Contains($"mac=\"{mac}\"")).ToList();

    [Fact]
    public async Task ShellyMeterChannels_ExportExtraMetrics_AndChannelMacLabels()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);

        var ch0 = new EmeterReading(-417_500, 231_200, 3_155, 43, PowerFactor: 0.81, ApparentPowerVa: 731.4, FrequencyHz: 50.0,
            ReturnedWh: 68.6, TemperatureC: 47.0, RelayOn: true);
        var ch1 = new EmeterReading(0, 231_200, 20, 0, PowerFactor: 0.0, ApparentPowerVa: 4.7, FrequencyHz: 50.0, ReturnedWh: 0);
        bc.Publish(new PlugLiveState(Guid.NewGuid(), "sh00000001", "Tablou ch0", true, ch0, DateTimeOffset.UtcNow, "S3EM-002CXCEU", PlugProtocols.ShellyRpc, 0));
        bc.Publish(new PlugLiveState(Guid.NewGuid(), "sh00000001", "Tablou ch1", true, ch1, DateTimeOffset.UtcNow, "S3EM-002CXCEU", PlugProtocols.ShellyRpc, 1));

        var scrape = await ScrapeAsync();
        var c0 = LinesFor(scrape, "sh00000001");
        c0.Should().Contain(l => l.StartsWith("kasa_power_load{") && l.Contains("protocol=\"shelly-rpc\"") && l.Contains("model=\"S3EM-002CXCEU\"") && l.EndsWith(" -417.5"));
        c0.Should().Contain(l => l.StartsWith("kasa_power_factor{") && l.EndsWith(" 0.81"));
        c0.Should().Contain(l => l.StartsWith("kasa_apparent_power_va{") && l.EndsWith(" 731.4"));
        c0.Should().Contain(l => l.StartsWith("kasa_frequency_hz{") && l.EndsWith(" 50"));
        c0.Should().Contain(l => l.StartsWith("kasa_energy_returned_wh_total{") && l.EndsWith(" 68.6"));
        c0.Should().Contain(l => l.StartsWith("kasa_device_temperature_c{") && l.EndsWith(" 47"));
        c0.Should().Contain(l => l.StartsWith("kasa_relay_state{") && l.EndsWith(" 1"));

        var c1 = LinesFor(scrape, "sh00000001-1");
        c1.Should().Contain(l => l.StartsWith("kasa_power_load{") && l.Contains("alias=\"Tablou ch1\""));
        c1.Should().NotContain(l => l.StartsWith("kasa_relay_state{"));          // no relay on channel 1
        c1.Should().NotContain(l => l.StartsWith("kasa_device_temperature_c{"));
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task KasaPlugs_DoNotGetShellyOnlyMetrics()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);

        bc.Publish(new PlugLiveState(Guid.NewGuid(), "m0000000901", "Birou", true,
            new EmeterReading(50_000, 230_000, 200, 100), DateTimeOffset.UtcNow, "KP115(EU)", PlugProtocols.Legacy));

        var lines = LinesFor(await ScrapeAsync(), "m0000000901");
        lines.Should().Contain(l => l.StartsWith("kasa_relay_state{") && l.EndsWith(" 1"));   // unchanged legacy behaviour
        lines.Should().NotContain(l => l.StartsWith("kasa_power_factor{") || l.StartsWith("kasa_frequency_hz{")
                                       || l.StartsWith("kasa_energy_returned_wh_total{") || l.StartsWith("kasa_device_temperature_c{"));
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Catalog_ListsTheNewMetricsForTheIntegrationPage()
    {
        KasaMetrics.Catalog.Select(c => c.Name).Should().Contain(new[]
        {
            "kasa_power_factor", "kasa_apparent_power_va", "kasa_frequency_hz", "kasa_energy_returned_wh_total", "kasa_device_temperature_c",
        });
    }

    /// <summary>The Grafana totals count only plugs (role="plug"); the house and the sub-meters have their own rows.</summary>
    [Fact]
    public void Dashboard_TotalsUseRolePlug_AndMetersHaveTheirOwnRows()
    {
        using var doc = JsonDocument.Parse(DeployFiles.Read(DeployFiles.Dashboard));
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray().ToList();
        string Title(JsonElement p) => p.GetProperty("title").GetString()!;
        IEnumerable<string> Exprs(JsonElement p) => p.TryGetProperty("targets", out var t)
            ? t.EnumerateArray().Select(x => x.GetProperty("expr").GetString()!) : Enumerable.Empty<string>();
        const string plug = "role=\"plug\"";

        foreach (var title in new[] { "Power now", "Energy (24 h)", "Cost (24 h, $currency)", "Energy (30 days)", "Cost (30 days, $currency)",
                                      "Energy per day (30 days)", "Plugs online / total" })
        foreach (var expr in Exprs(panels.Single(p => Title(p) == title)))
        {
            expr.Should().Contain(plug, title);
            expr.Replace("kasa_power_load{" + plug + "}", "").Replace("kasa_power_total{" + plug + "}", "")
                .Replace("kasa_online{" + plug + "}", "")
                .Should().NotContain("kasa_power_load").And.NotContain("kasa_power_total").And.NotContain("kasa_online",
                    "every series in a total must carry the filter ({0})", title);
        }

        doc.RootElement.GetRawText().Should().NotContain("S3EM", "the model-based meter filter of 0.3.0 is replaced by the role");
        panels.Where(p => p.GetProperty("type").GetString() == "row").Select(Title).Should().Contain(new[] { "Contor general", "Sub-contoare" });
        var all = panels.SelectMany(Exprs).ToList();
        all.Should().Contain(e => e.Contains("kasa_power_load{role=\"main\"}") && e.Contains("kasa_power_load{role=\"plug\"}"));   // rest of the house
        all.Should().Contain(e => e.Contains("kasa_voltage{role=\"main\"}"));
        all.Should().Contain(e => e.Contains("kasa_power_factor{role=\"main\"}"));
        all.Should().Contain(e => e.Contains("kasa_power_total{role=\"main\"}"));
        all.Should().Contain(e => e.Contains("kasa_power_load{role=\"sub\"}"));
        var voltage = panels.Single(p => Title(p) == "Grid voltage");
        voltage.GetProperty("fieldConfig").GetProperty("defaults").GetProperty("thresholds").GetProperty("steps").EnumerateArray()
            .Select(x => x.GetProperty("value").ValueKind == JsonValueKind.Null ? (double?)null : x.GetProperty("value").GetDouble())
            .Should().Equal(null, 207, 253);
    }

    [Fact]
    public async Task EverySeries_CarriesTheRoleLabel_AndARoleChangeMovesTheSeries()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        var reading = new EmeterReading(900_000, 230_000, 4_000, 100, PowerFactor: 0.9, RelayOn: true);
        bc.Publish(new PlugLiveState(id, "sh00000444", "Casa", true, reading, DateTimeOffset.UtcNow, "S3EM-002CXCEU", PlugProtocols.ShellyRpc, 0, PlugRole.SubMeter));
        bc.Publish(new PlugLiveState(Guid.NewGuid(), "m0000000445", "Birou", true, new EmeterReading(50_000, 230_000, 200, 10),
            DateTimeOffset.UtcNow, "KP115(EU)", PlugProtocols.Legacy));

        var scrape = await ScrapeAsync();
        LinesFor(scrape, "sh00000444").Should().NotBeEmpty().And.OnlyContain(l => l.Contains("role=\"sub\""));
        LinesFor(scrape, "m0000000445").Should().NotBeEmpty().And.OnlyContain(l => l.Contains("role=\"plug\""));
        KasaMetrics.LabelNames.Should().Equal("alias", "mac", "model", "protocol", "role");

        bc.Publish(new PlugLiveState(id, "sh00000444", "Casa", true, reading, DateTimeOffset.UtcNow, "S3EM-002CXCEU", PlugProtocols.ShellyRpc, 0, PlugRole.MainMeter));

        var after = LinesFor(await ScrapeAsync(), "sh00000444");
        after.Should().Contain(l => l.StartsWith("kasa_power_load{") && l.Contains("role=\"main\"") && l.EndsWith(" 900"));
        after.Should().NotContain(l => l.Contains("role=\"sub\""));   // old series removed, not frozen
        await metrics.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReadingWithoutTotal_DoesNotMoveTheEnergyCounter()
    {
        var bc = new PlugStateBroadcaster();
        using var metrics = new KasaMetrics(bc);
        await metrics.StartAsync(CancellationToken.None);
        var id = Guid.NewGuid();
        void Publish(long? total) => bc.Publish(new PlugLiveState(id, "sh00000777", "Plug S", true,
            new EmeterReading(5_000, null, null, total), DateTimeOffset.UtcNow, "SNPL-00112EU", PlugProtocols.ShellyRpc));

        Publish(100);
        Publish(null);   // energy component missing in this answer
        Publish(105);

        LinesFor(await ScrapeAsync(), "sh00000777").Should().Contain(l => l.StartsWith("kasa_power_total{") && l.EndsWith(" 105"));
        await metrics.StopAsync(CancellationToken.None);
    }
}
