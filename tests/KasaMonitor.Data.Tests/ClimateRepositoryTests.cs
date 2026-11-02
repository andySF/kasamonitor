using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.History;
using KasaMonitor.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class ClimateRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public void AddClimate_OnTopOfTheDeployedSchema_OnlyAddsTheClimateTables()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options);
        ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate("20261102121526_AddMieleCycleSource");
        var before = ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate("20261102130647_AddClimate");

        var after = ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' ORDER BY name").ToList();
        after.Except(before).Should().BeEquivalentTo("ClimateSensors", "ClimateReadings");
        before.Except(after).Should().BeEmpty();
    }

    [Fact]
    public void AddClimateDaily_OnTopOf091_OnlyAddsTheDailyTable()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options);
        ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate("20261102150000_AddPlugAppliance");
        var before = ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        ctx.Database.Migrate();

        var after = ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' ORDER BY name").ToList();
        after.Except(before).Should().BeEquivalentTo("ClimateDaily");
        before.Except(after).Should().BeEmpty();
    }

    [Fact]
    public async Task Daily_UpsertListLast_AndTheFirstReadingPerSensor()
    {
        using var db = new TestDbFactory();
        var repo = new ClimateRepository(db.CreateContext());
        var s = await repo.GetOrAddSensorAsync(ClimateSources.Govee, "AA:BB:CC:DD:EE:FF:00:31", "Baie", "Baie", false, Ct);
        await repo.AddReadingAsync(new ClimateReading(s.Id, T0, 21, 50, null), Ct);
        await repo.AddReadingAsync(new ClimateReading(s.Id, T0.AddDays(-3), 20, 50, null), Ct);
        var d1 = new DateOnly(2026, 11, 1);
        var stats = new ClimateStats(19, 20.5, 22, 40, 45, 50, 30);
        await repo.UpsertDailyAsync(new[] { new ClimateDayStats(s.Id, d1, stats), new ClimateDayStats(s.Id, d1.AddDays(1), ClimateStats.Empty) }, Ct);
        await repo.UpsertDailyAsync(new[] { new ClimateDayStats(s.Id, d1, stats with { AvgC = 20.7 }) }, Ct);   // an update, not a second row

        var days = await repo.ListDailyAsync(new[] { s.Id }, d1, d1.AddDays(5), Ct);
        days.Should().HaveCount(2);
        days[0].Stats.Should().Be(stats with { AvgC = 20.7 });
        days[1].Stats.Should().Be(ClimateStats.Empty);
        (await repo.LastDailyDaysAsync(Ct))[s.Id].Should().Be(d1.AddDays(1));
        (await repo.FirstReadingsAsync(Ct))[s.Id].Should().Be(T0.AddDays(-3));
    }

    [Fact]
    public async Task Sensor_CreatedOnce_EditsAreKept_EmptyNameRefused()
    {
        using var db = new TestDbFactory();
        var repo = new ClimateRepository(db.CreateContext());

        var s = await repo.GetOrAddSensorAsync(ClimateSources.Govee, "AA:BB:CC:DD:EE:FF:00:11", "Baie", "", false, Ct);
        s.Name.Should().Be("Baie");
        var edited = await repo.UpdateSensorAsync(s.Id, " Baie mare ", " Baie ", false, Ct);
        edited.Should().BeEquivalentTo(new { Name = "Baie mare", Room = "Baie", IsOutdoor = false });

        var again = await repo.GetOrAddSensorAsync(ClimateSources.Govee, "AA:BB:CC:DD:EE:FF:00:11", "Baie", "", false, Ct);
        again.Id.Should().Be(s.Id);
        again.Name.Should().Be("Baie mare");
        (await repo.ListSensorsAsync(true, Ct)).Should().ContainSingle();

        await repo.Invoking(r => r.UpdateSensorAsync(s.Id, "  ", "x", false, Ct)).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task SameExternalId_OfAnotherSource_IsAnotherSensor_ArchivedHiddenFromTheDefaultList()
    {
        using var db = new TestDbFactory();
        var repo = new ClimateRepository(db.CreateContext());
        var a = await repo.GetOrAddSensorAsync(ClimateSources.Shelly, "x", "A", "", false, Ct);
        var b = await repo.GetOrAddSensorAsync(ClimateSources.Govee, "x", "B", "", false, Ct);
        a.Id.Should().NotBe(b.Id);

        await repo.SetArchivedAsync(b.Id, true, Ct);
        (await repo.ListSensorsAsync(false, Ct)).Select(x => x.Id).Should().Equal(a.Id);
        (await repo.ListSensorsAsync(true, Ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task MarkSeen_KeepsTheBatteryWhenNoneIsGiven()
    {
        using var db = new TestDbFactory();
        var repo = new ClimateRepository(db.CreateContext());
        var s = await repo.GetOrAddSensorAsync(ClimateSources.Shelly, "aabbccddee01:0", "H&T", "", false, Ct);

        await repo.MarkSeenAsync(s.Id, T0, 87, Ct);
        await repo.MarkSeenAsync(s.Id, T0.AddMinutes(5), null, Ct);

        var stored = (await repo.ListSensorsAsync(true, Ct)).Single();
        stored.LastSeenUtc.Should().Be(T0.AddMinutes(5));
        stored.BatteryPercent.Should().Be(87);
    }

    [Fact]
    public async Task Readings_LatestPerSensor_RangeOldestFirst_DuplicateSecondIgnored_Retention()
    {
        using var db = new TestDbFactory();
        var repo = new ClimateRepository(db.CreateContext());
        var a = await repo.GetOrAddSensorAsync(ClimateSources.Govee, "a", "A", "", false, Ct);
        var b = await repo.GetOrAddSensorAsync(ClimateSources.OpenMeteo, "outdoor", "Afară", "Afară", true, Ct);

        await repo.AddReadingAsync(new ClimateReading(a.Id, T0, 21, 50, null), Ct);
        await repo.AddReadingAsync(new ClimateReading(a.Id, T0.AddMinutes(30), 21.5, 51, 90), Ct);
        await repo.AddReadingAsync(new ClimateReading(a.Id, T0.AddMinutes(30), 99, 99, null), Ct);   // same second: ignored
        await repo.AddReadingAsync(new ClimateReading(b.Id, T0.AddDays(-400), 3, 80, null), Ct);
        await repo.AddReadingAsync(new ClimateReading(b.Id, T0.AddMinutes(15), 8, 70, null), Ct);

        var latest = await repo.LatestReadingsAsync(Ct);
        latest[a.Id].Should().Be(new ClimateReading(a.Id, T0.AddMinutes(30), 21.5, 51, 90));
        latest[b.Id].TemperatureC.Should().Be(8);

        var range = await repo.ListReadingsAsync(new[] { a.Id, b.Id }, T0, T0.AddHours(1), Ct);
        range[a.Id].Select(r => r.TemperatureC).Should().Equal(21, 21.5);
        range[b.Id].Select(r => r.TemperatureC).Should().Equal(8);

        (await repo.DeleteReadingsBeforeAsync(T0.AddDays(-365), Ct)).Should().Be(1);
        (await repo.ListReadingsAsync(new[] { b.Id }, T0.AddYears(-2), T0.AddHours(1), Ct))[b.Id].Should().ContainSingle();
    }
}
