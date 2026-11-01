using System.Data.Common;
using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using KasaMonitor.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class EnergyReportServiceTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    // 01.11.2026 20:00 local (EET, UTC+2).
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    /// <summary>In-memory SQLite with a command counter (no query per plug/day/month).</summary>
    private sealed class Db : IDisposable
    {
        private readonly SqliteConnection _conn = new("Filename=:memory:");
        public readonly CommandCounter Counter = new();

        public Db()
        {
            _conn.Open();
            using var ctx = Create();
            ctx.Database.EnsureCreated();
        }

        public KasaDbContext Create() =>
            new(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(_conn).AddInterceptors(Counter).Options);

        public EnergyReportService Service() => new(new TestScopeFactory(Create), Ro, () => Now);

        public void Dispose() => _conn.Dispose();
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

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

    private static void Day(KasaDbContext ctx, Guid plug, string day, long wh) =>
        ctx.DailyEnergies.Add(new DailyEnergyEntity { PlugId = plug, Date = day, EnergyWh = wh });

    private static void Minute(KasaDbContext ctx, Guid plug, DateTimeOffset utc, double wh) =>
        ctx.LiveMinuteAggregates.Add(new LiveMinuteAggregateEntity { PlugId = plug, MinuteTs = utc.ToUnixTimeSeconds(), EnergyWh = wh, SampleCount = 60, CoveredSeconds = 60 });

    [Fact]
    public async Task House_WithoutAMainMeter_IsNull()
    {
        using var db = new Db();
        using (var ctx = db.Create()) { AddPlug(ctx, "Frigider", PlugRole.Plug, 1); await ctx.SaveChangesAsync(); }
        (await db.Service().GetHouseReportAsync(db.Service().Current(ReportGranularity.Month), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task House_Week_TodayRaisedByTheLiveStream_SubMetersApart()
    {
        using var db = new Db();
        Guid main, fridge, clamp;
        using (var ctx = db.Create())
        {
            main = AddPlug(ctx, "Contor", PlugRole.MainMeter, 1);
            fridge = AddPlug(ctx, "Frigider", PlugRole.Plug, 2);
            clamp = AddPlug(ctx, "Clemă", PlugRole.SubMeter, 3);
            Day(ctx, main, "2026-10-31", 9_000); Day(ctx, fridge, "2026-10-31", 1_000); Day(ctx, clamp, "2026-10-31", 500);
            Day(ctx, main, "2026-11-01", 1_000);                       // device row lags
            Day(ctx, main, "2026-10-20", 4_000);                       // previous week
            // Today's stream: 120 minutes × 20 Wh = 2.4 kWh for the main meter, 60 × 5 Wh for the fridge.
            var t0 = new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 120; i++) Minute(ctx, main, t0.AddMinutes(i), 20);
            for (var i = 0; i < 60; i++) Minute(ctx, fridge, t0.AddMinutes(i), 5);
            await ctx.SaveChangesAsync();
        }

        var svc = db.Service();
        var r = (await svc.GetHouseReportAsync(svc.Current(ReportGranularity.Week), CancellationToken.None))!;

        r.Period.Start.Should().Be(new DateOnly(2026, 10, 26));
        r.MainAlias.Should().Be("Contor");
        r.HouseKwh[5].Should().BeApproximately(9, 1e-9);
        r.PlugsKwh[5].Should().BeApproximately(1, 1e-9);
        r.HouseKwh[6].Should().BeApproximately(2.4, 1e-9);
        r.PlugsKwh[6].Should().BeApproximately(0.3, 1e-9);
        r.RestKwh[6].Should().BeApproximately(2.1, 1e-9);
        r.HouseTotalKwh.Should().BeApproximately(11.4, 1e-9);
        r.PreviousHouseTotalKwh.Should().BeApproximately(4, 1e-9);
        r.SubMeters.Should().ContainSingle().Which.Kwh.Should().BeApproximately(0.5, 1e-9);
        r.FirstHouseDay.Should().Be(new DateOnly(2026, 10, 20));
    }

    [Fact]
    public async Task House_Day_HoursFromMinuteAggregates()
    {
        using var db = new Db();
        Guid main;
        using (var ctx = db.Create())
        {
            main = AddPlug(ctx, "Contor", PlugRole.MainMeter, 1);
            // 19:00–19:59 local = 17:00Z: 60 × 10 Wh.
            for (var i = 0; i < 60; i++) Minute(ctx, main, new DateTimeOffset(2026, 11, 1, 17, i, 0, TimeSpan.Zero), 10);
            Minute(ctx, main, new DateTimeOffset(2026, 10, 31, 21, 59, 0, TimeSpan.Zero), 7); // 23:59 the day before
            await ctx.SaveChangesAsync();
        }

        var svc = db.Service();
        var r = (await svc.GetHouseReportAsync(svc.Current(ReportGranularity.Day), CancellationToken.None))!;

        r.Buckets.Should().HaveCount(24);
        r.HouseKwh[19].Should().BeApproximately(0.6, 1e-9);
        r.HouseKwh[23].Should().BeNull();   // 23:59 of 31.10 is the day before, not today's 23:00
        r.HouseKwh[18].Should().BeNull();
        r.PlugsKwh[19].Should().Be(0);
        r.HouseTotalKwh.Should().BeApproximately(0.6, 1e-9);
        r.PreviousHouseTotalKwh.Should().BeApproximately(0.007, 1e-9);
        r.DeltaPct.Should().NotBeNull();
    }

    [Fact]
    public async Task House_Year_UsesGroupedMonths_AndAFixedNumberOfQueries()
    {
        async Task<int> Reads(int plugCount)
        {
            using var db = new Db();
            using (var ctx = db.Create())
            {
                var main = AddPlug(ctx, "Contor", PlugRole.MainMeter, 1);
                for (var p = 0; p < plugCount; p++)
                {
                    var id = AddPlug(ctx, "P" + p, PlugRole.Plug, 10 + p);
                    for (var d = 1; d <= 28; d++) Day(ctx, id, $"2026-10-{d:D2}", 100);
                    ctx.MonthlyEnergies.Add(new MonthlyEnergyEntity { PlugId = id, Year = 2026, Month = 9, EnergyWh = 5_000 });
                }
                for (var d = 1; d <= 31; d++) Day(ctx, main, $"2026-10-{d:D2}", 10_000);
                Day(ctx, main, "2025-12-31", 8_000);
                await ctx.SaveChangesAsync();
            }
            var svc = db.Service();
            db.Counter.Reads = 0;
            var r = (await svc.GetHouseReportAsync(svc.Current(ReportGranularity.Year), CancellationToken.None))!;
            r.HouseKwh[9].Should().BeApproximately(310, 1e-9);
            r.PlugsKwh[9].Should().BeApproximately(2.8 * plugCount, 1e-9);
            r.PreviousHouseTotalKwh.Should().BeApproximately(8, 1e-9);
            r.HouseKwh[8].Should().BeNull(); // September: only plugs have data, the main meter none
            return db.Counter.Reads;
        }

        var few = await Reads(1);
        var many = await Reads(6);
        many.Should().Be(few);
        few.Should().BeLessThanOrEqualTo(7);
    }

    [Fact]
    public async Task House_LiveHours_AreReadOnlyForTheReportedChannels()
    {
        using var db = new Db();
        Guid main, archived;
        using (var ctx = db.Create())
        {
            main = AddPlug(ctx, "Contor", PlugRole.MainMeter, 1);
            archived = AddPlug(ctx, "Veche", PlugRole.Plug, 2);
            ctx.Plugs.Find(archived)!.IsArchived = true;
            var t0 = new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero);
            for (var i = 0; i < 10; i++) { Minute(ctx, main, t0.AddMinutes(i), 10); Minute(ctx, archived, t0.AddMinutes(i), 50); }
            await ctx.SaveChangesAsync();
        }
        using (var ctx = db.Create())
        {
            var hours = await new LiveRepository(ctx).SumEnergyByHourAsync(new[] { main },
                new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
            hours.Should().ContainSingle().Which.Should().Be(new PlugHourWh(main, new DateTimeOffset(2026, 11, 1, 14, 0, 0, TimeSpan.Zero), 100, 10));
            // The query filters on PlugId IN (...) + MinuteTs: SQLite answers it from the primary key (PlugId, MinuteTs).
            var conn = ctx.Database.GetDbConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN SELECT PlugId FROM LiveMinuteAggregates WHERE PlugId IN ($p) AND MinuteTs >= 0 AND MinuteTs < 10";
            var prm = cmd.CreateParameter();
            prm.ParameterName = "$p";
            prm.Value = main.ToString().ToUpperInvariant();
            cmd.Parameters.Add(prm);
            var plan = new List<string>();
            using (var reader = await cmd.ExecuteReaderAsync()) while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
            string.Join(" | ", plan).Should().Contain("sqlite_autoindex_LiveMinuteAggregates").And.Contain("MinuteTs");
        }
        var svc = db.Service();
        var r = (await svc.GetHouseReportAsync(svc.Current(ReportGranularity.Day), CancellationToken.None))!;
        r.HouseKwh[16].Should().BeApproximately(0.1, 1e-9);
        r.PlugsKwh[16].Should().Be(0);   // the archived plug's minutes are not read
    }

    [Fact]
    public async Task HouseRest_WithoutLiveStream_UsesThe10sSamplesOfTheSameMinute_OncePerMinute()
    {
        using var db = new Db();
        var main = Guid.NewGuid();
        var fridge = Guid.NewGuid();
        var minute = new DateTimeOffset(2026, 11, 1, 17, 59, 0, TimeSpan.Zero); // last whole minute before Now (18:00:00)
        using (var ctx = db.Create())
        {
            foreach (var (id, mw) in new[] { (main, 900_000L), (fridge, 300_000L) })
                for (var i = 0; i < 6; i++)
                    ctx.Samples.Add(new SampleEntity { PlugId = id, TimestampUtc = minute.AddSeconds(i * 10), Online = true, PowerMw = mw + i * 1000 });
            // A sample of the minute before: not counted.
            ctx.Samples.Add(new SampleEntity { PlugId = main, TimestampUtc = minute.AddSeconds(-5), Online = true, PowerMw = 5_000_000 });
            await ctx.SaveChangesAsync();
        }
        var states = new PlugStateBroadcaster();
        states.Publish(new PlugLiveState(main, "AA", "Contor", true, new EmeterReading(2_000_000, null, null, null), Now, Role: PlugRole.MainMeter));
        states.Publish(new PlugLiveState(fridge, "BB", "Frigider", true, new EmeterReading(0, null, null, null), Now, Role: PlugRole.Plug));
        var svc = new HouseRestService(new TestScopeFactory(db.Create), new LiveStreamBroadcaster(), states, () => Now.AddSeconds(5));

        db.Counter.Reads = 0;
        var b = (await svc.GetAsync(CancellationToken.None))!;
        var again = await svc.GetAsync(CancellationToken.None);

        b.FromPolls.Should().BeTrue();
        b.IntervalStartUtc.Should().Be(minute);
        b.MainW.Should().BeApproximately(902.5, 1e-9);
        b.PlugsW.Should().BeApproximately(302.5, 1e-9);
        b.RestW.Should().BeApproximately(600, 1e-9);
        again.Should().BeSameAs(b);
        db.Counter.Reads.Should().Be(1);
    }

    private static Guid AddHeating(KasaDbContext ctx)
    {
        var id = Guid.NewGuid();
        ctx.HeatingDevices.Add(new HeatingDeviceEntity { Id = id, Provider = "viessmann", InstallationId = "1", GatewaySerial = "g", DeviceId = "0", Name = "Vitodens" });
        return id;
    }

    private static void HeatDay(KasaDbContext ctx, Guid dev, string day, double heatingMax, double heatingLast, double dhw, double power) =>
        ctx.HeatingEnergyDaily.Add(new HeatingEnergyDailyEntity
        {
            DeviceId = dev, Day = day, GasHeatingMaxM3 = heatingMax, GasHeatingLastM3 = heatingLast, GasDhwMaxM3 = dhw, GasDhwLastM3 = dhw,
            PowerHeatingKwh = power, Source = "summary",
        });

    [Fact]
    public async Task Heating_Month_FromStoredDays_WithTheBoilerMonthAsCheck()
    {
        using var db = new Db();
        Guid dev;
        using (var ctx = db.Create())
        {
            dev = AddHeating(ctx);
            HeatDay(ctx, dev, "2026-10-31", 3.0, 3.0, 0.5, 0.2);
            HeatDay(ctx, dev, "2026-11-01", 1.4, 0.9, 0.3, 0.1); // today: last value 0.9 counts
            ctx.HeatingEnergyPeriods.Add(new HeatingEnergyPeriodEntity { DeviceId = dev, Kind = "gas", Purpose = "heating", Period = "month", Value = 1.2, Unit = "m3", UpdatedTs = Now.ToUnixTimeSeconds() });
            ctx.HeatingEnergyPeriods.Add(new HeatingEnergyPeriodEntity { DeviceId = dev, Kind = "gas", Purpose = "dhw", Period = "month", Value = 0.3, Unit = "m3", UpdatedTs = Now.ToUnixTimeSeconds() });
            ctx.HeatingEnergyPeriods.Add(new HeatingEnergyPeriodEntity { DeviceId = dev, Kind = "gas", Purpose = "heating", Period = "lastmonth", Value = 70, Unit = "m3", UpdatedTs = Now.ToUnixTimeSeconds() });
            await ctx.SaveChangesAsync();
        }

        var svc = db.Service();
        var r = await svc.GetHeatingReportAsync(dev, svc.Current(ReportGranularity.Month), CancellationToken.None);

        r.Buckets.Should().HaveCount(30);
        r.Gas.Series.Single(s => s.Key == "heating").Values[0].Should().Be(0.9);
        r.Gas.OwnTotal.Should().BeApproximately(1.2, 1e-9);
        r.Gas.ApiTotal.Should().BeApproximately(1.5, 1e-9);
        r.Gas.ApiDiffers.Should().BeTrue();
        r.Gas.PreviousTotal.Should().BeApproximately(70, 1e-9);
        r.Power.Total.Should().BeApproximately(0.1, 1e-9);
        r.FirstDay.Should().Be(new DateOnly(2026, 10, 31));
    }

    [Fact]
    public async Task Heating_Year_MonthsGroupedInTheDatabase()
    {
        using var db = new Db();
        Guid dev;
        using (var ctx = db.Create())
        {
            dev = AddHeating(ctx);
            HeatDay(ctx, dev, "2026-10-30", 2.0, 1.0, 0.5, 0.2);
            HeatDay(ctx, dev, "2026-10-31", 3.0, 3.0, 0.5, 0.2);
            HeatDay(ctx, dev, "2026-11-01", 1.4, 0.9, 0.3, 0.1);
            await ctx.SaveChangesAsync();
        }

        var svc = db.Service();
        var r = await svc.GetHeatingReportAsync(dev, svc.Current(ReportGranularity.Year), CancellationToken.None);

        r.Gas.Series[0].Values[9].Should().BeApproximately(5.0, 1e-9);   // closed days: highest
        r.Gas.Series[1].Values[9].Should().BeApproximately(1.0, 1e-9);
        r.Gas.Series[0].Values[10].Should().BeApproximately(0.9, 1e-9);  // today: last
        r.Heat.Series[0].Values[9].Should().BeNull();                     // no heat stored → no bar
        r.Power.Series[0].Values[9].Should().BeApproximately(0.4, 1e-9);
        r.Gas.Remainder.Should().BeNull();
        r.Gas.Total.Should().BeApproximately(7.2, 1e-9);
    }

    [Fact]
    public async Task Heating_Day_HoursFromTheStoredCounters()
    {
        using var db = new Db();
        Guid dev;
        using (var ctx = db.Create())
        {
            dev = AddHeating(ctx);
            var midnight = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);
            ctx.HeatingReadings.Add(new HeatingReadingEntity { DeviceId = dev, Ts = midnight.AddMinutes(-2).ToUnixTimeSeconds(), GasHeatingTodayM3 = 2.0, GasDhwTodayM3 = 0.5 });
            ctx.HeatingReadings.Add(new HeatingReadingEntity { DeviceId = dev, Ts = midnight.AddMinutes(30).ToUnixTimeSeconds(), GasHeatingTodayM3 = 0.1, GasDhwTodayM3 = 0.0 });
            ctx.HeatingReadings.Add(new HeatingReadingEntity { DeviceId = dev, Ts = midnight.AddHours(6).AddMinutes(10).ToUnixTimeSeconds(), GasHeatingTodayM3 = 0.4, GasDhwTodayM3 = 0.2, PowerTodayKwh = 0.05 });
            await ctx.SaveChangesAsync();
        }

        var svc = db.Service();
        var r = await svc.GetHeatingReportAsync(dev, svc.Current(ReportGranularity.Day), CancellationToken.None);

        r.Gas.Series[0].Values[0].Should().BeApproximately(0.1, 1e-9);
        r.Gas.Series[0].Values[6].Should().BeApproximately(0.3, 1e-9);
        r.Gas.Series[1].Values[6].Should().BeApproximately(0.2, 1e-9);
        r.Gas.Series[0].Values[3].Should().BeNull();
        r.Power.Series[0].Values[6].Should().BeNull(); // first electricity value has nothing to pair with
        r.Gas.OwnTotal.Should().BeApproximately(0.6, 1e-9);
    }

    [Fact]
    public async Task Heating_WithoutAnyData_RendersEmpty()
    {
        using var db = new Db();
        var svc = db.Service();
        foreach (var kind in Enum.GetValues<ReportGranularity>())
        {
            var r = await svc.GetHeatingReportAsync(Guid.NewGuid(), svc.Current(kind), CancellationToken.None);
            r.Gas.Total.Should().BeNull();
            r.Gas.DeltaPct.Should().BeNull();
            r.FirstDay.Should().BeNull();
        }
    }

    [Fact]
    public async Task Repositories_RoundTripTheTodayCounters()
    {
        using var db = new Db();
        var dev = Guid.NewGuid();
        var r = new HeatingReading(dev, Now, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            GasHeatingTodayM3: 1.2, GasDhwTodayM3: 0.3, PowerTodayKwh: 0.15, HeatTodayKwh: 12.5);
        using (var ctx = db.Create()) await new HeatingRepository(ctx).SaveReadingAsync(r, CancellationToken.None);
        using (var ctx = db.Create())
        {
            var repo = new HeatingRepository(ctx);
            (await repo.ListReadingsAsync(dev, Now.AddMinutes(-1), Now, CancellationToken.None)).Single().Should().Be(r);
            (await repo.ListCounterReadingsAsync(dev, Now.AddMinutes(-1), Now, CancellationToken.None)).Single()
                .Should().Be(new HeatingCounterReading(Now, 1.2, 0.3, 0.15, 12.5));
        }
    }
}
