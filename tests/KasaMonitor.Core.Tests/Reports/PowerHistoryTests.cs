using FluentAssertions;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.Reports;

/// <summary>The rules of the „Putere” chart of /history (0.14.0): resolution, grid, points, hourly roll-up, last year, alignment.</summary>
public class PowerHistoryTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    private static ReportPeriod Day(int y, int m, int d) => ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(y, m, d));

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi = 0) => new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, PowerResolution.Minute)]
    [InlineData(2, PowerResolution.Minute)]
    [InlineData(3, PowerResolution.Hour)]
    [InlineData(7, PowerResolution.Hour)]
    [InlineData(31, PowerResolution.Hour)]
    [InlineData(45, PowerResolution.Hour)]
    [InlineData(46, PowerResolution.Day)]
    [InlineData(366, PowerResolution.Day)]
    public void Resolution_TwoDaysByMinute_45DaysByHour_LongerByDay(int days, PowerResolution expected) =>
        PowerHistory.ResolutionFor(days).Should().Be(expected);

    [Fact]
    public void Resolution_OfThePeriodKinds()
    {
        PowerHistory.ResolutionFor(Day(2026, 11, 12)).Should().Be(PowerResolution.Minute);
        PowerHistory.ResolutionFor(ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 12))).Should().Be(PowerResolution.Hour);
        PowerHistory.ResolutionFor(ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 2, 1))).Should().Be(PowerResolution.Hour);
        PowerHistory.ResolutionFor(ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 1, 1))).Should().Be(PowerResolution.Day);
        PowerHistory.ResolutionFor(ReportPeriod.Between(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2))).Should().Be(PowerResolution.Minute);
        PowerHistory.ResolutionFor(ReportPeriod.Between(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 14))).Should().Be(PowerResolution.Hour);
        PowerHistory.ResolutionFor(ReportPeriod.Between(new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 15))).Should().Be(PowerResolution.Day);
    }

    [Fact]
    public void Grid_OfTheDstDays_Has23And25Hours()
    {
        PowerHistory.Grid(Day(2026, 11, 12), PowerResolution.Minute, Ro).Should().HaveCount(1440).And.StartWith(Utc(2026, 11, 11, 22));
        PowerHistory.Grid(Day(2026, 3, 29), PowerResolution.Minute, Ro).Should().HaveCount(23 * 60);
        PowerHistory.Grid(Day(2026, 10, 25), PowerResolution.Minute, Ro).Should().HaveCount(25 * 60);
        PowerHistory.Grid(Day(2026, 3, 29), PowerResolution.Hour, Ro).Should().HaveCount(23);
        PowerHistory.Grid(Day(2026, 10, 25), PowerResolution.Hour, Ro).Should().HaveCount(25);
        PowerHistory.Grid(ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1)), PowerResolution.Hour, Ro).Should().HaveCount(720);
        var leap = PowerHistory.Grid(ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2028, 1, 1)), PowerResolution.Day, Ro);
        leap.Should().HaveCount(366);
        leap[0].Should().Be(Utc(2027, 12, 31, 22));
        leap[100].Should().Be(Utc(2028, 4, 9, 21), "local midnights: summer time from 26 March");
    }

    [Fact]
    public void Points_WeighByMinutes_KeepTheMaxAndItsTime_GapsStayEmpty()
    {
        var day = Day(2026, 11, 12);
        var grid = PowerHistory.Grid(day, PowerResolution.Hour, Ro);
        var rows = new[]
        {
            new PowerAggregate(Utc(2026, 11, 11, 22, 0), 1_000_000, 2_000_000, 1),
            new PowerAggregate(Utc(2026, 11, 11, 22, 1), 3_000_000, 5_000_000, 1),
            new PowerAggregate(Utc(2026, 11, 11, 23, 0), 1_000_000, 1_500_000, 60),
            new PowerAggregate(Utc(2026, 11, 11, 23, 30), 4_000_000, 4_500_000, 30),
            new PowerAggregate(Utc(2026, 11, 11, 21, 59), 9_000_000, 9_000_000, 1),   // before the period
            new PowerAggregate(Utc(2026, 11, 12, 22, 0), 9_000_000, 9_000_000, 1),    // after it
        };
        var points = PowerHistory.Points(grid, day.EndUtc(Ro), rows);

        points.Should().HaveCount(24);
        points[0]!.AvgW.Should().Be(2000);
        points[0]!.MaxW.Should().Be(5000);
        points[0]!.MaxAtUtc.Should().Be(Utc(2026, 11, 11, 22, 1));
        points[0]!.Minutes.Should().Be(2);
        points[1]!.AvgW.Should().Be(2000, "(1000 W × 60 + 4000 W × 30) / 90");
        points[1]!.Minutes.Should().Be(90);
        points.Skip(2).Should().OnlyContain(p => p == null, "no data is a gap, not a zero");
    }

    [Fact]
    public void Summary_IsMinuteWeighted_AndNullWithoutData()
    {
        var points = new PowerPoint?[]
        {
            new(Utc(2026, 1, 1, 0), 100, 300, Utc(2026, 1, 1, 0, 10), 60),
            null,
            new(Utc(2026, 1, 1, 2), 400, 900, Utc(2026, 1, 1, 2, 5), 30),
        };
        var s = PowerHistory.Summary(points)!;
        s.AvgW.Should().Be(200);
        s.MaxW.Should().Be(900);
        s.MaxAtUtc.Should().Be(Utc(2026, 1, 1, 2, 5));
        PowerHistory.Summary(new PowerPoint?[] { null, null }).Should().BeNull();
    }

    [Fact]
    public void HoursFromMinutes_MeanOfTheMinutes_MaxOfTheMaxima_EnergyOfTheMinutesPresent()
    {
        var minutes = Enumerable.Range(0, 30).Select(i => new PowerAggregate(Utc(2026, 11, 12, 10, i), 1_000_000, 1_200_000, 1))
            .Concat(Enumerable.Range(30, 30).Select(i => new PowerAggregate(Utc(2026, 11, 12, 10, i), 3_000_000, i == 45 ? 7_000_000 : 3_100_000, 1)))
            .Concat(Enumerable.Range(0, 45).Select(i => new PowerAggregate(Utc(2026, 11, 12, 11, i), 2_000_000, 2_000_000, 1)))
            .Reverse();

        var hours = PowerHistory.HoursFromMinutes(minutes);

        hours.Should().HaveCount(2);
        hours[0].Should().Be(new PowerHour(Utc(2026, 11, 12, 10), 2_000_000, 7_000_000, 60, 2000));
        hours[1].Should().Be(new PowerHour(Utc(2026, 11, 12, 11), 2_000_000, 2_000_000, 45, 1500), "45 minutes at 2 kW: 1.5 kWh");
    }

    [Fact]
    public void Replaces_OnlyWithAtLeastAsMuchData()
    {
        PowerHistory.Replaces(storedCount: 6, newCount: 6).Should().BeTrue();
        PowerHistory.Replaces(storedCount: 3, newCount: 6).Should().BeTrue();
        PowerHistory.Replaces(storedCount: 60, newCount: 12).Should().BeFalse("readings deleted by the retention must not shrink the hour");
    }

    [Theory]
    [InlineData(ReportGranularity.Day, "2026-11-12", "2025-11-12", "2025-11-12")]
    [InlineData(ReportGranularity.Day, "2028-02-29", "2027-02-28", "2027-02-28")]
    [InlineData(ReportGranularity.Week, "2026-11-02", "2025-11-03", "2025-11-09")]
    [InlineData(ReportGranularity.Month, "2026-11-01", "2025-11-01", "2025-11-30")]
    [InlineData(ReportGranularity.Month, "2028-02-01", "2027-02-01", "2027-02-28")]
    [InlineData(ReportGranularity.Year, "2026-01-01", "2025-01-01", "2025-12-31")]
    public void LastYear_IsCalendarAware(ReportGranularity kind, string start, string expectedStart, string expectedLast)
    {
        var shifted = PowerHistory.LastYear(ReportPeriod.Containing(kind, DateOnly.Parse(start)));
        shifted.Start.Should().Be(DateOnly.Parse(expectedStart));
        shifted.LastDay.Should().Be(DateOnly.Parse(expectedLast));
        if (kind == ReportGranularity.Week) shifted.Start.DayOfWeek.Should().Be(DayOfWeek.Monday, "52 weeks back: weekdays meet weekdays");
    }

    [Fact]
    public void LastYear_OfARange_TheSameDates_29FebruaryTo28()
    {
        var range = ReportPeriod.Between(new DateOnly(2028, 2, 10), new DateOnly(2028, 2, 29));
        PowerHistory.LastYear(range).Should().Be(ReportPeriod.Between(new DateOnly(2027, 2, 10), new DateOnly(2027, 2, 28)));
        PowerHistory.ComparisonPeriod(range, CompareMode.Previous).Should().Be(range.Previous());
        PowerHistory.ComparisonPeriod(range, CompareMode.None).Should().BeNull();
    }

    private static IReadOnlyList<PowerPoint?> Hourly(ReportPeriod day) =>
        PowerHistory.Grid(day, PowerResolution.Hour, Ro).Select((t, i) => (PowerPoint?)new PowerPoint(t, i, i, t, 60)).ToList();

    [Fact]
    public void Align_SpringDstDay_ByWallClock_TheSkippedHourOfTheComparisonHasNoSlot()
    {
        var current = Day(2026, 3, 29);    // 23 h: 03:00 local does not exist
        var previous = Day(2026, 3, 28);
        var grid = PowerHistory.Grid(current, PowerResolution.Hour, Ro);
        var prevGrid = PowerHistory.Grid(previous, PowerResolution.Hour, Ro);
        var aligned = PowerHistory.Align(current, grid, previous, prevGrid, Hourly(previous), Ro);

        aligned.Should().HaveCount(23);
        aligned[2]!.AvgW.Should().Be(2, "02:00 meets 02:00");
        aligned[3]!.AvgW.Should().Be(4, "the current 04:00 meets yesterday's 04:00 (index 4), not 03:00");
        aligned[22]!.AvgW.Should().Be(23);
    }

    [Fact]
    public void Align_AutumnDstDay_TheRepeatedHourMeetsTheFirst0300()
    {
        var current = Day(2026, 10, 25);   // 25 h: 03:00 twice
        var previous = Day(2026, 10, 24);
        var aligned = PowerHistory.Align(current, PowerHistory.Grid(current, PowerResolution.Hour, Ro), previous,
            PowerHistory.Grid(previous, PowerResolution.Hour, Ro), Hourly(previous), Ro);

        aligned.Should().HaveCount(25);
        aligned[3]!.AvgW.Should().Be(3);
        aligned[4]!.AvgW.Should().Be(3, "the second 03:00");
        aligned[5]!.AvgW.Should().Be(4);
        aligned[24]!.AvgW.Should().Be(23);
    }

    [Fact]
    public void Align_ByDayOfThePeriod_AShorterComparisonLeavesTheLastDaysEmpty()
    {
        var march = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2027, 3, 1));
        var feb = march.Previous();
        var grid = PowerHistory.Grid(march, PowerResolution.Day, Ro);
        var febGrid = PowerHistory.Grid(feb, PowerResolution.Day, Ro);
        var febPoints = febGrid.Select((t, i) => (PowerPoint?)new PowerPoint(t, i + 1, i + 1, t, 1440)).ToList();

        var aligned = PowerHistory.Align(march, grid, feb, febGrid, febPoints, Ro);

        aligned.Should().HaveCount(31);
        aligned[27]!.AvgW.Should().Be(28, "28 March meets 28 February (and 28 March is summer time, February not: by wall clock)");
        aligned.Skip(28).Should().OnlyContain(p => p == null, "February 2027 has no 29th–31st");
    }

    [Fact]
    public void Align_LastYearOf29February_ByMinute()
    {
        var current = Day(2028, 2, 29);
        var last = PowerHistory.LastYear(current);
        var grid = PowerHistory.Grid(current, PowerResolution.Minute, Ro);
        var lastGrid = PowerHistory.Grid(last, PowerResolution.Minute, Ro);
        var points = lastGrid.Select((t, i) => (PowerPoint?)new PowerPoint(t, i, i, t, 1)).ToList();

        var aligned = PowerHistory.Align(current, grid, last, lastGrid, points, Ro);
        aligned.Should().HaveCount(1440);
        aligned[14 * 60 + 37]!.StartUtc.Should().Be(new DateTimeOffset(2027, 2, 28, 12, 37, 0, TimeSpan.Zero), "14:37 local of 28 February 2027");
    }

    [Fact]
    public void ComparisonSummary_InProgress_TheSamePartOfTheComparison()
    {
        var full = new PowerPoint?[]
        {
            new(Utc(2026, 1, 1, 0), 100, 100, Utc(2026, 1, 1, 0), 60),
            new(Utc(2026, 1, 1, 1), 300, 300, Utc(2026, 1, 1, 1), 60),
            new(Utc(2026, 1, 1, 2), 900, 5000, Utc(2026, 1, 1, 2), 60),
        };
        PowerHistory.ComparisonSummary(full, full, nowIndex: 1)!.AvgW.Should().Be(200);
        PowerHistory.ComparisonSummary(full, full, nowIndex: 1)!.MaxW.Should().Be(300, "the evening peak after now is not counted yet");
        PowerHistory.ComparisonSummary(full, new PowerPoint?[3], nowIndex: null)!.MaxW.Should().Be(5000, "a finished period: the whole comparison");
    }

    [Fact]
    public void NowIndex_AndReference()
    {
        var day = Day(2026, 11, 12);
        var grid = PowerHistory.Grid(day, PowerResolution.Minute, Ro);
        PowerHistory.NowIndex(grid, day.EndUtc(Ro), Utc(2026, 11, 12, 10, 30)).Should().Be(12 * 60 + 30);
        PowerHistory.NowIndex(grid, day.EndUtc(Ro), Utc(2026, 11, 12, 22, 0)).Should().BeNull();

        var main = Guid.NewGuid();
        var plug = Guid.NewGuid();
        PowerHistory.Reference(new[] { plug, main }, main).Should().Be(main);
        PowerHistory.Reference(new[] { plug }, main).Should().Be(plug);
        PowerHistory.Reference(Array.Empty<Guid>(), main).Should().BeNull();
    }
}
