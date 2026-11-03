using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Mqtt;
using KasaMonitor.Data;
using KasaMonitor.Web;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using KasaMonitor.Workers.Mqtt;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

/// <summary>0.11, F2: the entity_* families (R5), their series lifecycle and limit; the MQTT integration status; R4 in the registry.</summary>
public class GenericMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private static async Task<List<string>> LinesAsync(string marker)
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray()).Split('\n').Where(l => !l.StartsWith('#') && l.Contains(marker)).ToList();
    }

    private sealed class Rig : IDisposable
    {
        private readonly SqliteConnection _conn = new("Filename=:memory:");
        private readonly ServiceProvider _sp;
        public MqttState State { get; } = new();
        public GenericRecorder Recorder { get; }

        public Rig()
        {
            _conn.Open();
            var services = new ServiceCollection();
            services.AddDbContext<KasaDbContext>(o => o.UseSqlite(_conn));
            services.AddKasaWorkers();
            _sp = services.BuildServiceProvider();
            using (var scope = _sp.CreateScope()) scope.ServiceProvider.GetRequiredService<KasaDbContext>().Database.EnsureCreated();
            Recorder = new GenericRecorder(_sp.GetRequiredService<IServiceScopeFactory>(), State);
        }

        public async Task<KasaMonitor.Core.Abstractions.GenericDevice> AddAsync(string discoveryTopic, string discoveryJson, string? stateTopic = null, string? statePayload = null)
        {
            State.Registry.ApplyDiscovery(MqttDiscoveryParser.Parse(discoveryTopic, discoveryJson), Now);
            if (stateTopic is not null) State.Registry.ApplyMessage(stateTopic, statePayload!, Now);
            var ext = State.Registry.Devices(Now).Last().ExternalId;
            return await Recorder.AdoptAsync(State.Registry.Get(ext, Now)!, Now, CancellationToken.None);
        }

        public void Dispose()
        {
            _sp.Dispose();
            _conn.Dispose();
        }
    }

    private const string Meter = """{"uniq_id":"gm_power","name":"Putere","stat_t":"gm/meter","val_tpl":"{{ value_json.p }}","dev_cla":"power","unit_of_meas":"kW","stat_cla":"measurement","dev":{"ids":"gm-meter","name":"Contor GM"}}""";
    private const string MeterEnergy = """{"uniq_id":"gm_energy","name":"Energie","stat_t":"gm/meter","val_tpl":"{{ value_json.e }}","dev_cla":"energy","unit_of_meas":"kWh","stat_cla":"total_increasing","dev":{"ids":"gm-meter","name":"Contor GM"}}""";
    private const string Lqi = """{"uniq_id":"gm_lqi","name":"LQI","stat_t":"gm/meter","val_tpl":"{{ value_json.l }}","unit_of_meas":"lqi","stat_cla":"measurement","dev":{"ids":"gm-meter","name":"Contor GM"}}""";

    [Fact]
    public async Task FamiliesPerClass_CanonicalUnits_NoIds_RenameAndWithdrawRemoveTheSeries()
    {
        using var rig = new Rig();
        rig.State.Registry.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/gm/power/config", Meter), Now);
        rig.State.Registry.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/gm/energy/config", MeterEnergy), Now);
        rig.State.Registry.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/gm/lqi/config", Lqi), Now);
        rig.State.Registry.ApplyMessage("gm/meter", """{"p":1.5,"e":2.5,"l":120}""", Now);
        var d = await rig.Recorder.AdoptAsync(rig.State.Registry.Get("gm-meter", Now)!, Now, CancellationToken.None);
        var prefs = new Dictionary<string, EntityPrefs> { [$"dev:{d.Id}/gm_lqi"] = new($"dev:{d.Id}/gm_lqi", InMetrics: true) };

        GenericMetrics.Update(rig.State, rig.Recorder, prefs, 500, Now).Should().Be(0);
        const string l = "device=\"Contor GM\",entity=\"{0}\",room=\"\",integration=\"mqtt\"";
        var lines = await LinesAsync("Contor GM");
        lines.Should().Contain("entity_power_watts{" + string.Format(l, "Putere") + "} 1500");
        lines.Should().Contain("entity_energy_wh_total{" + string.Format(l, "Energie") + "} 2500");
        lines.Should().Contain("entity_value{" + string.Format(l, "LQI") + ",unit=\"lqi\"} 120", "an unknown class only through entity_value{unit}");
        lines.Should().Contain("entity_online{device=\"Contor GM\",room=\"\",integration=\"mqtt\"} 1");
        lines.Should().NotContain(x => x.Contains("gm-meter") || x.Contains("gm_power"), "no ids as labels");

        // The counter goes on from the device's value; a reset is skipped.
        rig.State.Registry.ApplyMessage("gm/meter", """{"p":1.5,"e":3.0,"l":120}""", Now.AddMinutes(1));
        GenericMetrics.Update(rig.State, rig.Recorder, prefs, 500, Now.AddMinutes(1));
        (await LinesAsync("Contor GM")).Should().Contain("entity_energy_wh_total{" + string.Format(l, "Energie") + "} 3000");

        await rig.Recorder.UpdateDeviceAsync(d.Id, "Contor nou", "", null, CancellationToken.None);
        GenericMetrics.Update(rig.State, rig.Recorder, prefs, 500, Now.AddMinutes(1));
        (await LinesAsync("Contor GM")).Should().BeEmpty("renamed: the old series are removed");
        (await LinesAsync("Contor nou")).Count(x => x.StartsWith("entity_power_watts", StringComparison.Ordinal)).Should().Be(1);

        GenericMetrics.Update(rig.State, rig.Recorder, prefs, 2, Now.AddMinutes(1)).Should().Be(1, "3 series over a limit of 2");
        rig.State.MetricsWarning.Should().Contain("limita de 2");

        await rig.Recorder.SetArchivedAsync(d.Id, true, CancellationToken.None);
        GenericMetrics.Update(rig.State, rig.Recorder, prefs, 500, Now.AddMinutes(1));
        (await LinesAsync("Contor nou")).Should().BeEmpty("withdrawn: gone from /metrics");
        rig.State.MetricsWarning.Should().BeNull();
    }

    [Fact]
    public async Task ClimateRoutedEntities_AreOnlyInClimateFamilies_DiagnosticsNotByDefault()
    {
        using var rig = new Rig();
        var d = await rig.AddAsync("homeassistant/sensor/gt/t/config",
            """{"uniq_id":"gt_t","name":"Temperatura","stat_t":"gt/s","val_tpl":"{{ value_json.t }}","dev_cla":"temperature","unit_of_meas":"°C","stat_cla":"measurement","dev":{"ids":"gt-sensor","name":"Senzor GT"}}""",
            "gt/s", """{"t":21.5}""");
        GenericMetrics.Update(rig.State, rig.Recorder, new Dictionary<string, EntityPrefs>(), 500, Now);
        var lines = await LinesAsync("Senzor GT");
        lines.Should().NotContain(x => x.StartsWith("entity_temperature_celsius", StringComparison.Ordinal), "routed to climate_*");
        lines.Should().ContainSingle(x => x.StartsWith("entity_online", StringComparison.Ordinal));
        rig.Recorder.Classification(d.Id).TemperatureKey.Should().NotBeNull();
    }

    [Fact]
    public void MqttStatus_OffNotConfiguredErrorWarningOk()
    {
        IntegrationStatusRules.Mqtt(false, true, false, "b:1883", null, 0, 0, 0, 0, null).Level.Should().Be(IntegrationLevel.Off);
        IntegrationStatusRules.Mqtt(true, false, false, ":1883", null, 0, 0, 0, 0, null).Level.Should().Be(IntegrationLevel.NotConfigured);
        var refused = IntegrationStatusRules.Mqtt(true, true, false, "b:1883", "Eroare: autentificare refuzată", 0, 0, 0, 0, null);
        (refused.Level, refused.Text).Should().Be((IntegrationLevel.Error, "neconectat la b:1883: Eroare: autentificare refuzată"));
        IntegrationStatusRules.Mqtt(true, true, true, "b:1883", null, 2, 1, 0, 0, Now).Level.Should().Be(IntegrationLevel.Warning);
        IntegrationStatusRules.Mqtt(true, true, true, "b:1883", null, 2, 2, 0, 1, Now).Text.Should().Contain("1 problemă");
        var ok = IntegrationStatusRules.Mqtt(true, true, true, "b:1883", null, 2, 2, 3, 0, Now);
        (ok.Level, ok.Text).Should().Be((IntegrationLevel.Ok, "conectat la b:1883 · 2/2 dispozitive răspund · 3 descoperite neadăugate"));
    }

    [Fact]
    public void R4_EveryClimateSource_IsARegisteredIntegration_AndMqttHasItsTab()
    {
        foreach (var source in ClimateSources.All)
            Integrations.Get(source).Should().NotBeNull($"the catalog uses the climate source „{source}” as an integration key");
        var mqtt = Integrations.Get(IntegrationKeys.Mqtt)!;
        mqtt.SettingsComponent.Name.Should().Be("MqttSettingsTab");
        mqtt.AddComponent!.Name.Should().Be("MqttAddPanel");
        mqtt.Categories.Should().Contain(DeviceCategory.Generic);
        SettingsTabs.Get("mqtt").Should().NotBeNull();
        GenericMetrics.Catalog.Select(m => m.Name).Should().Contain("entity_energy_wh_total").And.Contain("entity_value");
    }

    [Theory]
    [InlineData(0, "0 entități")]
    [InlineData(1, "1 entitate")]
    [InlineData(6, "6 entități")]
    [InlineData(21, "21 de entități")]
    [InlineData(105, "105 entități")]
    [InlineData(120, "120 de entități")]
    public void Count_RomanianPlural(int n, string expected) => UiFormat.Count(n, "entitate", "entități").Should().Be(expected);
}
