using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>The app on a temporary database, without background workers, with one plug, one climate sensor and one Miele appliance.</summary>
public sealed class DevicesFactory : WebApplicationFactory<Program>
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-devices-{Guid.NewGuid():N}");
    public Guid PlugId { get; private set; }
    public Guid SensorId { get; private set; }
    public const string MieleId = "000111222333";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _seeded;

    public DevicesFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("KasaMonitor:DbPath", Path.Combine(Dir, "kasa.db"));
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    public async Task SeedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_seeded) return;
            var now = DateTimeOffset.UtcNow;
            using (var scope = Services.CreateScope())
            {
                PlugId = await scope.ServiceProvider.GetRequiredService<IPlugRepository>().UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.10"),
                    new PlugInfo("aabbccddee00", "Frigider", "KP115(EU)", "1.0", "1.0.12 Build 1", "", true, -50, TimeSpan.Zero)), now, CancellationToken.None);
                var miele = scope.ServiceProvider.GetRequiredService<IMieleRepository>();
                await miele.UpsertDeviceAsync(MieleId, null, now, CancellationToken.None);
                await miele.SetAliasAsync(MieleId, "Mașina de spălat", CancellationToken.None);
                var device = (await miele.ListDevicesAsync(true, CancellationToken.None)).Single();
                Services.GetRequiredService<MieleState>().Publish(new MieleApplianceLive(device, null, null, null, null, 0));
            }
            Services.GetRequiredService<IPlugStateBroadcaster>().Publish(new PlugLiveState(PlugId, "aabbccddee00", "Frigider", true,
                new EmeterReading(85_000, 230_000, 400, 1_000, 10, 100), now.AddSeconds(-5), "KP115(EU)"));
            await Services.GetRequiredService<ClimateRecorder>().RecordAsync(new ClimateSample(ClimateSources.Shelly, "aabbccddee01:0", "Camera tehnică", now, 22.4, 48), CancellationToken.None);
            SensorId = Services.GetRequiredService<ClimateState>().Sensors.Single().Sensor.Id;
            _seeded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public HttpClient NoRedirect() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class DevicesPageTests : IClassFixture<DevicesFactory>
{
    private readonly DevicesFactory _f;
    public DevicesPageTests(DevicesFactory f) => _f = f;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    [Fact]
    public async Task Plugs_IsAPermanentRedirectToDispozitive()
    {
        var r = await _f.NoRedirect().GetAsync("/plugs");
        r.StatusCode.Should().Be(HttpStatusCode.MovedPermanently);
        r.Headers.Location!.ToString().Should().Be("/dispozitive");
    }

    [Fact]
    public async Task Dispozitive_ListsEveryKind_WithTheIntegrationChips_AndTheFilters()
    {
        await _f.SeedAsync();
        var html = Decode(await _f.CreateClient().GetStringAsync("/dispozitive"));
        html.Should().Contain("<title>KasaMonitor — Dispozitive</title>");
        html.Should().Contain("aria-label=\"Starea integrărilor\"");
        foreach (var i in Integrations.All)
            html.Should().Contain($"href=\"/settings/{i.Key}\"", $"the chip of „{i.Name}” links its settings tab");
        html.Should().Contain("id=\"fCat\"").And.Contain("id=\"fFirma\"").And.Contain("id=\"fStare\"").And.Contain("id=\"fRetrase\"")
            .And.Contain("role=\"search\"");
        html.Should().Contain("Frigider").And.Contain("Camera tehnică").And.Contain("Mașina de spălat");
        html.Should().Contain(">Online<").And.Contain("TP-Link (Kasa, Tapo)").And.Contain("Senzor climă").And.Contain("Electrocasnic");
        html.Should().Contain("aria-expanded=\"false\"").And.Contain("Retrage Frigider");
        // The menu: „Dispozitive” is /dispozitive.
        html.Should().Contain("href=\"/dispozitive\"").And.NotContain("href=\"/plugs\"");
    }

    [Fact]
    public async Task Dispozitive_FilterInTheAddress_AndARowOpenedFromALink()
    {
        await _f.SeedAsync();
        var c = _f.CreateClient();
        var shelly = Decode(await c.GetStringAsync("/dispozitive?firma=shelly"));
        shelly.Should().Contain("Camera tehnică").And.NotContain(">Frigider<");
        var open = Decode(await c.GetStringAsync(DevicePages.Row(DeviceRow.ClimateKey(_f.SensorId))));
        open.Should().Contain("Salvează senzorul").And.Contain($"id=\"cName-{_f.SensorId}\"", "?d= opens the row with the sensor's editor");
        var plug = Decode(await c.GetStringAsync(DevicePages.Row(DeviceRow.PlugKey(_f.PlugId))));
        plug.Should().Contain("Salvează numele").And.Contain("un singur aparat pe priză").And.Contain("Setările dispozitivului");
        var miele = Decode(await c.GetStringAsync(DevicePages.Row(DeviceRow.MieleKey(DevicesFactory.MieleId))));
        miele.Should().Contain("Priza care îl alimentează").And.Contain("Salvează aparatul");
        var add = Decode(await c.GetStringAsync(DevicePages.Add(IntegrationKeys.TpLink)));
        add.Should().Contain("Scanează acum").And.Contain("Adaugă după IP");
        Decode(await c.GetStringAsync(DevicePages.Add(IntegrationKeys.Viessmann))).Should().Contain("Lipsește Client ID-ul").And.Contain("href=\"/settings/viessmann\"");
        Decode(await c.GetStringAsync(DevicePages.Add(IntegrationKeys.Govee))).Should().Contain("Lipsește cheia API Govee");
    }

    [Fact]
    public async Task DataPages_KeepTheirData_AndLinkTheDeviceRow_InsteadOfTheMovedForms()
    {
        await _f.SeedAsync();
        var c = _f.CreateClient();
        var clima = Decode(await c.GetStringAsync("/clima"));
        clima.Should().Contain("Camera tehnică").And.Contain($"href=\"/dispozitive?d=climate%3A{_f.SensorId}\"")
            .And.NotContain("id=\"eName\"", "R6: the sensor is edited on /dispozitive only");
        var aparate = Decode(await c.GetStringAsync("/aparate"));
        aparate.Should().Contain("Mașina de spălat").And.Contain($"href=\"/dispozitive?d=miele%3A{DevicesFactory.MieleId}\"")
            .And.NotContain("Priza care îl alimentează", "R6: alias and plug moved to /dispozitive").And.NotContain("Salvează</button>");
        var prize = Decode(await c.GetStringAsync("/prize"));
        prize.Should().Contain($"href=\"/dispozitive?d=plug%3A{_f.PlugId}\"").And.NotContain("Reading the plug").And.NotContain(">Details");
        var govee = Decode(await c.GetStringAsync("/settings/govee"));
        govee.Should().NotContain("<th>Acasă</th>", "the Govee ticks moved to Dispozitive → Adaugă");
        Decode(await c.GetStringAsync("/contor")).Should().Contain("href=\"/dispozitive?firma=shelly\"", "R8: the main meter is chosen on Dispozitive");
    }
}
