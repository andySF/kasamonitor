using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Home;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using KasaMonitor.Workers.Nilm;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class HomeServiceTests : IDisposable
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    // 02.11.2026 15:35 local (EET, UTC+2).
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 13, 35, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TodayStart = new(2026, 11, 1, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset YesterdayStart = new(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private DateTimeOffset _now = Now;
    private Guid _main, _fridge, _tv;

    public HomeServiceTests()
    {
        _conn.Open();
        using var ctx = Create();
        ctx.Database.EnsureCreated();
    }

    public void Dispose() => _conn.Dispose();

    private KasaDbContext Create() => new(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(_conn).Options);

    private static Guid AddPlug(KasaDbContext ctx, string alias, PlugRole role, int n)
    {
        var id = Guid.NewGuid();
        ctx.Plugs.Add(new PlugEntity
        {
            Id = id, Mac = $"AA:BB:CC:00:00:{n:X2}", IpAddress = "192.168.1." + n, Alias = alias, Protocol = "shelly",
            Role = (int)role, FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch,
        });
        return id;
    }

    private static void Minute(KasaDbContext ctx, Guid plug, DateTimeOffset utc, double avgW, double wh) =>
        ctx.LiveMinuteAggregates.Add(new LiveMinuteAggregateEntity
        {
            PlugId = plug, MinuteTs = utc.ToUnixTimeSeconds(), MinW = avgW, AvgW = avgW, MaxW = avgW, EnergyWh = wh, SampleCount = 60, CoveredSeconds = 60,
        });

    /// <summary>
    /// Yesterday: the main meter 1 Wh per minute all day (1,44 kWh), the plugs 0 Wh (at night 200 / 50 / 10 W). Today up to 15:35: main 2 Wh/min at 200 W, the fridge
    /// 0,5 Wh/min at 50 W, the TV 0 Wh at 10 W.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var ctx = Create();
        _main = AddPlug(ctx, "Contor", PlugRole.MainMeter, 1);
        _fridge = AddPlug(ctx, "Frigider", PlugRole.Plug, 2);
        _tv = AddPlug(ctx, "TV", PlugRole.Plug, 3);
        for (var t = YesterdayStart; t < TodayStart; t = t.AddMinutes(1))
        {
            Minute(ctx, _main, t, 200, 1);
            Minute(ctx, _fridge, t, 50, 0);
            Minute(ctx, _tv, t, 10, 0);
        }
        for (var t = TodayStart; t < Now; t = t.AddMinutes(1))
        {
            Minute(ctx, _main, t, 200, 2);
            Minute(ctx, _fridge, t, 50, 0.5);
            Minute(ctx, _tv, t, 10, 0);
        }
        await ctx.SaveChangesAsync();
    }

    private HomeService Service(Func<DateTimeOffset, DateTimeOffset, CancellationToken, Task<IReadOnlyList<ApplianceEnergyRow>>>? detected = null,
        TimeZoneInfo? boiler = null, HeatingState? heating = null)
    {
        var scopes = new TestScopeFactory(Create);
        var reports = new EnergyReportService(scopes, Ro, () => _now, boilerTimeZone: () => boiler ?? Ro);
        return new HomeService(scopes, reports, TestSettings.Create(scopes), heating: heating, clock: () => _now,
            detectedEnergy: detected ?? ((_, _, _) => Task.FromResult<IReadOnlyList<ApplianceEnergyRow>>(
                new[] { new ApplianceEnergyRow(7, "Boiler", "boiler", "bi-droplet", 300) })));
    }

    [Fact]
    public async Task Today_ComparedWithYesterdayAtTheSameTime_NotWithTheWholeDay()
    {
        await SeedAsync();
        var home = Service();
        var day = await home.GetAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2)), CancellationToken.None);

        day.SameTime.Should().NotBeNull();
        day.SameTime!.SameTimeUtc.Should().Be(new DateTimeOffset(2026, 11, 1, 13, 35, 0, TimeSpan.Zero));
        day.SameTime.TodayKwh.Should().BeApproximately(935 * 2 / 1000.0, 1e-9);
        day.SameTime.YesterdaySameTimeKwh.Should().BeApproximately(935 / 1000.0, 1e-9, "only yesterday's minutes before 15:35");
        day.SameTime.YesterdayKwh.Should().BeApproximately(1.44, 1e-9);
        day.SameTime.DeltaPct.Should().BeApproximately(100, 1e-9);
        day.Profile.Should().NotBeNull().And.HaveCount(24);

        var yesterday = await home.GetAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 1)), CancellationToken.None);
        yesterday.SameTime.Should().BeNull("only the current day is compared at the same time");
    }

    [Fact]
    public async Task Flows_PlugsDetectedAndUndetected_AddUpToTheMeter()
    {
        await SeedAsync();
        var day = await Service().GetAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2)), CancellationToken.None);

        var f = day.Flows!;
        f.MeterKwh.Should().BeApproximately(1.87, 1e-9);
        f.Rows.Single(r => r.Name == "Frigider").Kwh.Should().BeApproximately(0.4675, 1e-9);
        f.Rows.Single(r => r.Name == "Boiler").Kind.Should().Be(FlowKind.Detected);
        f.UndetectedKwh.Should().BeApproximately(1.87 - 0.4675 - 0.3, 1e-9);
        f.FlowsKwh.Should().BeApproximately(f.MeterKwh, 1e-9);
        day.House!.PlugTotals!.Select(p => p.Alias).Should().BeEquivalentTo("Frigider", "TV");
    }

    [Fact]
    public async Task Flows_ASingleAppliancePlug_StandsForItsAppliance_TheReportKeepsTheAlias()
    {
        await SeedAsync();
        await using (var ctx = Create())
            (await new KasaMonitor.Data.Repositories.PlugRepository(ctx).SetApplianceAsync(_fridge, true, "Combină frigorifică", "fridge", CancellationToken.None))
                .Should().BeTrue();
        var day = await Service().GetAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2)), CancellationToken.None);

        var row = day.Flows!.Rows.Single(r => r.Key == _fridge.ToString());
        row.Name.Should().Be("Combină frigorifică", "the bars and the Sankey show the appliance");
        row.Kwh.Should().BeApproximately(0.4675, 1e-9);
        day.Flows.Rows.Should().Contain(r => r.Name == "TV", "a plain plug keeps its alias");
        day.House!.PlugTotals!.Select(p => p.Alias).Should().BeEquivalentTo("Frigider", "TV");
    }

    [Fact]
    public async Task Period_IsCachedForAMinute_ThenServedStaleWhileOneRefreshRuns()
    {
        await SeedAsync();
        var home = Service();
        var period = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2));
        var a = await home.GetAsync(period, CancellationToken.None);
        (await home.GetAsync(period, CancellationToken.None)).Should().BeSameAs(a);
        _now = Now.AddMinutes(2);
        (await home.GetAsync(period, CancellationToken.None)).Should().BeSameAs(a, "expired: the old value is served while it is recomputed");
        await home.RefreshingAsync(period);
        (await home.GetAsync(period, CancellationToken.None)).Should().NotBeSameAs(a, "the refresh replaced it");
    }

    [Fact]
    public async Task ConcurrentCallers_ShareOneComputation_OffTheCallersThread()
    {
        await SeedAsync();
        var calls = 0;
        var gate = new TaskCompletionSource<IReadOnlyList<ApplianceEnergyRow>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var home = Service(detected: (_, _, _) => { Interlocked.Increment(ref calls); return gate.Task; });
        var period = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2));

        var first = home.GetAsync(period, CancellationToken.None);
        var second = home.GetAsync(period, CancellationToken.None);
        first.IsCompleted.Should().BeFalse("the computation runs on the thread pool, the caller is not blocked");
        await Task.Delay(200);
        gate.SetResult(Array.Empty<ApplianceEnergyRow>());
        (await first).Should().BeSameAs(await second);
        calls.Should().Be(1, "one computation for both callers");
    }

    [Fact]
    public async Task SameTime_CutsBothDaysAtTheStartOfTheCurrentMinute()
    {
        await SeedAsync();
        // 15:35:40: today's minute 15:35 has no aggregate yet; yesterday must not count its 15:35 minute either.
        _now = Now.AddSeconds(40);
        var day = await Service().GetAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2)), CancellationToken.None);
        day.SameTime!.YesterdaySameTimeKwh.Should().BeApproximately(935 / 1000.0, 1e-9, "minutes 00:00–15:34 only, as today");
        day.SameTime.TodayKwh.Should().BeApproximately(935 * 2 / 1000.0, 1e-9);
    }

    [Fact]
    public async Task Standby_IsTheNightFloorOfTheRest_AndTheMetricsHaveTheBillEstimate()
    {
        await SeedAsync();
        var home = Service();

        var standby = await home.GetStandbyAsync(CancellationToken.None);
        standby!.Watts.Should().Be(140, "200 W main − 50 W fridge − 10 W TV between 01:00 and 05:00");
        standby.Nights.Should().Be(2);

        await home.RefreshAsync(CancellationToken.None);
        home.Metrics.StandbyW.Should().Be(140);
        var bill = home.Metrics.Bill!;
        bill.Electricity!.SoFar.Should().BeApproximately(1.44 + 1.87, 1e-9);
        bill.Electricity.LowConfidence.Should().BeTrue();
        bill.ElectricityRon.Should().Be((decimal)bill.Electricity.Projected * 1.30m);
        bill.Gas.Should().BeNull("no boiler");
        bill.TotalRon.Should().Be(bill.ElectricityRon, "no boiler: the total is the electricity");
        bill.GasExpected.Should().BeFalse();
    }

    [Fact]
    public async Task SameTime_WithAGapInYesterdaysStream_IsPartial_NoDelta()
    {
        await SeedAsync();
        await using (var ctx = Create())
        {
            // Yesterday 08:00–10:00 local: no live minute (restart, Wi-Fi).
            var from = YesterdayStart.AddHours(8).ToUnixTimeSeconds();
            var to = YesterdayStart.AddHours(10).ToUnixTimeSeconds();
            ctx.LiveMinuteAggregates.RemoveRange(ctx.LiveMinuteAggregates.Where(m => m.MinuteTs >= from && m.MinuteTs < to));
            await ctx.SaveChangesAsync();
        }
        var day = await Service().GetAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2)), CancellationToken.None);
        day.SameTime!.Partial.Should().BeTrue("2 h of 15,6 h missing: under 90 % coverage");
        day.SameTime.DeltaPct.Should().BeNull("a gap must not show as a false increase");
        day.SameTime.TodayKwh.Should().BeApproximately(1.87, 1e-9, "today from the same source: Σ of today's live minutes");
    }

    [Fact]
    public async Task Ttl_CurrentDayOneMinute_PeriodInProgressTenMinutes_PastUntilTheDayChanges()
    {
        await SeedAsync();
        var home = Service();
        var today = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2));
        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 2));
        var lastMonth = month.Previous();
        home.Ttl(today, Now).Should().Be(TimeSpan.FromMinutes(1));
        home.Ttl(month, Now).Should().Be(TimeSpan.FromMinutes(10));
        home.Ttl(lastMonth, Now).Should().Be(TimeSpan.MaxValue);

        var m = await home.GetAsync(month, CancellationToken.None);
        var p = await home.GetAsync(lastMonth, CancellationToken.None);
        _now = Now.AddMinutes(5);
        (await home.GetAsync(month, CancellationToken.None)).Should().BeSameAs(m, "a month in progress is kept 10 minutes");
        await home.RefreshingAsync(month);
        (await home.GetAsync(month, CancellationToken.None)).Should().BeSameAs(m, "no refresh was started within the 10 minutes");
        _now = Now.AddMinutes(11);
        await home.GetAsync(month, CancellationToken.None);
        await home.RefreshingAsync(month);
        (await home.GetAsync(month, CancellationToken.None)).Should().NotBeSameAs(m);
        _now = Now.AddMinutes(90);
        (await home.GetAsync(lastMonth, CancellationToken.None)).Should().BeSameAs(p, "a past period does not change (kept beyond an hour)");
        _now = Now.AddDays(1);
        (await home.GetAsync(lastMonth, CancellationToken.None)).Should().NotBeSameAs(p, "recomputed once the day changes");
    }

    [Fact]
    public async Task PeriodInProgress_ComparedWithTheSameShareOfThePreviousOne()
    {
        await SeedAsync();
        // Week 26.10–01.11 (previous, has the whole of 01.11 = 1,44 kWh) vs the week from 02.11 (Monday, 15:35 now).
        var week = await Service().GetAsync(ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 2)), CancellationToken.None);
        week.InProgress.Should().BeTrue();
        week.House!.PreviousHouseKwh!.Should().HaveCount(7);
        // Monday at 15:35 = bucket 0 at 65 % of the day: the previous Monday (26.10) has no data → null, not the whole week.
        week.PreviousSameFractionKwh.Should().BeNull();
        week.House.PreviousHouseTotalKwh.Should().BeApproximately(1.44, 1e-9);

        // Positive path: Sunday 08.11 at 12:00 local, day 7 of the week half elapsed; the week before (26.10–01.11) has only
        // its Sunday (1,44 kWh) → the same point = half of it.
        _now = new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
        var sunday = await Service().GetAsync(ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 8)), CancellationToken.None);
        sunday.InProgress.Should().BeTrue();
        sunday.PreviousSameFractionKwh.Should().BeApproximately(0.72, 1e-9);

        var day = SameFraction.Previous(new double?[] { 1.0, 2.0, 4.0 }, (2, 0.25));
        day.Should().Be(4.0, "two whole buckets + a quarter of the third");
    }

    [Fact]
    public async Task Gas_OfTheCurrentDay_FollowsTheBoilersDay()
    {
        await SeedAsync();
        // 00:30 Romanian time on 03.11 = 23:30 CET on 02.11: the boiler's day is still 02.11.
        _now = new DateTimeOffset(2026, 11, 2, 22, 30, 0, TimeSpan.Zero);
        var heating = new HeatingState();
        heating.Publish(new HeatingLive(new HeatingDevice(Guid.NewGuid(), "viessmann", "1", "g", "0", "m", "Centrala", true, null, null), null, null, null, null, true));
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var day = await Service(boiler: berlin, heating: heating).GetAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 3)), CancellationToken.None);
        day.Gas!.Period.Should().Be(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2)));

        // A boiler without gas data this month: the bill's total is not the electricity alone („—”, never gas = 0).
        var bill = await Service(boiler: berlin, heating: heating).GetBillAsync(CancellationToken.None);
        bill!.GasExpected.Should().BeTrue();
        bill.ElectricityRon.Should().NotBeNull();
        bill.TotalRon.Should().BeNull();

        heating.IsStale(_now).Should().BeTrue("the boiler has never been read (no snapshot)");
    }

    [Fact]
    public async Task Standby_APlugWithoutLiveStream_IsSubtractedFromItsPolls_AndTheMetricIsZeroUnder5W()
    {
        await SeedAsync();
        Guid router;
        await using (var ctx = Create())
        {
            router = AddPlug(ctx, "Router", PlugRole.Plug, 4);
            ctx.Plugs.Local.Single(p => p.Id == router).StreamEnabled = false;
            // Polled at 10 s during both nights: 30 W.
            foreach (var night in new[] { YesterdayStart, TodayStart })
                for (var t = night.AddHours(1); t < night.AddHours(5); t = t.AddMinutes(1))
                    ctx.Samples.Add(new SampleEntity { PlugId = router, TimestampUtc = t, Online = true, PowerMw = 30_000 });
            await ctx.SaveChangesAsync();
        }
        var standby = await Service().GetStandbyAsync(CancellationToken.None);
        standby!.Watts.Should().Be(110, "200 − 50 − 10 − the router's 30 W: never in the plug and in the standby at once");

        HomeService.StandbyMetric(new StandbyFloor(4, 1, 10)).Should().Be(0, "not measurable: the series exists with 0");
        HomeService.StandbyMetric(new StandbyFloor(42, 1, 10)).Should().Be(42);
        HomeService.StandbyMetric(null).Should().BeNull();
    }
}
