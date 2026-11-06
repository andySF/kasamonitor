using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data.Entities;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>„Putere” of /history (0.14.0) on a real SQLite database: only the minute and hour aggregates are read.</summary>
public class PowerHistoryServiceTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly CancellationToken Ct = CancellationToken.None;
    // 3 December 2026: November is finished.
    private static readonly DateTimeOffset Now = new(2026, 12, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly ReportPeriod November = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));

    private static async Task SeedHoursAsync(TestDbFactory f, Guid plug, ReportPeriod period, Func<int, long> avgMw)
    {
        using var ctx = f.CreateContext();
        var hours = PowerHistory.Grid(period, PowerResolution.Hour, Ro)
            .Select((t, i) => new PowerHour(t, avgMw(i), avgMw(i) * 2, 60, avgMw(i) / 1000)).ToList();
        await new PowerAggregateRepository(ctx).UpsertHoursAsync(plug, hours, Ct);
    }

    private static PowerHistoryService Service(TestDbFactory f, DateTimeOffset? now = null) =>
        new(new TestScopeFactory(f.CreateContext), () => Ro, clock: () => now ?? Now);

    [Fact]
    public async Task Month_ByHour_TheMainMeterByDefault_ComparedWithTheSameMonthLastYear()
    {
        using var f = new TestDbFactory();
        var main = await RollupWorkerTests.AddPlugAsync(f, "aabbccddee00", "Casa", PlugRole.MainMeter);
        var plug = await RollupWorkerTests.AddPlugAsync(f, "aabbccddee01", "Frigider");
        await SeedHoursAsync(f, main, November, i => i % 24 == 19 ? 5_000_000 : 500_000);
        await SeedHoursAsync(f, main, PowerHistory.LastYear(November), _ => 400_000);

        var p = await Service(f).GetAsync(November, CompareMode.LastYear, null, Ct);

        p.Resolution.Should().Be(PowerResolution.Hour);
        p.Channels.Select(c => c.Alias).Should().Equal("Casa", "Frigider");
        p.Selected.Should().Equal(main);
        p.Grid.Should().HaveCount(720);
        var s = p.Series.Single().Summary!;
        s.MaxW.Should().Be(10_000);
        TimeZoneInfo.ConvertTime(s.MaxAtUtc, Ro).Hour.Should().Be(19, "the evening peak, local time");
        s.AvgW.Should().BeApproximately((23 * 500 + 5000) / 24.0, 1e-6);
        p.ComparisonPeriod.Should().Be(ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2025, 11, 1)));
        p.ComparedPlugId.Should().Be(main);
        p.ComparisonAligned.Should().HaveCount(720).And.OnlyContain(x => x != null && x.AvgW == 400);
        p.ComparisonSummary!.AvgW.Should().Be(400);
        p.NowIndex.Should().BeNull();
    }

    [Fact]
    public async Task Selection_KeepsTheAddressOrder_ComparesTheMainMeter_UnknownIdsDropped_AYearIsDaily()
    {
        using var f = new TestDbFactory();
        var main = await RollupWorkerTests.AddPlugAsync(f, "aabbccddee00", "Casa", PlugRole.MainMeter);
        var plug = await RollupWorkerTests.AddPlugAsync(f, "aabbccddee01", "Frigider");
        await SeedHoursAsync(f, plug, November, _ => 80_000);
        var year = ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 1, 1));

        var p = await Service(f).GetAsync(year, CompareMode.Previous, new[] { plug, Guid.NewGuid(), main }, Ct);

        p.Resolution.Should().Be(PowerResolution.Day);
        p.Selected.Should().Equal(plug, main);
        p.ComparedPlugId.Should().Be(main, "the main meter when it is shown");
        var fridge = p.Series[0].Points;
        fridge.Should().HaveCount(365);
        fridge.Count(x => x != null).Should().Be(30, "the days of November, from their hours");
        fridge.First(x => x != null)!.Minutes.Should().Be(24 * 60);
        fridge.First(x => x != null)!.AvgW.Should().Be(80);
        p.NowIndex.Should().NotBeNull("2026 is in progress on 3 December");

        var none = await Service(f).GetAsync(year, CompareMode.LastYear, Array.Empty<Guid>(), Ct);
        none.Series.Should().BeEmpty();
        none.ComparisonAligned.Should().BeNull();
    }

    [Fact]
    public async Task Today_ByMinute_ComparedWithYesterdayUpToNow()
    {
        using var f = new TestDbFactory();
        var main = await RollupWorkerTests.AddPlugAsync(f, "aabbccddee00", "Casa", PlugRole.MainMeter);
        var today = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 12, 3));
        using (var ctx = f.CreateContext())
        {
            // Yesterday: 300 W all day, 6 kW at 20:00; today: 500 W until now (12:00 local).
            foreach (var t in PowerHistory.Grid(today.Previous(), PowerResolution.Minute, Ro))
                ctx.OneMinuteAggregates.Add(new OneMinuteAggregateEntity { PlugId = main, BucketStartUtc = t, PAvgMw = 300_000,
                    PMaxMw = TimeZoneInfo.ConvertTime(t, Ro).Hour == 20 ? 6_000_000 : 300_000, SampleCount = 6, OnlineCount = 6 });
            foreach (var t in PowerHistory.Grid(today, PowerResolution.Minute, Ro).Where(t => t < Now))
                ctx.OneMinuteAggregates.Add(new OneMinuteAggregateEntity { PlugId = main, BucketStartUtc = t, PAvgMw = 500_000, PMaxMw = 500_000, SampleCount = 6, OnlineCount = 6 });
            await ctx.SaveChangesAsync(Ct);
        }

        var p = await Service(f).GetAsync(today, CompareMode.Previous, null, Ct);

        p.Resolution.Should().Be(PowerResolution.Minute);
        p.Series.Single().Points.Count(x => x != null).Should().Be(12 * 60);
        p.NowIndex.Should().Be(12 * 60);
        p.ComparisonSummary!.MaxW.Should().Be(300, "yesterday's 20:00 is after the time of now");
        p.ComparisonSummary.Minutes.Should().Be(12 * 60 + 1);
    }
}
