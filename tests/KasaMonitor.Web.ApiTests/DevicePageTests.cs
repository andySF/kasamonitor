using System.Net;
using FluentAssertions;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>0.11, F1: the generated page /dispozitive/{key} and its links from the device list.</summary>
public class DevicePageTests : IClassFixture<DevicesFactory>
{
    private readonly DevicesFactory _f;
    public DevicePageTests(DevicesFactory f) => _f = f;

    private static string Decode(string html) => WebUtility.HtmlDecode(html);

    [Fact]
    public async Task PlugPage_ShowsEveryValue_TheIntegrationPage_AndTheConfigureLink()
    {
        await _f.SeedAsync();
        var key = DeviceRow.PlugKey(_f.PlugId);
        var html = Decode(await _f.CreateClient().GetStringAsync(DevicePages.Device(key)));
        html.Should().Contain("<title>KasaMonitor — Frigider</title>");
        html.Should().Contain("Putere").And.Contain("85,0").And.Contain("Tensiune").And.Contain("230,0").And.Contain("Energie (contorul dispozitivului)");
        html.Should().Contain($"data-entity=\"{key}/power\"").And.Contain("data-view=\"chart\"").And.Contain("data-view=\"counter\"");
        html.Should().Contain("Diagnostic", "today / month counters and „online” are folded");
        html.Should().Contain($"href=\"/dispozitive?d=plug%3A{_f.PlugId}\"", "✎ opens the expanded row, the only place it is configured");
        html.Should().Contain($"href=\"/history?plug={_f.PlugId}#curent\"", "the integration's page keeps the totals");
    }

    [Fact]
    public async Task ClimateSensorPage_ShowsTemperatureAndHumidity()
    {
        await _f.SeedAsync();
        var html = Decode(await _f.CreateClient().GetStringAsync(DevicePages.Device(DeviceRow.ClimateKey(_f.SensorId))));
        html.Should().Contain("Camera tehnică").And.Contain("Temperatură").And.Contain("22,4").And.Contain("Umiditate").And.Contain("48");
        html.Should().Contain("href=\"/clima\"");
    }

    [Fact]
    public async Task UnknownOrMalformedKey_Is404()
    {
        await _f.SeedAsync();
        var c = _f.CreateClient();
        (await c.GetAsync(DevicePages.Device(DeviceRow.PlugKey(Guid.NewGuid())))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.GetAsync(DevicePages.Device("plug:nu-e-guid"))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await c.GetAsync("/dispozitive/nimic%3A1")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeviceList_LinksTheGeneratedPage_AndTheExpandedRowHasTheEntityPreferences()
    {
        await _f.SeedAsync();
        var c = _f.CreateClient();
        var list = Decode(await c.GetStringAsync("/dispozitive"));
        list.Should().Contain($"href=\"/dispozitive/plug%3A{_f.PlugId}\"").And.Contain("Toate valorile Frigider");
        var row = Decode(await c.GetStringAsync(DevicePages.Row(DeviceRow.PlugKey(_f.PlugId))));
        row.Should().Contain("Valorile afișate pe pagina dispozitivului").And.Contain("Salvează valorile afișate")
            .And.Contain("Salvează numele", "R1: the plug's own editor still opens by its key prefix");
        Decode(await c.GetStringAsync(DevicePages.Row(DeviceRow.MieleKey(DevicesFactory.MieleId)))).Should().Contain("Salvează aparatul");
    }
}
