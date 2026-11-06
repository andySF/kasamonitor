using System.Net;
using System.Text;
using FluentAssertions;
using Xunit;

namespace KasaMonitor.Web.ApiTests;

/// <summary>/history (0.9.2) and its CSV export on an empty database, without background workers.</summary>
public class HistoryPageTests : IClassFixture<HomeFactory>
{
    private readonly HomeFactory _f;
    public HistoryPageTests(HomeFactory f) => _f = f;

    private static readonly string[] Sections = { "id=\"curent\"", "id=\"putere\"", "id=\"gaz\"", "id=\"aparate\"", "id=\"consumatori\"", "id=\"clima\"", "id=\"costuri\"", "id=\"evenimente\"" };

    [Fact]
    public async Task History_OnePage_OnePeriod_AllSectionsInOrder_InRomanian()
    {
        var c = _f.CreateClient();
        var html = WebUtility.HtmlDecode(await c.GetStringAsync("/history?p=luna&d=2026-10"));

        html.Should().Contain("<title>KasaMonitor — Istoric</title>").And.Contain("octombrie 2026").And.Contain("Interval");
        html.Should().Contain("Compară cu luna precedentă");
        Sections.Select(s => html.IndexOf(s, StringComparison.Ordinal)).Should().OnlyContain(p => p > 0).And.BeInAscendingOrder();
        html.Should().Contain("href=\"#clima\"", "the chips lead to the sections");
        html.Should().Contain("/history/export.csv?section=curent&p=luna&d=2026-10");
        html.Should().Contain("Fără contor general").And.Contain("Fără centrală conectată").And.Contain("Niciun aparat Miele conectat")
            .And.Contain("Niciun senzor de climă").And.Contain("Nicio priză și niciun contor");
        html.Should().Contain("Același interval anul trecut", "one comparison control for the page (0.14.0)").And.NotContain("role=\"switch\" aria-checked");
        html.Should().NotContain("Last 7 days").And.NotContain("Loading data").And.NotContain("No plugs available");
    }

    [Fact]
    public async Task History_RangeAndComparisonFromTheAddress()
    {
        var c = _f.CreateClient();
        var html = WebUtility.HtmlDecode(await c.GetStringAsync("/history?p=interval&de=2026-10-01&pana=2026-10-20&cmp=0"));
        html.Should().Contain("1 oct – 20 oct 2026").And.Contain("type=\"date\"");
        html.Should().Contain("section=costuri&p=interval&de=2026-10-01&pana=2026-10-20&cmp=0");
        (WebUtility.HtmlDecode(await c.GetStringAsync("/history?plugId=" + Guid.NewGuid()))).Should().Contain("<title>KasaMonitor — Istoric</title>", "the 0.9.0 links still open");
    }

    [Fact]
    public async Task Csv_IsTheSectionTable_Utf8Bom_Semicolons_UnknownSectionIs400()
    {
        var c = _f.CreateClient();
        var r = await c.GetAsync("/history/export.csv?section=costuri&p=luna&d=2026-10");
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        r.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        r.Content.Headers.ContentDisposition!.FileName.Should().Contain("istoric-costuri-luna-2026-10.csv");
        var bytes = await r.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        text.Should().StartWith("Ziua;Curent kWh;Curent lei;Gaz m³;Gaz lei;Total lei;Notă\r\n2026-10-01;");
        text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(32, "the header and the 31 days of October");

        foreach (var s in new[] { "curent", "putere", "gaz", "aparate", "aparate-programe", "consumatori", "consumatori-detectati", "clima", "clima-grade-zile", "evenimente" })
            (await c.GetAsync($"/history/export.csv?section={s}&p=an&d=2026")).StatusCode.Should().Be(HttpStatusCode.OK, s);
        (await c.GetAsync("/history/export.csv?section=nimic")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Consumatori_WithoutAMainMeter_HasNoIdentifiedSummary()
    {
        // The summary (7 / 30 days) sits with the detection's cards: shown only with detection on and a main meter.
        var html = WebUtility.HtmlDecode(await _f.CreateClient().GetStringAsync("/consumatori"));
        html.Should().Contain("<title>KasaMonitor — Consumatori</title>").And.NotContain("Cât din consum e identificat");
    }

    [Theory]
    [InlineData("/consumatori?sort=dp&dir=asc")]
    [InlineData("/consumatori?sort=%3Cscript%3E&dir=sideways")]
    [InlineData("/consumatori?sort=&dir=")]
    [InlineData("/consumatori?sort=last&sort=dp&dir=desc")]
    public async Task Consumatori_SortInTheAddress_WrongValuesStillOpenThePage(string url)
    {
        var r = await _f.CreateClient().GetAsync(url);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        WebUtility.HtmlDecode(await r.Content.ReadAsStringAsync()).Should().Contain("<title>KasaMonitor — Consumatori</title>");
    }
}
