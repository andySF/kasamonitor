using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Workers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>The app on a temporary database, without background workers, with a fake Govee key from the environment setting.</summary>
public sealed class ClimaFactory : WebApplicationFactory<Program>
{
    public const string FakeKey = "test-govee-key-456";
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-clima-{Guid.NewGuid():N}");

    public ClimaFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSqliteTestDatabase(Dir);
        builder.UseSetting("KasaMonitor:GoveeApiKey", FakeKey);
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class ClimaPageTests : IClassFixture<ClimaFactory>
{
    private readonly ClimaFactory _f;
    public ClimaPageTests(ClimaFactory f) => _f = f;

    [Fact]
    public async Task Pages_Render_EmptyThenWithASensor_KeyNeverShown()
    {
        var c = _f.CreateClient();
        (await c.GetStringAsync("/clima")).Should().Contain("Niciun senzor încă.");

        var now = DateTimeOffset.UtcNow;
        var recorder = _f.Services.GetRequiredService<ClimateRecorder>();
        await recorder.RecordAsync(new ClimateSample(ClimateSources.Govee, "AA:BB:CC:DD:EE:FF:00:11", "Baie", now.AddMinutes(-40), 24.5, 55), CancellationToken.None);
        await recorder.RecordAsync(new ClimateSample(ClimateSources.Govee, "AA:BB:CC:DD:EE:FF:00:11", "Baie", now.AddMinutes(-5), 25.7, 56.7), CancellationToken.None);
        await recorder.RecordAsync(new ClimateSample(ClimateSources.OpenMeteo, OpenMeteoApi.OutdoorExternalId, "Afară", now, 9.4, 78,
            DefaultRoom: "Afară", DefaultOutdoor: true), CancellationToken.None);

        var clima = await c.GetStringAsync("/clima");
        clima.Should().Contain("Baie").And.Contain("25,7").And.Contain("Min / medie / max").And.Contain("9,4").And.Contain("Open-Meteo");

        // R2: on the URL of each tab (a tab renders alone; the whole /settings no longer contains them all).
        var govee = await c.GetStringAsync("/settings/govee");
        govee.Should().Contain("Govee (termometre Wi-Fi)").And.Contain("încărcată").And.NotContain(ClimaFactory.FakeKey);
        var meteo = await c.GetStringAsync("/settings/openmeteo");
        meteo.Should().Contain("Temperatura exterioară (Open-Meteo)").And.NotContain(ClimaFactory.FakeKey);
        (await c.GetStringAsync("/integration")).Should().Contain("climate_temperature_celsius");
        // The prometheus registry is process-wide and the other factories of this assembly register their own (empty)
        // climate state, so only the family is checked here; the series themselves are covered by ClimateMetricsTests.
        (await c.GetStringAsync("/metrics")).Should().Contain("climate_api_requests_today{source=\"govee\"}");
        (await c.GetStringAsync("/centrala")).Should().NotContain(ClimaFactory.FakeKey);
    }
}
