using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Costing;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Web.Ui;
using Xunit;

namespace KasaMonitor.Web.Tests;

/// <summary>The tables of /history (0.9.2): the page shows them and the CSV export writes them.</summary>
public class HistoryTablesTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly EnergyPrices Prices = new(1.5m, 10.55, 0.3m, true);

    private static Plug P(string alias, PlugRole role) => new(Guid.NewGuid(), "AA:BB", IPAddress.Loopback, alias, false, "m", "", "", "",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null, Role: role);

    private static HouseReport Month(DateTimeOffset now, out Plug fridge)
    {
        var main = P("Contor", PlugRole.MainMeter);
        fridge = P("Frigider", PlugRole.Plug);
        var daily = new List<PlugDailyKwh>();
        for (var d = 1; d <= 2; d++)
        {
            daily.Add(new PlugDailyKwh(main.Id, new DailyKwh(2026, 11, d, 10_000)));
            daily.Add(new PlugDailyKwh(fridge.Id, new DailyKwh(2026, 11, d, 1_000)));
        }
        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));
        return HouseReportBuilder.Build(new HouseReportInput(month, Ro, main, new[] { fridge }, Array.Empty<Plug>(), daily,
            Array.Empty<PlugMonthWh>(), Array.Empty<PlugMonthWh>(), Array.Empty<PlugHourWh>(), now, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void Electricity_RowsStopAtNow_DaysLinkToTheirDay_ThePlugFilterAddsItsColumn()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var house = Month(now, out var fridge);

        var t = HistoryTables.Electricity(house, Prices, now, compare: true, plug: fridge.Id);

        t.Headers.Should().Equal("Ziua", "Casă (contor) kWh", "Prize kWh", "Restul casei kWh", "Casă lei", "Frigider kWh", "Notă");
        t.Rows.Should().HaveCount(2, "the rest of November has not started (R7)");
        t.Rows[0].Should().Equal("duminică 1 nov", "10,0", "1,00", "9,00", "15,00", "1,00", "");
        t.Rows[1][^1].Should().Be("în curs (parțial)");
        t.Links![1].Should().Be("/history?p=zi&d=2026-11-02&plug=" + fridge.Id);
        // The CSV: the day in ISO, the numbers numeric, the markers in „Notă” (review R10).
        HistoryCsv.Write(t).Split("\r\n")[1].Should().Be("2026-11-01;10,0;1,00;9,00;15,00;1,00;");
    }

    [Fact]
    public void DrillLinks_DayOfAWeekOrRange_MonthOfAYear_NoneForHours()
    {
        var day = new ReportBucket(0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new DateTime(2026, 3, 4), new DateOnly(2026, 3, 4));
        HistoryTables.DrillLink(day, ReportGranularity.Week, true, null).Should().Be("/history?p=zi&d=2026-03-04");
        HistoryTables.DrillLink(day, ReportGranularity.Range, false, null).Should().Be("/history?p=zi&d=2026-03-04&cmp=0");
        HistoryTables.DrillLink(day, ReportGranularity.Year, true, null).Should().Be("/history?p=luna&d=2026-03");
        HistoryTables.DrillLink(day, ReportGranularity.Day, true, null).Should().BeNull();
    }

    [Fact]
    public void Consumers_GroupRowsListTheirMembers_AndTheTotalIsTheMeter()
    {
        var b = ConsumerBreakdownRule.Build(new double?[] { 10 }, Array.Empty<ConsumerPlug>(),
            new Dictionary<long, IReadOnlyList<double>> { [1] = new[] { 2.0 }, [2] = new[] { 1.0 } },
            new[] { new ConsumerSignature(1, "Fierbător", true, null), new ConsumerSignature(2, "Necunoscut #2", false, null) });

        var t = HistoryTables.Consumers(b, Prices);

        t.Rows.Select(r => r[0]).Should().Equal("Fierbător", "Neidentificați (1 semnături)", "  Necunoscut #2", "Nedetectat", "Total (contor)");
        t.Rows[0][4].Should().Be("20,0");
        t.Rows[^1][2].Should().Be("10,0");
    }

    [Fact]
    public void Events_LocalTimes_AndTheSource()
    {
        var e = new HistoryEvent(new DateTimeOffset(2026, 11, 2, 10, 5, 0, TimeSpan.Zero), HistoryEventSource.Boiler, "Vitodens", "eroare F4",
            new DateTimeOffset(2026, 11, 2, 10, 15, 0, TimeSpan.Zero), true);
        HistoryTables.Events(new[] { e }, Ro).Rows.Single().Should().Equal("2026-11-02 12:05", "2026-11-02 12:15", "Centrală", "Vitodens", "eroare F4");
    }
}
