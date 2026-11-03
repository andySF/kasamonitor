using FluentAssertions;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Miele;
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

/// <summary>
/// The app on a temporary database, without background workers, with every secret the settings know set: Govee key and
/// Miele client secret from the configuration, TP-Link / Shelly / remote write passwords and the Viessmann and Miele
/// tokens in the database. No tab may show any of them.
/// </summary>
public sealed class SettingsFactory : WebApplicationFactory<Program>
{
    public const string GoveeKey = "secret-govee-key-901";
    public const string MieleSecret = "secret-miele-client-902";
    public const string TapoPassword = "secret-tapo-pw-903";
    public const string ShellyPassword = "secret-shelly-pw-904";
    public const string RemoteWriteUser = "secret-rw-user-905";
    public const string RemoteWritePassword = "secret-rw-pw-906";
    public const string ViessmannAccess = "secret-vicare-access-907";
    public const string ViessmannRefresh = "secret-vicare-refresh-908";
    public const string MieleAccess = "secret-miele-access-909";
    public const string MieleRefresh = "secret-miele-refresh-910";

    public static readonly string[] Secrets =
        [GoveeKey, MieleSecret, TapoPassword, ShellyPassword, RemoteWriteUser, RemoteWritePassword, ViessmannAccess, ViessmannRefresh, MieleAccess, MieleRefresh];

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-settings-{Guid.NewGuid():N}");
    private bool _seeded;

    public SettingsFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("KasaMonitor:DbPath", Path.Combine(Dir, "kasa.db"));
        builder.UseSetting("KasaMonitor:GoveeApiKey", GoveeKey);
        builder.UseSetting("KasaMonitor:MieleClientSecret", MieleSecret);
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    public async Task<HttpClient> ClientWithSecretsAsync()
    {
        var c = CreateClient();
        if (_seeded) return c;
        var kasa = Services.GetRequiredService<IKasaSettingsService>();
        await kasa.SaveAsync(new KasaSettingsUpdate(TapoUsername: "u@example.com", NewTapoPassword: TapoPassword, NewShellyPassword: ShellyPassword,
            RemoteWriteUrl: "http://prometheus.example:9090/api/v1/write", NewRemoteWriteUser: RemoteWriteUser, NewRemoteWritePassword: RemoteWritePassword),
            CancellationToken.None);
        var heating = Services.GetRequiredService<IHeatingSettingsService>();
        await heating.SaveAsync(new HeatingSettingsUpdate(ClientId: "vicare-client"), CancellationToken.None);
        await heating.SaveTokenAsync(new ViessmannToken(ViessmannAccess, ViessmannRefresh, DateTimeOffset.UtcNow.AddHours(1)), CancellationToken.None);
        var miele = Services.GetRequiredService<IMieleSettingsService>();
        await miele.SaveClientIdAsync("miele-client", CancellationToken.None);
        await miele.SaveTokenAsync(new MieleToken(MieleAccess, MieleRefresh, DateTimeOffset.UtcNow.AddDays(30), 2_592_000), CancellationToken.None);
        _seeded = true;
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class SettingsPageTests : IClassFixture<SettingsFactory>
{
    private readonly SettingsFactory _f;
    public SettingsPageTests(SettingsFactory f) => _f = f;

    private static string Decode(string html) => System.Net.WebUtility.HtmlDecode(html);

    [Fact]
    public void Registry_EveryIntegrationHasATab_AndTheTabsAreAppExportThenTheIntegrations()
    {
        Integrations.All.Select(i => i.Key).Should().Equal(IntegrationKeys.TpLink, IntegrationKeys.Shelly, IntegrationKeys.Viessmann,
            IntegrationKeys.Miele, IntegrationKeys.Govee, IntegrationKeys.OpenMeteo, IntegrationKeys.Mqtt);
        SettingsTabs.All.Select(t => t.Name).Should().Equal("Aplicație", "Export", "TP-Link (Kasa, Tapo)", "Shelly", "Viessmann", "Miele", "Govee", "Open-Meteo", "MQTT");
        foreach (var i in Integrations.All)
        {
            SettingsTabs.Get(i.Key).Should().NotBeNull($"„{i.Name}” needs a settings tab");
            SettingsTabs.Get(i.Key)!.Component.Should().Be(i.SettingsComponent);
            i.SettingsComponent.Should().Implement<KasaMonitor.Web.Components.Settings.ISettingsTab>();
            i.Categories.Should().NotBeEmpty();
            i.SettingsUrl.Should().Be("/settings/" + i.Key);
        }
        SettingsTabs.All.Select(t => t.Component).Should().AllSatisfy(t => t.Should().Implement<KasaMonitor.Web.Components.Settings.ISettingsTab>());
    }

    [Fact]
    public async Task Registry_EveryIntegrationHasAState_OnAnEmptyApp()
    {
        await _f.ClientWithSecretsAsync();
        using var scope = _f.Services.CreateScope();
        foreach (var i in Integrations.All)
        {
            var st = await i.Status(scope.ServiceProvider, DateTimeOffset.UtcNow, CancellationToken.None);
            st.Text.Should().NotBeNullOrWhiteSpace($"„{i.Name}” must say how it is");
            foreach (var secret in SettingsFactory.Secrets) st.Text.Should().NotContain(secret);
        }
    }

    [Fact]
    public async Task EveryTab_Renders_Active_WithItsStateLine_AndNoSecretAnywhere()
    {
        var c = await _f.ClientWithSecretsAsync();
        foreach (var tab in SettingsTabs.All)
        {
            var html = Decode(await c.GetStringAsync(SettingsTabs.Href(tab.Key)));
            html.Should().Contain("<title>KasaMonitor — Setări</title>");
            html.Should().Contain($"id=\"tab-{tab.Key}\"").And.Contain("role=\"tablist\"").And.Contain("role=\"tabpanel\"");
            html.Should().MatchRegex($"class=\"nav-link active\" role=\"tab\" id=\"tab-{tab.Key}\"[^>]*aria-selected=\"true\"", $"„{tab.Name}” is the selected tab");
            if (tab.Integration is not null) html.Should().Contain("Stare:", $"„{tab.Name}” starts with the integration's state");
            foreach (var secret in SettingsFactory.Secrets)
                html.Should().NotContain(secret, $"tab „{tab.Name}” must not show a secret");
        }
    }

    [Fact]
    public async Task Dispozitive_AndEveryAddPanel_ShowNoSecret()
    {
        var c = await _f.ClientWithSecretsAsync();
        var urls = Integrations.All.Select(i => DevicePages.Add(i.Key)).Prepend(DevicePages.Path).ToList();
        foreach (var url in urls)
        {
            var html = Decode(await c.GetStringAsync(url));
            html.Should().Contain("<title>KasaMonitor — Dispozitive</title>");
            foreach (var secret in SettingsFactory.Secrets)
                html.Should().NotContain(secret, $"{url} must not show a secret");
        }
        Decode(await c.GetStringAsync(DevicePages.Add(IntegrationKeys.Viessmann))).Should().Contain("Reconectează", "the add panel knows the account is connected");
    }

    [Fact]
    public async Task EachTab_ShowsItsOwnFields_AndOnlyThem()
    {
        var c = await _f.ClientWithSecretsAsync();
        var app = Decode(await c.GetStringAsync("/settings/aplicatie"));
        app.Should().Contain("Tarife").And.Contain("Curent (RON / kWh)").And.Contain("Gaz (RON / kWh)").And.Contain("Descoperire în rețea")
            .And.Contain("Flux live").And.Contain("Consumatori detectați (NILM)").And.Contain("Baza gradelor-zile").And.Contain("Grafana")
            .And.Contain("Păstrează citirile brute");
        app.Should().Contain("id=\"poll\"").And.MatchRegex("id=\"poll\" value=\"10\" readonly disabled", "R9: the poll interval is read-only, from the configuration")
            .And.Contain("KasaMonitor__PollIntervalSeconds");
        app.Should().NotContain("Rețele broadcast").And.NotContain("Client ID").And.NotContain("remote write");

        Decode(await c.GetStringAsync("/settings")).Should().Contain("Curent (RON / kWh)", "/settings opens „Aplicație”");
        Decode(await c.GetStringAsync("/settings/nimic")).Should().Contain("Curent (RON / kWh)", "an unknown tab falls back to „Aplicație”");

        var export = Decode(await c.GetStringAsync("/settings/export"));
        export.Should().Contain("Prometheus remote write").And.Contain("href=\"/integration\"").And.Contain("•••• (setat");
        var tplink = Decode(await c.GetStringAsync("/settings/tplink"));
        tplink.Should().Contain("Rețele broadcast").And.Contain("Cont TP-Link").And.Contain("u@example.com").And.NotContain("Descoperire automată");
        var shelly = Decode(await c.GetStringAsync("/settings/shelly"));
        shelly.Should().Contain("Parolă Shelly").And.Contain("Adrese de probat").And.Contain("Permite comanda releului");
        var vicare = Decode(await c.GetStringAsync("/settings/viessmann"));
        vicare.Should().Contain("Client ID").And.Contain("vicare-client").And.Contain("href=\"/centrala/connect\"").And.Contain("Reconectează")
            .And.Contain("Fusul orar al centralei").And.NotContain("Preț gaz").And.NotContain("Gaz (RON / kWh)");
        var miele = Decode(await c.GetStringAsync("/settings/miele"));
        miele.Should().Contain("Miele (mașină de spălat, uscător)").And.Contain("încărcat").And.Contain("Reconectează");
        var govee = Decode(await c.GetStringAsync("/settings/govee"));
        govee.Should().Contain("Govee (termometre Wi-Fi)").And.Contain("încărcată").And.Contain("cereri azi")
            .And.Contain("href=\"/dispozitive?adauga=govee\"", "the ticks moved to Dispozitive → Adaugă → Govee").And.NotContain("<th>Acasă</th>");
        var meteo = Decode(await c.GetStringAsync("/settings/openmeteo"));
        meteo.Should().Contain("Latitudine").And.NotContain("Baza gradelor-zile de încălzire");
    }

    [Fact]
    public async Task Menu_HasNoIntegrare_ButTheIntegrationPageStays_LinkedFromExport()
    {
        var c = await _f.ClientWithSecretsAsync();
        var home = Decode(await c.GetStringAsync("/settings/export"));
        home.Should().NotContain(">Integrare</a>", "R11: „Integrare” left the top menu");
        var page = Decode(await c.GetStringAsync("/integration"));
        page.Should().Contain("<title>KasaMonitor — Integrare</title>").And.Contain("href=\"/settings/export\"");
    }

    [Fact]
    public async Task DataPages_LinkTheExactTab()
    {
        var client = _f.CreateClient();
        Decode(await client.GetStringAsync("/clima")).Should().Contain("href=\"/settings/govee\"").And.Contain("href=\"/settings/openmeteo\"")
            .And.NotContain("turul și apa caldă", "R10: the boiler gives only the room thermostat since 0.9.3");
        Decode(await client.GetStringAsync("/aparate")).Should().Contain("href=\"/settings/miele\"");
        Decode(await client.GetStringAsync("/history?p=luna&d=2026-10-05")).Should().Contain("href=\"/settings/aplicatie\"");
    }
}
