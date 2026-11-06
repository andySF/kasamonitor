using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data.Entities;
using KasaMonitor.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class RollupWorkerTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    /// <summary>A plug row (the roll-up goes plug by plug, from the Plugs table).</summary>
    internal static async Task<Guid> AddPlugAsync(TestDbFactory f, string mac = "aabbccddee00", string alias = "Frigider", PlugRole role = PlugRole.Plug)
    {
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var id = await repo.UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.10"),
            new PlugInfo(mac, alias, "KP115(EU)", "1.0", "1.0.12 Build 1", "", true, -50, TimeSpan.Zero)), DateTimeOffset.UtcNow, Ct);
        if (role != PlugRole.Plug) await repo.SetRoleAsync(id, role, Ct);
        return id;
    }

    private static Sample S(Guid plug, DateTimeOffset t, long mw) => new(0, plug, t, true, mw, null, null, null, null, null, null);

    [Fact]
    public async Task RollupSamplesToMinutesAsync_Aggregates_BySample_PerMinute()
    {
        using var f = new TestDbFactory();
        var plugId = await AddPlugAsync(f);
        var dayBase = DateTimeOffset.Parse("2026-05-08T00:00:00Z");

        using (var ctx = f.CreateContext())
        {
            var sampleRepo = new SampleRepository(ctx);
            var samples = new[]
            {
                new Sample(0, plugId, dayBase, true, 1000, null, null, null, null, null, null),
                new Sample(0, plugId, dayBase.AddSeconds(10), true, 2000, null, null, null, null, null, null),
                new Sample(0, plugId, dayBase.AddSeconds(20), true, 3000, null, null, null, null, null, null),
                // Different minute
                new Sample(0, plugId, dayBase.AddMinutes(1), true, 4000, null, null, null, null, null, null),
            };
            await sampleRepo.InsertManyAsync(samples, CancellationToken.None);
        }

        var scopeFactory = new TestScopeFactory(f.CreateContext);
        var worker = new RollupWorker(scopeFactory, NullLogger<RollupWorker>.Instance);
        await worker.RollupSamplesToMinutesAsync(new DateOnly(2026, 5, 8), CancellationToken.None);

        using var ctxRead = f.CreateContext();
        var aggs = await ctxRead.OneMinuteAggregates.Where(a => a.PlugId == plugId).OrderBy(a => a.BucketStartUtc).ToListAsync();
        aggs.Should().HaveCount(2);
        aggs[0].PAvgMw.Should().Be(2000); // (1000+2000+3000)/3
        aggs[0].PMaxMw.Should().Be(3000);
        aggs[0].PMinMw.Should().Be(1000);
        aggs[0].SampleCount.Should().Be(3);
    }

    [Fact]
    public async Task MinutesToHours_TheHourlyRollupMath_PerPlug()
    {
        using var f = new TestDbFactory();
        var plug = await AddPlugAsync(f);
        var t0 = new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero);
        using (var ctx = f.CreateContext())
        {
            var samples = new List<Sample>();
            for (var m = 0; m < 90; m++)
                for (var s = 0; s < 6; s++)
                    samples.Add(S(plug, t0.AddMinutes(m).AddSeconds(10 * s), m < 30 ? 1_000_000 : m == 45 && s == 3 ? 9_000_000 : 3_000_000));
            await new SampleRepository(ctx).InsertManyAsync(samples, Ct);
        }
        var worker = new RollupWorker(new TestScopeFactory(f.CreateContext), NullLogger<RollupWorker>.Instance);
        await worker.RollupSamplesToMinutesAsync(new DateOnly(2026, 5, 8), Ct);
        (await worker.RollupMinutesToHoursAsync(new DateOnly(2026, 5, 8), Ct)).Should().Be(2);

        using var read = f.CreateContext();
        var hours = await read.OneHourAggregates.Where(a => a.PlugId == plug).OrderBy(a => a.BucketStartUtc).ToListAsync();
        hours.Should().HaveCount(2);
        hours[0].BucketStartUtc.Should().Be(t0);
        hours[0].PAvgMw.Should().Be(2_016_667, "(30 minutes at 1 kW + 29 at 3 kW + one of 4 kW, a 9 kW reading among five of 3 kW) / 60");
        hours[0].PMaxMw.Should().Be(9_000_000);
        hours[0].SampleCount.Should().Be(60, "the minutes of the hour");
        hours[0].EnergyWh.Should().Be(2017);
        hours[1].SampleCount.Should().Be(30);
        hours[1].EnergyWh.Should().Be(1500, "30 minutes at 3 kW");
    }

    [Fact]
    public async Task Backfill_EveryDayWithMinutesButNoHours_OnceAndIdempotent()
    {
        using var f = new TestDbFactory();
        var a = await AddPlugAsync(f, "aabbccddee00");
        var b = await AddPlugAsync(f, "aabbccddee01", "Cuptor");
        var day1 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        using (var ctx = f.CreateContext())
        {
            foreach (var (plug, start, days) in new[] { (a, day1, 3), (b, day1.AddDays(1), 1) })
                for (var d = 0; d < days; d++)
                    for (var m = 0; m < 120; m++)
                        ctx.OneMinuteAggregates.Add(new OneMinuteAggregateEntity
                            { PlugId = plug, BucketStartUtc = start.AddDays(d).AddMinutes(m), PAvgMw = 500_000, PMaxMw = 800_000, PMinMw = 0, SampleCount = 6, OnlineCount = 6 });
            await ctx.SaveChangesAsync(Ct);
            // Day 2 of plug a already has its hours (a regular cycle): left alone.
            await new PowerAggregateRepository(ctx).UpsertHoursAsync(a, new[] { new PowerHour(day1.AddDays(1), 1, 1, 60, 0), new PowerHour(day1.AddDays(1).AddHours(1), 1, 1, 60, 0) }, Ct);
        }
        var worker = new RollupWorker(new TestScopeFactory(f.CreateContext), NullLogger<RollupWorker>.Instance);

        var now = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);
        (await worker.BackfillAsync(now, Ct)).Should().Be(2 * 2 + 2, "plug a: days 1 and 3, two hours each; plug b: one day");
        using (var ctx = f.CreateContext())
            (await new SettingsRepository(ctx).GetAsync(SettingKeys.PowerBackfillDoneUtc, Ct)).Should().NotBeNull("the marker of a backfill without errors");
        (await worker.BackfillAsync(now, Ct)).Should().Be(0, "the marker: skipped");

        using var read = f.CreateContext();
        (await read.OneHourAggregates.CountAsync(x => x.PlugId == a)).Should().Be(6);
        (await read.OneHourAggregates.SingleAsync(x => x.PlugId == a && x.BucketStartUtc == day1.AddDays(1))).PAvgMw.Should().Be(1, "the day that had hours is not touched");
        (await read.OneHourAggregates.SingleAsync(x => x.PlugId == b && x.BucketStartUtc == day1.AddDays(1).AddHours(1))).PAvgMw.Should().Be(500_000);
    }

    /// <summary>
    /// Point 6 of 0.14.0: once the retention has deleted the raw readings of a day (all, or the start of it), rolling that day
    /// again — the catch-up after a restart, the full roll of yesterday — keeps its minutes and hours as they were.
    /// </summary>
    [Fact]
    public async Task ADayRolledAgainAfterTheRetention_KeepsItsMinutesAndHours()
    {
        using var f = new TestDbFactory();
        var plug = await AddPlugAsync(f);
        var day = new DateOnly(2026, 6, 1);
        var t0 = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        using (var ctx = f.CreateContext())
        {
            var samples = new List<Sample>();
            for (var m = 0; m < 60; m++)
                for (var s = 0; s < 6; s++)
                    samples.Add(S(plug, t0.AddMinutes(m).AddSeconds(10 * s), 2_000_000));
            await new SampleRepository(ctx).InsertManyAsync(samples, Ct);
        }
        var scopes = new TestScopeFactory(f.CreateContext);
        var worker = new RollupWorker(scopes, NullLogger<RollupWorker>.Instance);
        await worker.RollupSamplesToMinutesAsync(day, Ct);
        await worker.RollupMinutesToHoursAsync(day, Ct);
        var (minutesBefore, hoursBefore) = await SnapshotAsync(f, plug);

        // The retention cuts in the middle of a minute (its cutoff is now − N months, to the tick): 12:10:25.
        var retention = new ReadingsRetentionService(scopes, NullLogger<ReadingsRetentionService>.Instance, () => t0.AddMinutes(10).AddSeconds(25).AddMonths(3));
        (await retention.ApplyAsync(3, Ct)).Should().Be(10 * 6 + 3);
        await worker.RollupSamplesToMinutesAsync(day, Ct);
        await worker.RollupMinutesToHoursAsync(day, Ct);
        (await SnapshotAsync(f, plug)).Should().Be((minutesBefore, hoursBefore), "the minute 12:10 kept its 6 readings, the others are unchanged");

        // Every reading gone: rolling the day writes nothing and deletes nothing.
        (await retention.ApplyAsync(1, Ct)).Should().BeGreaterThan(0);
        (await worker.RollupSamplesToMinutesAsync(day, Ct)).Should().Be(0);
        (await worker.RollupMinutesToHoursAsync(day, Ct)).Should().Be(0);
        await worker.BackfillAsync(t0.AddDays(3), Ct);
        (await SnapshotAsync(f, plug)).Should().Be((minutesBefore, hoursBefore));

        // A whole cycle with the catch-up of the last days does not touch them either.
        var cycle = new RollupWorker(scopes, NullLogger<RollupWorker>.Instance);
        await cycle.RunCycleAsync(t0.AddDays(3), Ct);
        (await SnapshotAsync(f, plug)).Should().Be((minutesBefore, hoursBefore));
    }

    private static async Task<(string Minutes, string Hours)> SnapshotAsync(TestDbFactory f, Guid plug)
    {
        using var ctx = f.CreateContext();
        var minutes = (await ctx.OneMinuteAggregates.AsNoTracking().Where(a => a.PlugId == plug).ToListAsync())
            .OrderBy(a => a.BucketStartUtc).Select(a => $"{a.BucketStartUtc:O}:{a.PAvgMw}/{a.PMaxMw}/{a.SampleCount}").ToList();
        var hours = (await ctx.OneHourAggregates.AsNoTracking().Where(a => a.PlugId == plug).ToListAsync())
            .OrderBy(a => a.BucketStartUtc).Select(a => $"{a.BucketStartUtc:O}:{a.PAvgMw}/{a.PMaxMw}/{a.SampleCount}/{a.EnergyWh}").ToList();
        minutes.Should().HaveCount(60);
        hours.Should().HaveCount(1);
        return (string.Join(";", minutes), string.Join(";", hours));
    }

    [Fact]
    public async Task Cycle_RollsSinceThePreviousOne_TodayIsThereWithinACycle()
    {
        using var f = new TestDbFactory();
        var plug = await AddPlugAsync(f);
        var now = new DateTimeOffset(2026, 11, 12, 10, 7, 0, TimeSpan.Zero);
        async Task AddAsync(DateTimeOffset from, int minutes, long mw)
        {
            using var ctx = f.CreateContext();
            var list = new List<Sample>();
            for (var m = 0; m < minutes; m++)
                for (var s = 0; s < 6; s++)
                    list.Add(S(plug, from.AddMinutes(m).AddSeconds(10 * s), mw));
            await new SampleRepository(ctx).InsertManyAsync(list, Ct);
        }
        await AddAsync(now.AddDays(-3), 10, 1_000_000);   // before the first cycle: the plug has no minutes yet
        await AddAsync(now.AddMinutes(-7), 7, 2_000_000);  // 10:00–10:06 today
        var worker = new RollupWorker(new TestScopeFactory(f.CreateContext), NullLogger<RollupWorker>.Instance);

        await worker.RunCycleAsync(now, Ct);
        await AddAsync(now, 15, 4_000_000);                 // 10:07–10:21
        await worker.RunCycleAsync(now.AddMinutes(15), Ct); // at 10:22: 10:07–10:21 rolled; the 10:00 hour now has 22 minutes

        using var read = f.CreateContext();
        (await read.OneMinuteAggregates.CountAsync(a => a.PlugId == plug)).Should().Be(10 + 7 + 15);
        var hour = await read.OneHourAggregates.SingleAsync(a => a.PlugId == plug && a.BucketStartUtc == new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
        hour.SampleCount.Should().Be(22);
        hour.PAvgMw.Should().Be(3_363_636, "(7 × 2 kW + 15 × 4 kW) / 22, rounded to the mW");
        (await read.OneHourAggregates.CountAsync(a => a.PlugId == plug)).Should().Be(2, "the hour three days ago came from the catch-up");
    }

    private static async Task AddSamplesAsync(TestDbFactory f, Guid plug, DateTimeOffset from, int minutes, long mw)
    {
        using var ctx = f.CreateContext();
        var list = new List<Sample>();
        for (var m = 0; m < minutes; m++)
            for (var s = 0; s < 6; s++)
                list.Add(S(plug, from.AddMinutes(m).AddSeconds(10 * s), mw));
        await new SampleRepository(ctx).InsertManyAsync(list, Ct);
    }

    private static async Task<int> MinutesOnAsync(TestDbFactory f, Guid plug, DateTimeOffset dayStart)
    {
        using var ctx = f.CreateContext();
        return await ctx.OneMinuteAggregates.CountAsync(a => a.PlugId == plug && a.BucketStartUtc >= dayStart && a.BucketStartUtc < dayStart.AddDays(1));
    }

    /// <summary>
    /// Fix round 1, R1: a past day with readings but no minutes (the old daily worker missed it), or with readings beyond its
    /// minutes, gets its minutes from the one-time backfill, and its hours explicitly — even when the day already had an hour.
    /// </summary>
    [Fact]
    public async Task Backfill_PastDaysWithReadingsButNoMinutes_GetTheirMinutesAndHours()
    {
        using var f = new TestDbFactory();
        var plug = await AddPlugAsync(f);
        var missed = new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
        var partial = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
        var recent = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);
        await AddSamplesAsync(f, plug, missed, 30, 1_000_000);
        await AddSamplesAsync(f, plug, partial, 60, 2_000_000);
        await AddSamplesAsync(f, plug, recent, 10, 3_000_000);
        var worker = new RollupWorker(new TestScopeFactory(f.CreateContext), NullLogger<RollupWorker>.Instance);
        // 15 August was rolled up to 10:29 only, its hour too; 9 September completely.
        await worker.RollupSamplesToMinutesAsync(partial, partial.AddMinutes(30), Ct);
        await worker.RollupMinutesToHoursAsync(new DateOnly(2026, 8, 15), Ct);
        await worker.RollupSamplesToMinutesAsync(new DateOnly(2026, 9, 9), Ct);

        var written = await worker.BackfillAsync(new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero), Ct);

        written.Should().Be(30 + 1 + 30 + 1 + 1, "1 August: 30 minutes and their hour; 15 August: the 30 missing minutes and its hour again; 9 September: its hour");
        (await MinutesOnAsync(f, plug, missed.Date)).Should().Be(30);
        (await MinutesOnAsync(f, plug, partial.Date)).Should().Be(60);
        using var read = f.CreateContext();
        (await read.OneHourAggregates.SingleAsync(a => a.PlugId == plug && a.BucketStartUtc == partial)).SampleCount.Should().Be(60, "the hour is rolled again explicitly");
        (await read.OneHourAggregates.SingleAsync(a => a.PlugId == plug && a.BucketStartUtc == missed)).PAvgMw.Should().Be(1_000_000);
    }

    /// <summary>Fix round 1, R2: each plug is caught up from its own last minute, whatever the length of the stop (here 10 days).</summary>
    [Fact]
    public async Task Cycle_CatchesUpEachPlugFromItsLastMinute_AfterAStopLongerThanAWeek()
    {
        using var f = new TestDbFactory();
        var a = await AddPlugAsync(f, "aabbccddee00");
        var b = await AddPlugAsync(f, "aabbccddee01", "Cuptor");
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        await AddSamplesAsync(f, a, now.AddDays(-12), 20, 1_000_000);
        var worker = new RollupWorker(new TestScopeFactory(f.CreateContext), NullLogger<RollupWorker>.Instance);
        await worker.RollupSamplesToMinutesAsync(now.AddDays(-12), now.AddDays(-12).AddHours(1), Ct);
        // The stop: readings of 10 and 9 days ago and of today, no minutes; plug b never had minutes.
        await AddSamplesAsync(f, a, now.AddDays(-10), 15, 1_500_000);
        await AddSamplesAsync(f, a, now.AddDays(-9), 15, 1_500_000);
        await AddSamplesAsync(f, a, now.AddMinutes(-30), 30, 2_000_000);
        await AddSamplesAsync(f, b, now.AddDays(-20), 5, 500_000);

        await new RollupWorker(new TestScopeFactory(f.CreateContext), NullLogger<RollupWorker>.Instance).RunCycleAsync(now, Ct);

        using var read = f.CreateContext();
        (await read.OneMinuteAggregates.CountAsync(x => x.PlugId == a)).Should().Be(20 + 15 + 15 + 30);
        (await read.OneMinuteAggregates.CountAsync(x => x.PlugId == b)).Should().Be(5, "from its first reading, 20 days ago");
        (await read.OneHourAggregates.CountAsync(x => x.PlugId == a)).Should().Be(4, "12 days ago (the overlap reads its last minutes again), 10 and 9 days ago, today");
    }

    /// <summary>Fix round 1, R3: the retention runs once per UTC day, after a roll without errors.</summary>
    [Fact]
    public async Task Retention_OncePerUtcDay_AfterTheRoll()
    {
        using var f = new TestDbFactory();
        var plug = await AddPlugAsync(f);
        var scopes = new TestScopeFactory(f.CreateContext);
        var settings = TestSettings.Create(scopes);
        await settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false, ReadingsRetentionMonths: 12), Ct);
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = now;
        var worker = new RollupWorker(scopes, NullLogger<RollupWorker>.Instance,
            new ReadingsRetentionService(scopes, NullLogger<ReadingsRetentionService>.Instance, () => clock), settings);
        async Task<int> OldAsync()
        {
            using var ctx = f.CreateContext();
            return await ctx.Samples.CountAsync(x => x.TimestampUtc < now.AddYears(-1));
        }

        await AddSamplesAsync(f, plug, now.AddYears(-2), 1, 1_000);
        await worker.RunCycleAsync(now, Ct);
        (await OldAsync()).Should().Be(0);
        await AddSamplesAsync(f, plug, now.AddYears(-2).AddDays(1), 1, 1_000);
        clock = now.AddMinutes(15);
        await worker.RunCycleAsync(clock, Ct);
        (await OldAsync()).Should().Be(6, "the same UTC day: no second retention");
        clock = now.AddDays(1);
        await worker.RunCycleAsync(clock, Ct);
        (await OldAsync()).Should().Be(0, "the next day");
    }

    /// <summary>Fix round 1, R5: rows written for a past period bump the version of the aggregates; the rows of now do not.</summary>
    [Fact]
    public async Task Cycle_BumpsTheVersion_OnlyForRowsOfAPastPeriod()
    {
        using var f = new TestDbFactory();
        var plug = await AddPlugAsync(f);
        var now = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        await AddSamplesAsync(f, plug, now.AddDays(-3), 10, 1_000_000);
        var version = new PowerAggregatesVersion();
        var worker = new RollupWorker(new TestScopeFactory(f.CreateContext), NullLogger<RollupWorker>.Instance, version: version);

        await worker.RunCycleAsync(now, Ct);
        var after = version.Value;
        after.Should().BeGreaterThan(0, "the catch-up wrote minutes of three days ago");
        await AddSamplesAsync(f, plug, now, 14, 2_000_000);
        await worker.RunCycleAsync(now.AddMinutes(15), Ct);
        version.Value.Should().Be(after, "minutes of the last cycle belong to periods still settling");
        (await MinutesOnAsync(f, plug, new DateTimeOffset(now.Date, TimeSpan.Zero))).Should().Be(14);
    }
}
