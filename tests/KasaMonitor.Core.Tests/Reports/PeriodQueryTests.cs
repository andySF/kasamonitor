using FluentAssertions;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.Reports;

/// <summary>The period in the address of /history and the home page (0.9.2).</summary>
public class PeriodQueryTests
{
    private static readonly DateOnly Today = new(2026, 11, 2);

    [Theory]
    [InlineData("luna", "2026-10", ReportGranularity.Month, "2026-10-01")]
    [InlineData("luna", "2026-10-17", ReportGranularity.Month, "2026-10-01")]
    [InlineData("an", "2025", ReportGranularity.Year, "2025-01-01")]
    [InlineData("saptamana", "2026-10-28", ReportGranularity.Week, "2026-10-26")]
    [InlineData("zi", "2026-10-31", ReportGranularity.Day, "2026-10-31")]
    public void Parse_TheKindAndAnyDayOfThePeriod(string p, string d, ReportGranularity kind, string start)
    {
        var period = PeriodQuery.Parse(p, d, null, null, Today);
        period.Kind.Should().Be(kind);
        period.Start.Should().Be(DateOnly.Parse(start));
    }

    [Fact]
    public void Format_IsTheShortestForm_AndParsesBack()
    {
        var periods = new[]
        {
            ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 2)),
            ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 3, 1)),
            ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 10, 29)),
            ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 10, 30)),
            ReportPeriod.Between(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20)),
        };
        PeriodQuery.Format(periods[0]).Should().Be("p=luna&d=2026-11");
        PeriodQuery.Format(periods[1]).Should().Be("p=an&d=2026");
        PeriodQuery.Format(periods[2]).Should().Be("p=saptamana&d=2026-10-26");
        PeriodQuery.Format(periods[3]).Should().Be("p=zi&d=2026-10-30");
        PeriodQuery.Format(periods[4]).Should().Be("p=interval&de=2026-10-01&pana=2026-10-20");
        foreach (var p in periods)
        {
            var q = System.Web.HttpUtility.ParseQueryString(PeriodQuery.Format(p));
            PeriodQuery.Parse(q["p"], q["d"], q["de"], q["pana"], Today).Should().Be(p);
        }
    }

    [Fact]
    public void Parse_MissingInvalidOrFuture_FallsBackToTheCurrentPeriodOfTheFallbackKind()
    {
        PeriodQuery.Parse(null, null, null, null, Today, ReportGranularity.Month).Should().Be(ReportPeriod.Containing(ReportGranularity.Month, Today));
        PeriodQuery.Parse("nimic", "x", null, null, Today).Should().Be(ReportPeriod.Containing(ReportGranularity.Day, Today));
        PeriodQuery.Parse("luna", "2027-01", null, null, Today).Should().Be(ReportPeriod.Containing(ReportGranularity.Month, Today), "the future is not shown");
        PeriodQuery.Parse("luna", "abc", null, null, Today).Should().Be(ReportPeriod.Containing(ReportGranularity.Month, Today));
    }

    [Fact]
    public void Range_IsOrdered_EndsToday_AndWithoutDatesFallsBack()
    {
        PeriodQuery.Parse("interval", null, "2026-10-20", "2026-10-01", Today).Should().Be(ReportPeriod.Between(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 20)));
        PeriodQuery.Parse("interval", null, "2026-10-20", "2026-12-31", Today).LastDay.Should().Be(Today);
        PeriodQuery.Parse("interval", null, null, "2026-10-01", Today, ReportGranularity.Month).Should().Be(ReportPeriod.Containing(ReportGranularity.Month, Today));
    }

    [Fact]
    public void History_CarriesThePeriodTheComparisonThePlugAndTheSection()
    {
        var month = ReportPeriod.Containing(ReportGranularity.Month, Today);
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        PeriodQuery.History(month).Should().Be("/history?p=luna&d=2026-11");
        PeriodQuery.History(month, compare: false, plug: id, anchor: "curent").Should().Be("/history?p=luna&d=2026-11&cmp=0&plug=" + id + "#curent");
        PeriodQuery.Compare(null).Should().BeTrue();
        PeriodQuery.Compare("1").Should().BeTrue();
        PeriodQuery.Compare("0").Should().BeFalse();
    }

    [Fact]
    public void CompareMode_None_Previous_LastYear_AndLastYearKeepsTheOtherSectionsComparing()
    {
        PeriodQuery.CompareModeOf(null).Should().Be(CompareMode.Previous);
        PeriodQuery.CompareModeOf("1").Should().Be(CompareMode.Previous);
        PeriodQuery.CompareModeOf("nu").Should().Be(CompareMode.None);
        PeriodQuery.CompareModeOf(" AN ").Should().Be(CompareMode.LastYear);
        PeriodQuery.Compare("an").Should().BeTrue("the other sections keep their Δ against the previous period");
        PeriodQuery.CompareParam(CompareMode.Previous).Should().BeEmpty();
        PeriodQuery.CompareParam(CompareMode.LastYear).Should().Be("&cmp=an");
    }

    [Fact]
    public void PowerSeries_InTheAddress_IdsDashForNone_GarbageIsTheDefault()
    {
        var a = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var b = Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
        PeriodQuery.PowerSeries(null).Should().BeNull();
        PeriodQuery.PowerSeries("nimic").Should().BeNull();
        PeriodQuery.PowerSeries("-").Should().BeEmpty();
        PeriodQuery.PowerSeries($"{a},x,{b},{a}").Should().Equal(a, b);
        PeriodQuery.PowerParam(null).Should().BeEmpty();
        PeriodQuery.PowerParam(Array.Empty<Guid>()).Should().Be("&pw=-");

        var month = ReportPeriod.Containing(ReportGranularity.Month, Today);
        PeriodQuery.History(month, CompareMode.LastYear, null, new[] { a, b }, "putere")
            .Should().Be($"/history?p=luna&d=2026-11&cmp=an&pw={a},{b}#putere");
    }
}
