using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using KasaMonitor.Core.Devices;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using KasaMonitor.Workers.Mqtt;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>
/// The app on a temporary database without background workers (no broker): the MQTT messages of the documentation fixtures
/// are handed to the worker's consumer directly (the fake-transport path of the pages).
/// </summary>
public sealed class MqttFactory : WebApplicationFactory<Program>
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-mqtt-{Guid.NewGuid():N}");
    public long PlugDeviceId { get; private set; }
    public long SensorDeviceId { get; private set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _seeded;

    public MqttFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("KasaMonitor:DbPath", Path.Combine(Dir, "kasa.db"));
        builder.UseSetting("KasaMonitor:MqttHost", "broker.test");
        builder.UseSetting("KasaMonitor:MqttPasswordFile", Path.Combine(Dir, "nu-exista.password"));
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    public static IEnumerable<(string Topic, string Payload, bool Retain)> Fixture(params string[] files) => files.SelectMany(f =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "mqtt", f)).Where(l => l.Trim().Length > 0).Select(l =>
        {
            var o = JsonNode.Parse(l)!.AsObject();
            var p = o["payload"];
            return (o["topic"]!.GetValue<string>(), p is JsonValue v && v.TryGetValue<string>(out var s) ? s : p!.ToJsonString(), o["retain"]?.GetValue<bool>() ?? false);
        }));

    public async Task SeedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_seeded) return;
            var worker = Services.GetRequiredService<MqttDiscoveryWorker>();
            foreach (var (topic, payload, retain) in Fixture("z2m-discovery.jsonl", "esphome-discovery.jsonl", "ha-discovery.jsonl", "z2m-states.jsonl", "esphome-states.jsonl"))
                await worker.ProcessAsync(new MqttMessage(topic, payload, retain), CancellationToken.None);
            PlugDeviceId = (await worker.AddAsync("zigbee2mqtt_0xaabbccddee000002", CancellationToken.None)).Id;
            SensorDeviceId = (await worker.AddAsync("zigbee2mqtt_0xaabbccddee000001", CancellationToken.None)).Id;
            _seeded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>0.11, F2: the MQTT tab, „Adaugă” and „Descoperite”, the generic device's page and row, „Alte dispozitive” in /history, the metrics catalog.</summary>
public class MqttPagesTests : IClassFixture<MqttFactory>
{
    private readonly MqttFactory _f;
    public MqttPagesTests(MqttFactory f) => _f = f;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    [Fact]
    public async Task SettingsTab_ShowsTheBroker_AndThePasswordOnlyAsMissing()
    {
        await _f.SeedAsync();
        var html = Decode(await _f.CreateClient().GetStringAsync(SettingsTabs.Href(IntegrationKeys.Mqtt)));
        html.Should().Contain("Broker MQTT (Home Assistant Discovery)").And.Contain("value=\"broker.test\"").And.Contain("Prefix discovery")
            .And.Contain("Adaugă automat ce se descoperă").And.Contain("lipsă").And.Contain("nu există");
        html.Should().Contain("Probleme").And.Contain("orphan", "a payload without unique_id is listed");
        html.Should().Contain("șablon neacceptat");
        html.Should().NotContain("parola-");
    }

    [Fact]
    public async Task Dispozitive_ChipDiscoveredAndAddPanel_AndTheGenericRows()
    {
        await _f.SeedAsync();
        var c = _f.CreateClient();
        var html = Decode(await c.GetStringAsync("/dispozitive"));
        html.Should().Contain("href=\"/settings/mqtt\"").And.Contain("descris pe broker").And.Contain("senzor-co2");
        html.Should().Contain("Priza birou").And.Contain("Dormitor senzor").And.Contain("+ releu").And.Contain("Alt dispozitiv");
        html.Should().Contain($"href=\"/dispozitive/dev%3A{_f.PlugDeviceId}\"");
        var add = Decode(await c.GetStringAsync(DevicePages.Add(IntegrationKeys.Mqtt)));
        add.Should().Contain("Cere republicarea").And.Contain("Usa intrare").And.Contain("Senzor ușă");
        var row = Decode(await c.GetStringAsync(DevicePages.Row("dev:" + _f.PlugDeviceId)));
        row.Should().Contain("Salvează dispozitivul").And.Contain("automat (Contor)").And.Contain(">Metrici<").And.Contain("Descrierea primită")
            .And.Contain("0xaabbccddee000002_power_zigbee2mqtt");
        Decode(await c.GetStringAsync("/dispozitive?cat=generic&retrase=1")).Should().NotContain("Priza birou", "a meter is not „Alt dispozitiv”");
    }

    [Fact]
    public async Task GenericDevicePage_ValuesCommandsAnd404()
    {
        await _f.SeedAsync();
        var c = _f.CreateClient();
        var html = Decode(await c.GetStringAsync(DevicePages.Device("dev:" + _f.PlugDeviceId)));
        html.Should().Contain("<title>KasaMonitor — Priza birou</title>").And.Contain("Putere").And.Contain("63,4").And.Contain("comandă (neactivată)")
            .And.Contain("data-view=\"counter\"").And.Contain("azi");
        var sensor = Decode(await c.GetStringAsync(DevicePages.Device("dev:" + _f.SensorDeviceId)));
        sensor.Should().Contain("21,4").And.Contain("Diagnostic").And.Contain("Linkquality");
        (await c.GetAsync(DevicePages.Device("dev:999"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.GetAsync(DevicePages.Device("dev:x"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        Decode(await c.GetStringAsync("/clima")).Should().Contain("Dormitor senzor", "the routed sensor is in Climă");
    }

    [Fact]
    public async Task History_OtherDevicesSection_AndItsCsv()
    {
        await _f.SeedAsync();
        var c = _f.CreateClient();
        var html = Decode(await c.GetStringAsync("/history?p=zi"));
        html.Should().Contain("id=\"alte\"").And.Contain("Alte dispozitive").And.Contain("Priza birou</a> · Putere");
        html.Should().NotContain("Dormitor senzor</a> · Temperatură", "the routed climate stays in „Climă”")
            .And.NotContain("Dormitor senzor</a> · Linkquality", "diagnostics are not in the history by default");
        var csv = await c.GetAsync("/history/export.csv?section=" + HistorySections.Generic + "&p=zi");
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        (await csv.Content.ReadAsStringAsync()).Should().Contain("Dispozitiv").And.Contain("Σ (contor)");
        Decode(await c.GetStringAsync("/integration")).Should().Contain("entity_power_watts").And.Contain("entity_value");
    }
}
