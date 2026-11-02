using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>The app on a temporary empty database, without background workers.</summary>
public sealed class HomeFactory : WebApplicationFactory<Program>
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-home-{Guid.NewGuid():N}");

    public HomeFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("KasaMonitor:DbPath", Path.Combine(Dir, "kasa.db"));
        builder.UseSetting("KasaMonitor:GrafanaUrl", "http://grafana.example:3000/d/kasa");
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public class HomePageTests : IClassFixture<HomeFactory>
{
    private readonly HomeFactory _f;
    public HomePageTests(HomeFactory f) => _f = f;

    [Fact]
    public async Task Root_IsTheHomePage_WithItsSections_AndTheThemeScript()
    {
        var c = _f.CreateClient();
        // Texts written by C# expressions come HTML-encoded (ă → &#x103;): compare the decoded page.
        var html = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/"));
        html.Should().Contain("<title>KasaMonitor — Acasă</title>");
        html.Should().Contain("Acum în casă").And.Contain("Energie azi").And.Contain("Pe ore · curent").And.Contain("Aparate · azi")
            .And.Contain("Unde s-a dus energia azi").And.Contain("Azi vs ieri").And.Contain("Ultima actualizare");
        html.Should().Contain(">Azi</button>", "the day button of the period selector");
        html.Should().Contain("Fără contor general", "an empty database has no main meter");
        html.Should().Contain("http://grafana.example:3000/d/kasa", "the footer links Grafana from the setting");
        html.Should().Contain("data-theme-toggle").And.Contain("kasa-theme").And.Contain("css/home.css");
        html.Should().NotContain("id=\"h-clima\"", "without a source the „Climă” card is left out, no placeholder");

        // Menu order, in Romanian: Acasă, Prize, Contor, Centrală, Aparate, Climă, Consumatori, Istoric, Dispozitive, Setări, Integrare.
        var nav = new[] { ">Acasă</a>", ">Prize</a>", ">Contor</a>", ">Centrală</a>", ">Aparate</a>", ">Climă</a>", ">Consumatori</a>", ">Istoric</a>", ">Dispozitive</a>", ">Setări</a>", ">Integrare</a>" };
        var positions = nav.Select(n => html.IndexOf(n, StringComparison.Ordinal)).ToList();
        positions.Should().OnlyContain(p => p > 0).And.BeInAscendingOrder();
    }

    [Fact]
    public async Task PeriodInTheAddress_AndThePagesHaveTheirRomanianTitles()
    {
        var c = _f.CreateClient();
        var month = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/?p=luna&d=2026-10-05"));
        month.Should().Contain("Energie · octombrie 2026").And.Contain("Pe zile · curent").And.Contain("Compară cu luna precedentă");
        var bad = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/?p=nimic&d=x"));
        bad.Should().Contain("Energie azi", "an invalid address falls back to today");
        (System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/plugs"))).Should().Contain("<title>KasaMonitor — Dispozitive</title>");
        (System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/history"))).Should().Contain("<title>KasaMonitor — Istoric</title>");
        (System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/settings"))).Should().Contain("<title>KasaMonitor — Setări</title>");
    }

    [Fact]
    public async Task OldDashboard_IsNowPrize_WithItsCards()
    {
        var c = _f.CreateClient();
        var html = System.Net.WebUtility.HtmlDecode(await c.GetStringAsync("/prize"));
        html.Should().Contain("<title>KasaMonitor — Prize</title>").And.Contain("Putere acum").And.Contain("Azi (toate prizele)");
        (await c.GetStringAsync("/integration")).Should().Contain("kasa_house_standby_w").And.Contain("kasa_bill_estimate_ron");
        var metrics = await c.GetStringAsync("/metrics");
        metrics.Should().NotMatchRegex("(?m)^kasa_house_standby_w ", "no main meter: no standby series");
    }
}
