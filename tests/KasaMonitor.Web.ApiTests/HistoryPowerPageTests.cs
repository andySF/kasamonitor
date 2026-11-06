using System.Net;
using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>The app on a temporary database with a main meter and a plug, their minutes of 14 October 2026 and 2025 and the hours of October 2026.</summary>
public sealed class PowerFactory : WebApplicationFactory<Program>
{
    public static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    public static readonly ReportPeriod Day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 10, 14));
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"kasa-power-{Guid.NewGuid():N}");
    public Guid MainId { get; private set; }
    public Guid PlugId { get; private set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _seeded;

    public PowerFactory() => Directory.CreateDirectory(Dir);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSqliteTestDatabase(Dir);
        builder.ConfigureTestServices(s => s.RemoveAll<IHostedService>());
    }

    public async Task SeedAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_seeded) return;
            var ct = CancellationToken.None;
            using var scope = Services.CreateScope();
            var plugs = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
            MainId = await plugs.UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.20"),
                new PlugInfo("aabbccddee10", "Contor casă", "KP115(EU)", "1.0", "1.0.12 Build 1", "", true, -50, TimeSpan.Zero)), DateTimeOffset.UtcNow, ct);
            await plugs.SetRoleAsync(MainId, PlugRole.MainMeter, ct);
            PlugId = await plugs.UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.21"),
                new PlugInfo("aabbccddee11", "Frigider", "KP115(EU)", "1.0", "1.0.12 Build 1", "", true, -50, TimeSpan.Zero)), DateTimeOffset.UtcNow, ct);
            var db = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
            // 14 October 2026: the house 600 W, 4.2 kW at 19:30; the fridge 80 W. 14 October 2025: the house 500 W.
            foreach (var t in PowerHistory.Grid(Day, PowerResolution.Minute, Ro))
            {
                var evening = TimeZoneInfo.ConvertTime(t, Ro) is { Hour: 19, Minute: 30 };
                db.OneMinuteAggregates.Add(new OneMinuteAggregateEntity { PlugId = MainId, BucketStartUtc = t, PAvgMw = 600_000, PMaxMw = evening ? 4_200_000 : 700_000, SampleCount = 6, OnlineCount = 6 });
                db.OneMinuteAggregates.Add(new OneMinuteAggregateEntity { PlugId = PlugId, BucketStartUtc = t, PAvgMw = 80_000, PMaxMw = 90_000, SampleCount = 6, OnlineCount = 6 });
            }
            foreach (var t in PowerHistory.Grid(PowerHistory.LastYear(Day), PowerResolution.Minute, Ro))
                db.OneMinuteAggregates.Add(new OneMinuteAggregateEntity { PlugId = MainId, BucketStartUtc = t, PAvgMw = 500_000, PMaxMw = 600_000, SampleCount = 6, OnlineCount = 6 });
            await db.SaveChangesAsync(ct);
            var month = ReportPeriod.Containing(ReportGranularity.Month, Day.Start);
            await scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>().UpsertHoursAsync(MainId,
                PowerHistory.Grid(month, PowerResolution.Hour, Ro).Select(t => new PowerHour(t, 650_000, 3_000_000, 60, 650)).ToList(), ct);
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

/// <summary>/history „Putere” (0.14.0): the section, its series in the address, the comparison control and the CSV.</summary>
public class HistoryPowerPageTests : IClassFixture<PowerFactory>
{
    private readonly PowerFactory _f;
    public HistoryPowerPageTests(PowerFactory f) => _f = f;

    private async Task<string> PageAsync(string url)
    {
        await _f.SeedAsync();
        return WebUtility.HtmlDecode(await _f.CreateClient().GetStringAsync(url));
    }

    [Fact]
    public async Task ADay_TheMainMeterByDefault_ByMinute_WithItsFigures()
    {
        var html = await PageAsync("/history?p=zi&d=2026-10-14");

        html.Should().Contain("id=\"putere\"").And.Contain("href=\"#putere\"");
        html.IndexOf("id=\"curent\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("id=\"putere\"", StringComparison.Ordinal));
        html.IndexOf("id=\"putere\"", StringComparison.Ordinal).Should().BeLessThan(html.IndexOf("id=\"gaz\"", StringComparison.Ordinal));
        html.Should().Contain("Serii afișate").And.Contain("Contor casă (contor)").And.Contain("Frigider");
        html.Should().Contain("Putere medie · Contor casă").And.Contain("Putere maximă · Contor casă");
        html.Should().Contain("4.200").And.Contain("14 oct, 19:30", "the maximum and its minute, local time");
        html.Should().Contain("class=\"chart hist-power\"").And.Contain("Un punct pe minut");
        html.Should().Contain("ziua precedentă", "the previous period by default");
        html.Should().Contain("section=putere&p=zi&d=2026-10-14");
    }

    [Fact]
    public async Task LastYear_FromTheAddress_TheSameDayOf2025_AndTheOtherSectionsKeepComparing()
    {
        var html = await PageAsync("/history?p=zi&d=2026-10-14&cmp=an");

        html.Should().Contain("Același interval anul trecut");
        html.Should().MatchRegex("<option value=\"an\" selected[^>]*>Același interval anul trecut");
        html.Should().Contain("anul trecut (marți, 14 octombrie 2025)").And.Contain("față de marți, 14 octombrie 2025");
        html.Should().Contain("▲ 100 W", "600 W against 500 W");
        html.Should().Contain("anul trecut doar în Putere; celelalte secțiuni: față de ziua precedentă", "the control says what the other sections compare with");
        html.Should().Contain("section=putere&p=zi&d=2026-10-14&cmp=an");
    }

    [Fact]
    public async Task Series_InTheAddress_NoneAndBoth()
    {
        (await PageAsync("/history?p=zi&d=2026-10-14&pw=-")).Should().Contain("Alege cel puțin o serie.");
        var both = await PageAsync($"/history?p=zi&d=2026-10-14&cmp=0&pw={_f.PlugId},{_f.MainId}");
        both.Should().Contain("Putere medie · Contor casă", "the figures follow the main meter when it is shown");
        both.Should().Contain("<option value=\"0\" selected");
        both.Should().Contain($"section=putere&p=zi&d=2026-10-14&cmp=0&pw={_f.PlugId},{_f.MainId}");
    }

    [Fact]
    public async Task AMonth_ByHour_AndTheCsv()
    {
        var html = await PageAsync("/history?p=luna&d=2026-10");
        html.Should().Contain("Un punct pe oră").And.Contain("3.000");

        var r = await _f.CreateClient().GetAsync($"/history/export.csv?section=putere&p=zi&d=2026-10-14&cmp=an&pw={_f.MainId},{_f.PlugId}");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentDisposition!.FileName.Should().Contain("istoric-putere-zi-2026-10-14.csv");
        var bytes = await r.Content.ReadAsByteArrayAsync();
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().Be("Minutul;Serie;Medie W;Max W;Maxim la;Minute cu date;Comparație medie W;Comparație max W");
        lines[1].Should().Be("2026-10-14 00:00;Contor casă;600;700;2026-10-14 00:00;1;500;600");
        lines[2].Should().Be("2026-10-14 00:00;Frigider;80;90;2026-10-14 00:00;1;;");
        lines.Should().HaveCount(1 + 2 * 1440);
    }

    /// <summary>Fix round 1, R4: a bar of a month opens its day with the same comparison and the same series.</summary>
    [Fact]
    public async Task DrillDown_KeepsTheComparisonAndTheSeries()
    {
        var html = await PageAsync($"/history?p=luna&d=2026-10&cmp=an&pw={_f.PlugId},{_f.MainId}");
        html.Should().Contain($"href=\"/history?p=zi&d=2026-10-12&cmp=an&pw={_f.PlugId},{_f.MainId}\"");
    }
}
