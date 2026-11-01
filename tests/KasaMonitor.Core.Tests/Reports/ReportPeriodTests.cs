using FluentAssertions;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.Reports;

public class ReportPeriodTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Theory]
    [InlineData("2026-11-01", "2026-10-26")] // Sunday → Monday before
    [InlineData("2026-10-26", "2026-10-26")] // Monday → itself
    [InlineData("2026-11-04", "2026-11-02")] // Wednesday
    [InlineData("2027-01-02", "2026-12-28")] // across the year
    public void Week_StartsOnMonday(string day, string monday)
    {
        var p = ReportPeriod.Containing(ReportGranularity.Week, DateOnly.Parse(day));
        p.Start.Should().Be(DateOnly.Parse(monday));
        p.Start.DayOfWeek.Should().Be(DayOfWeek.Monday);
        p.DayCount.Should().Be(7);
        p.Buckets(Ro).Should().HaveCount(7);
    }

    [Theory]
    [InlineData("2027-02-10", 28)]
    [InlineData("2028-02-29", 29)]
    [InlineData("2026-04-30", 30)]
    [InlineData("2026-11-01", 30)]
    [InlineData("2026-12-31", 31)]
    public void Month_Has28To31Days(string day, int days)
    {
        var p = ReportPeriod.Containing(ReportGranularity.Month, DateOnly.Parse(day));
        p.Start.Day.Should().Be(1);
        p.DayCount.Should().Be(days);
        p.Buckets(Ro).Should().HaveCount(days);
        p.LastDay.Should().Be(p.Start.AddDays(days - 1));
    }

    [Fact]
    public void Year_HasTwelveMonthBuckets()
    {
        var p = ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 11, 1));
        p.Start.Should().Be(new DateOnly(2026, 1, 1));
        p.EndExclusive.Should().Be(new DateOnly(2027, 1, 1));
        var buckets = p.Buckets(Ro);
        buckets.Should().HaveCount(12);
        buckets.Select(b => b.Day.Month).Should().Equal(Enumerable.Range(1, 12));
        // Each month bucket starts at the local midnight: 22:00 UTC in winter, 21:00 UTC in summer.
        buckets[0].StartUtc.Should().Be(new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero));
        buckets[6].StartUtc.Should().Be(new DateTimeOffset(2026, 6, 30, 21, 0, 0, TimeSpan.Zero));
        buckets[11].EndUtc.Should().Be(new DateTimeOffset(2026, 12, 31, 22, 0, 0, TimeSpan.Zero));
        p.BucketOfDay(new DateOnly(2026, 11, 1)).Should().Be(10);
    }

    [Fact]
    public void Previous_IsTheRightPeriod()
    {
        ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 3, 1)).Previous().Start.Should().Be(new DateOnly(2026, 2, 28));
        ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 1)).Previous().Start.Should().Be(new DateOnly(2026, 10, 19));
        var march = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2027, 3, 31));
        march.Previous().Should().Be(new ReportPeriod(ReportGranularity.Month, new DateOnly(2027, 2, 1)));
        march.Previous().DayCount.Should().Be(28);
        ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2027, 1, 15)).Previous().Start.Should().Be(new DateOnly(2026, 12, 1));
        ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 11, 1)).Previous().Start.Should().Be(new DateOnly(2025, 1, 1));
        // Next undoes Previous.
        march.Previous().Next().Should().Be(march);
    }

    [Fact]
    public void Day_HasLocalHours_23And25OnTheDstDays()
    {
        var normal = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 1)).Buckets(Ro);
        normal.Should().HaveCount(24);
        normal[0].StartUtc.Should().Be(new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero));
        normal.Select(b => b.LocalStart.Hour).Should().Equal(Enumerable.Range(0, 24));

        // 29.03.2026: 03:00 → 04:00, the day has 23 hours and no 03:00.
        var spring = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 3, 29)).Buckets(Ro);
        spring.Should().HaveCount(23);
        spring.Select(b => b.LocalStart.Hour).Should().NotContain(3);
        spring.Should().OnlyContain(b => !b.RepeatedHour);

        // 25.10.2026: 04:00 → 03:00, the day has 25 hours and 03:00 twice (the second one marked).
        var autumn = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 10, 25)).Buckets(Ro);
        autumn.Should().HaveCount(25);
        autumn.Count(b => b.LocalStart.Hour == 3).Should().Be(2);
        autumn.Single(b => b.RepeatedHour).LocalStart.Hour.Should().Be(3);
        autumn[^1].EndUtc.Should().Be(new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Week_AcrossDst_DayBucketsFollowLocalMidnights()
    {
        var week = ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 10, 25)).Buckets(Ro);
        week.Should().HaveCount(7);
        var sunday = week.Single(b => b.Day == new DateOnly(2026, 10, 25));
        (sunday.EndUtc - sunday.StartUtc).Should().Be(TimeSpan.FromHours(25));
        sunday.StartUtc.Should().Be(new DateTimeOffset(2026, 10, 24, 21, 0, 0, TimeSpan.Zero)); // EEST midnight
        sunday.EndUtc.Should().Be(new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero));   // EET midnight
    }

    [Theory]
    [InlineData(110.0, 100.0, 10.0)]
    [InlineData(50.0, 100.0, -50.0)]
    [InlineData(0.0, 2.0, -100.0)]
    public void DeltaPct_IsTheChangeInPercent(double current, double previous, double expected) =>
        ReportMath.DeltaPct(current, previous).Should().BeApproximately(expected, 1e-9);

    [Fact]
    public void DeltaPct_WithoutAPreviousValueOrAZeroOne_IsNull()
    {
        ReportMath.DeltaPct(5, 0).Should().BeNull();
        ReportMath.DeltaPct(0, 0).Should().BeNull();
        ReportMath.DeltaPct(5, null).Should().BeNull();
        ReportMath.DeltaPct(null, 5).Should().BeNull();
    }

    [Fact]
    public void SumOrNull_And_DiffersBy()
    {
        ReportMath.SumOrNull(new double?[] { null, null }).Should().BeNull();
        ReportMath.SumOrNull(new double?[] { null, 1.5, 2 }).Should().Be(3.5);
        ReportMath.DiffersBy(100, 106, 0.05).Should().BeTrue();
        ReportMath.DiffersBy(100, 104, 0.05).Should().BeFalse();
        ReportMath.DiffersBy(0, 0, 0.05).Should().BeFalse();
    }
}
