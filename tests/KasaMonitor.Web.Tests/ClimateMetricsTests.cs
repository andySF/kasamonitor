using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Web;
using KasaMonitor.Workers;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class ClimateMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static ClimateSensor Sensor(Guid id, string name, string room, string source, bool archived = false) =>
        new(id, source, "AA:BB:CC:DD:EE:FF:00:11", name, room, false, archived, Now, null);

    private static async Task<List<string>> LinesAsync(string marker)
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray()).Split('\n')
            .Where(l => !l.StartsWith('#') && (l.Contains(marker) || l.StartsWith("climate_api_requests_today"))).ToList();
    }

    [Fact]
    public async Task Sensor_BecomesOneSeriesPerFamily_RenameReplacesIt_ArchiveRemovesIt_NoIdsAsLabels()
    {
        var state = new ClimateState(clock: () => Now);
        var id = Guid.NewGuid();
        state.Publish(new ClimateSensorLive(Sensor(id, "Baie test M", "Baie", ClimateSources.Govee), 25.7, 56.7, 80, Now.AddMinutes(-5)));
        state.GoveeCalls.Record();
        ClimateMetrics.Update(state, Now);

        const string l = "sensor=\"Baie test M\",room=\"Baie\",source=\"govee\"";
        var lines = await LinesAsync("Baie test M");
        lines.Should().Contain("climate_temperature_celsius{" + l + "} 25.7");
        lines.Should().Contain("climate_humidity_percent{" + l + "} 56.7");
        lines.Should().Contain("climate_battery_percent{" + l + "} 80");
        lines.Should().Contain("climate_online{" + l + "} 1");
        lines.Should().Contain("climate_last_reading_timestamp_seconds{" + l + "} " + Now.AddMinutes(-5).ToUnixTimeSeconds());
        lines.Should().Contain(x => x.StartsWith("climate_api_requests_today{source=\"govee\"}"));
        lines.Should().Contain("climate_api_requests_today{source=\"openmeteo\"} 0");
        lines.Should().NotContain(x => x.Contains("AA:BB") || x.Contains(id.ToString()));

        // Renamed: the old series disappear, one series per family remains.
        state.UpdateSensor(Sensor(id, "Baie test N", "Baie", ClimateSources.Govee));
        ClimateMetrics.Update(state, Now);
        (await LinesAsync("Baie test M")).Should().NotContain(x => x.Contains("Baie test M"));
        (await LinesAsync("Baie test N")).Count(x => x.StartsWith("climate_temperature_celsius")).Should().Be(1);

        // Stale (older than 6 expected intervals of Govee = 60 min): offline.
        ClimateMetrics.Update(state, Now.AddHours(2));
        (await LinesAsync("Baie test N")).Should().Contain("climate_online{sensor=\"Baie test N\",room=\"Baie\",source=\"govee\"} 0");

        state.UpdateSensor(Sensor(id, "Baie test N", "Baie", ClimateSources.Govee, archived: true));
        ClimateMetrics.Update(state, Now);
        (await LinesAsync("Baie test N")).Should().NotContain(x => x.Contains("Baie test N"));
    }

    [Fact]
    public async Task MissingValues_HaveNoSeries()
    {
        var state = new ClimateState(clock: () => Now);
        state.Publish(new ClimateSensorLive(Sensor(Guid.NewGuid(), "Tur test M", "", ClimateSources.Viessmann), 45, null, null, Now));
        ClimateMetrics.Update(state, Now);

        var lines = await LinesAsync("Tur test M");
        lines.Should().Contain("climate_temperature_celsius{sensor=\"Tur test M\",room=\"\",source=\"viessmann\"} 45");
        lines.Should().NotContain(x => x.StartsWith("climate_humidity_percent") && x.Contains("Tur test M"));
        lines.Should().NotContain(x => x.StartsWith("climate_battery_percent") && x.Contains("Tur test M"));
    }

    [Fact]
    public void Catalog_HasEveryFamilyOfThePackage() =>
        ClimateMetrics.Catalog.Select(c => c.Name).Should().Equal("climate_temperature_celsius", "climate_humidity_percent", "climate_battery_percent",
            "climate_online", "climate_last_reading_timestamp_seconds", "climate_api_requests_today");
}
