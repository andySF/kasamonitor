using FluentAssertions;
using KasaMonitor.Core.Reports;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using Xunit;

namespace KasaMonitor.Web.Tests;

/// <summary>The axis, the texts, the colours and the CSV of the „Putere” chart of /history (0.14.0).</summary>
public class PowerChartTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static ReportPeriod Day(int y, int m, int d) => ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(y, m, d));

    [Fact]
    public void Ticks_ADayByMinute_EveryTwoHours_TheAutumnDayLabelsItsRepeatedHourOnce()
    {
        var grid = PowerHistory.Grid(Day(2026, 11, 12), PowerResolution.Minute, Ro);
        var ticks = PowerChart.Ticks(grid, PowerResolution.Minute, Ro);
        ticks.Select(t => t.Label).Should().Equal("00:00", "02:00", "04:00", "06:00", "08:00", "10:00", "12:00", "14:00", "16:00", "18:00", "20:00", "22:00");
        ticks[1].Index.Should().Be(120);

        var autumn = PowerChart.Ticks(PowerHistory.Grid(Day(2026, 10, 25), PowerResolution.Minute, Ro), PowerResolution.Minute, Ro);
        autumn.Select(t => t.Label).Should().OnlyHaveUniqueItems();
        autumn[2].Index.Should().Be(5 * 60, "04:00 comes 5 hours after midnight on the 25 h day");
    }

    [Fact]
    public void Ticks_AWeekByHour_ByDayName_AMonthEveryThreeDays_AYearByMonth()
    {
        var week = ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 2));
        PowerChart.Ticks(PowerHistory.Grid(week, PowerResolution.Hour, Ro), PowerResolution.Hour, Ro).Select(t => t.Label)
            .Should().Equal("lun 2", "mar 3", "mie 4", "joi 5", "vin 6", "sâm 7", "dum 8");

        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 10, 1));
        var mt = PowerChart.Ticks(PowerHistory.Grid(month, PowerResolution.Hour, Ro), PowerResolution.Hour, Ro);
        mt.Should().HaveCountLessThanOrEqualTo(PowerChart.MaxTicks);
        mt.Select(t => t.Label).Should().StartWith(new[] { "1 oct", "4 oct" });

        var year = ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 1, 1));
        PowerChart.Ticks(PowerHistory.Grid(year, PowerResolution.Day, Ro), PowerResolution.Day, Ro).Select(t => t.Label)
            .Should().Equal("ian 2026", "feb", "mar", "apr", "mai", "iun", "iul", "aug", "sep", "oct", "nov", "dec");
    }

    [Fact]
    public void Titles_LocalTime_InRomanian()
    {
        var t = new DateTimeOffset(2026, 11, 12, 12, 37, 0, TimeSpan.Zero);
        PowerChart.PointTitle(t, PowerResolution.Minute, Ro).Should().Be("joi 12 nov, 14:37");
        PowerChart.PointTitle(t.AddMinutes(-37), PowerResolution.Hour, Ro).Should().Be("joi 12 nov, 14:00–15:00");
        PowerChart.PointTitle(new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.Zero), PowerResolution.Day, Ro).Should().Be("joi 12 nov 2026");
        PowerChart.When(t, PowerResolution.Minute, Ro).Should().Be("12 nov, 14:37");
        PowerChart.When(t.AddMinutes(-37), PowerResolution.Day, Ro).Should().Be("12 nov 2026, 14:00–15:00");
        PowerChart.RangeTitle(t.AddMinutes(-37), t.AddMinutes(-29), 8, PowerResolution.Minute, Ro).Should().Be("joi 12 nov, 14:00–14:08");
        PowerChart.RangeTitle(new DateTimeOffset(2026, 11, 11, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 14, 22, 0, 0, TimeSpan.Zero), 3,
            PowerResolution.Day, Ro).Should().Be("12–14 nov 2026");
    }

    private static HistoryPower Power(ReportPeriod period, CompareMode compare, int? nowIndex, Guid main, params Guid[] selected)
    {
        var resolution = PowerHistory.ResolutionFor(period);
        var grid = PowerHistory.Grid(period, resolution, Ro);
        var series = selected.Select((id, k) => new HistoryPowerSeries(id, period, resolution, grid,
            grid.Select((t, i) => i % 2 == 0 ? new PowerPoint(t, 100 * (k + 1), 250 * (k + 1), t, 60) : null).ToList())).ToList();
        var cp = PowerHistory.ComparisonPeriod(period, compare);
        var aligned = compare == CompareMode.None ? null : grid.Select(t => (PowerPoint?)new PowerPoint(t, 90, 120, t, 60)).ToList();
        var channels = new[] { new PowerChannel(main, "Casa", true) }.Concat(selected.Where(id => id != main).Select((id, k) => new PowerChannel(id, "Priză " + k, false))).ToList();
        return new HistoryPower(period, resolution, grid, channels, selected, series, compare, cp, cp is null ? null : PowerHistory.Reference(selected, main),
            aligned, aligned is null ? null : PowerHistory.Summary(aligned), nowIndex);
    }

    [Fact]
    public void ComparisonWords_PreviousLastYearToday()
    {
        var main = Guid.NewGuid();
        var nov = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));
        PowerChart.ComparisonWords(Power(nov, CompareMode.Previous, null, main, main)).Should().Be(("luna precedentă", "față de luna precedentă"));
        PowerChart.ComparisonWords(Power(nov, CompareMode.LastYear, null, main, main)).Should().Be(("anul trecut (noiembrie 2025)", "față de noiembrie 2025"));
        PowerChart.ComparisonWords(Power(nov, CompareMode.LastYear, 5, main, main)).Versus.Should().Be("față de aceeași parte din noiembrie 2025");
        PowerChart.ComparisonWords(Power(Day(2026, 11, 12), CompareMode.Previous, 5, main, main)).Should().Be(("ieri", "față de ieri la aceeași oră"));
    }

    [Fact]
    public void Colors_TheMainMeterItsOwn_PlugsTheNextFree_AsOnContor()
    {
        var main = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var colors = PowerChart.Colors(Power(Day(2026, 11, 12), CompareMode.None, null, main, a, main, b));
        colors[main].Should().Be(LiveChartColors.Main);
        colors[a].Should().Be(LiveChartColors.Overlays[0]);
        colors[b].Should().Be(LiveChartColors.Overlays[1]);
    }

    [Fact]
    public void Csv_OneRowPerBucketAndSeriesWithData_TheComparisonOnTheComparedSeries()
    {
        var main = Guid.NewGuid();
        var plug = Guid.NewGuid();
        var p = Power(Day(2026, 11, 12), CompareMode.LastYear, null, main, main, plug);
        var table = HistoryTables.Power(p, Ro);

        table.Headers.Should().Equal("Minutul", "Serie", "Medie W", "Max W", "Maxim la", "Minute cu date", "Comparație medie W", "Comparație max W");
        table.Rows.Should().HaveCount(720 * 2, "every second minute has data, for two series");
        table.Rows[0].Should().Equal("2026-11-12 00:00", "Casa", "100", "250", "2026-11-12 00:00", "60", "90", "120");
        table.Rows[1].Should().Equal("2026-11-12 00:00", "Priză 0", "200", "500", "2026-11-12 00:00", "60", "", "");

        var hours = HistoryTables.Power(Power(Day(2026, 10, 25), CompareMode.None, null, main, main) with { }, Ro);
        hours.Headers.Should().HaveCount(6, "no comparison columns without a comparison");
    }

    [Fact]
    public void Csv_TheRepeatedHourOfTheAutumnDay_IsMarked()
    {
        var main = Guid.NewGuid();
        var period = ReportPeriod.Between(new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 26));
        var grid = PowerHistory.Grid(period, PowerResolution.Hour, Ro);
        var series = new HistoryPowerSeries(main, period, PowerResolution.Hour, grid, grid.Select(t => (PowerPoint?)new PowerPoint(t, 1, 1, t, 60)).ToList());
        var p = new HistoryPower(period, PowerResolution.Hour, grid, new[] { new PowerChannel(main, "Casa", true) }, new[] { main }, new[] { series },
            CompareMode.None, null, null, null, null, null);
        var keys = HistoryTables.Power(p, Ro).Rows.Select(r => r[0]).ToList();
        keys.Should().HaveCount(73).And.Contain("2026-10-25 03:00").And.Contain("2026-10-25 03:00 (2)").And.OnlyHaveUniqueItems();
    }
}
