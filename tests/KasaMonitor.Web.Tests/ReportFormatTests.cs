using FluentAssertions;
using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Reports;
using KasaMonitor.Web.Ui;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class ReportFormatTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public void Titles_AreRomanian()
    {
        ReportFormat.Title(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 1))).Should().Be("duminică, 1 noiembrie 2026");
        ReportFormat.Title(ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 1))).Should().Be("26 oct – 1 nov 2026");
        ReportFormat.Title(ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2027, 1, 1))).Should().Be("28 dec 2026 – 3 ian 2027");
        ReportFormat.Title(ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1))).Should().Be("noiembrie 2026");
        ReportFormat.Title(ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 11, 1))).Should().Be("2026");
    }

    [Theory]
    [InlineData(2026, 11, 2)]
    [InlineData(2026, 7, 15)]
    public void BoilerDayNote_EndsAt01RomanianTime(int y, int m, int d)
    {
        ReportFormat.BoilerDayNote(KasaMonitor.Core.Heating.BoilerClock.DefaultTimeZone, Ro, new DateOnly(y, m, d))
            .Should().Be("zi = după ceasul centralei (CET), se încheie la 01:00 ora României");
    }

    [Fact]
    public void BucketLabels_MarkTheRepeatedDstHour()
    {
        var p = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 10, 25));
        var labels = p.Buckets(Ro).Select(b => ReportFormat.BucketLabel(b, p.Kind)).ToList();
        labels.Should().HaveCount(25).And.OnlyHaveUniqueItems().And.Contain("03:00").And.Contain("03:00*");
        var w = ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 1));
        ReportFormat.BucketLabel(w.Buckets(Ro)[0], w.Kind).Should().Be("lun 26");
    }

    [Fact]
    public void Delta_ArrowsAndNoComparison()
    {
        ReportFormat.Delta(12.4).Should().Be("▲ 12 %");
        ReportFormat.Delta(-3.46).Should().Be("▼ 3,5 %");
        ReportFormat.Delta(null).Should().BeNull();
        ReportFormat.DeltaClass(5).Should().Be("text-danger");
        ReportFormat.DeltaClass(-5).Should().Be("text-success");
    }

    [Fact]
    public void RestOfTheHouse_ShowsAboutZeroWithinTheMargin_AndZeroWhenNegative()
    {
        ReportFormat.RestW(HouseBalance.Balance(1000, 400)).Should().Be("600 W");
        ReportFormat.RestW(HouseBalance.Balance(460, 470)).Should().Be("≈ 0 W");
        ReportFormat.RestW(HouseBalance.Balance(460, 500)).Should().Be("0 W");
        ReportFormat.RestTitle(HouseBalance.Balance(460, 500)).Should().StartWith(ReportFormat.NegativeText);
        ReportFormat.RestTitle(HouseBalance.Balance(460, 470)).Should().StartWith(ReportFormat.WithinMarginText);
        ReportFormat.RestW(null).Should().Be("—");
        ReportFormat.RestKwh(0.12, RestKind.WithinMargin).Should().Be("≈ 0,12");
        ReportFormat.RestKwh(0, RestKind.Negative).Should().Be("0 ⚠");
    }
}
