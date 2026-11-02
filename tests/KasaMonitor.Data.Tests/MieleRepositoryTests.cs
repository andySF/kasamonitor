using FluentAssertions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class MieleRepositoryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AddMiele_OnTopOfTheDeployedSchema_OnlyAddsTheMieleTables()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options);
        ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate("20261101185832_AddHeatingReadingCounters");
        var before = ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' ORDER BY name").ToList();

        ctx.Database.Migrate();

        var after = ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table' ORDER BY name").ToList();
        after.Except(before).Should().BeEquivalentTo("MieleDevices", "MieleStateSnapshots", "MieleCycles");
        before.Except(after).Should().BeEmpty();
    }

    [Fact]
    public async Task Device_Upsert_KeepsAliasPlugAndArchive_UpdatesIdent()
    {
        using var db = new TestDbFactory();
        var repo = new MieleRepository(db.CreateContext());
        var d = await repo.UpsertDeviceAsync("000148508574", new MieleIdent(1, "Washing machine", null, "WWV980"), T0, CancellationToken.None);
        d.Alias.Should().Be("Mașină de spălat");
        d.TypeText.Should().Be("Mașină de spălat");
        d.TechType.Should().Be("WWV980");

        var plug = Guid.NewGuid();
        await repo.SetAliasAsync(d.Id, "Mașina din baie", CancellationToken.None);
        await repo.SetPlugAsync(d.Id, plug, CancellationToken.None);
        var again = await new MieleRepository(db.CreateContext()).UpsertDeviceAsync(d.Id, new MieleIdent(1, "Washing machine", "Washer", "WWV980"), T0.AddHours(1), CancellationToken.None);
        again.Alias.Should().Be("Mașina din baie");
        again.PlugId.Should().Be(plug);
        again.DeviceName.Should().Be("Washer");
        again.LastSeenUtc.Should().Be(T0.AddHours(1));

        var named = await repo.UpsertDeviceAsync("000111222333", new MieleIdent(2, "Tumble dryer", "Uscătorul", "TWV780"), T0, CancellationToken.None);
        named.Alias.Should().Be("Uscătorul", "the friendly name of the Miele app is the first alias");
        await repo.SetArchivedAsync(named.Id, true, CancellationToken.None);
        (await repo.ListDevicesAsync(false, CancellationToken.None)).Select(x => x.Id).Should().Equal("000148508574");
        (await repo.ListDevicesAsync(true, CancellationToken.None)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Cycle_RoundTrip_PhasesJson_OpenThenClosed_Counted()
    {
        using var db = new TestDbFactory();
        var repo = new MieleRepository(db.CreateContext());
        var phases = new[] { new MielePhaseSpan(260, "Spălare principală", T0, T0.AddMinutes(30)), new MielePhaseSpan(266, "Centrifugare", T0.AddMinutes(30), null) };
        var c = await repo.AddCycleAsync(new MieleCycle(0, "dev", T0, null, T0.AddMinutes(31), 1, "Cottons", phases, 0.4, 20, null, null), CancellationToken.None);
        c.Id.Should().BePositive();
        (await repo.ListOpenCyclesAsync(CancellationToken.None)).Should().ContainSingle().Which.Phases.Should().BeEquivalentTo(phases);
        (await repo.CountClosedCyclesAsync("dev", CancellationToken.None)).Should().Be(0);

        await repo.UpdateCycleAsync(c with { EndUtc = T0.AddHours(1), EndReason = MieleEndReasons.End, PlugEnergyWh = 812.5 }, CancellationToken.None);
        (await repo.ListOpenCyclesAsync(CancellationToken.None)).Should().BeEmpty();
        var stored = (await repo.ListCyclesAsync("dev", 5, CancellationToken.None)).Should().ContainSingle().Subject;
        stored.Duration.Should().Be(TimeSpan.FromHours(1));
        stored.PlugEnergyWh.Should().Be(812.5);
        stored.Phases[0].PhaseText.Should().Be("Spălare principală");
        (await repo.CountClosedCyclesAsync("dev", CancellationToken.None)).Should().Be(1);
        MieleRepository.PhasesToJson(phases).Should().Contain("\"phaseText\":\"Spălare principală\"").And.Contain("\"toTs\":null");
        MieleRepository.PhasesFromJson("not json").Should().BeEmpty();
    }

    [Fact]
    public void AddMieleCycleSource_On085_ExistingCyclesBecomeObserved_ApiStartUniquePerDevice()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        using var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options);
        ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate("20261102113425_AddMiele");
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, EndTs, LastRunningTs, PhasesJson) VALUES ('dev', 100, 200, 200, '[]')");

        ctx.Database.Migrate();

        ctx.Database.SqlQueryRaw<string>("SELECT Source AS Value FROM MieleCycles").ToList().Should().Equal("observed");
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM MieleCycles WHERE ApiStartTs IS NULL AND ApiEndTs IS NULL AND ApiEnergyKwh IS NULL AND ApiWaterL IS NULL AND EnergyProfileJson IS NULL").ToList().Should().Equal(1);
        // NULL keys never collide (observed cycles), a second row with the same (DeviceId, ApiStartTs) does.
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source) VALUES ('dev', 300, 300, '[]', 'observed')");
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source, ApiStartTs) VALUES ('dev', 400, 400, '[]', 'api', 400)");
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source, ApiStartTs) VALUES ('other', 400, 400, '[]', 'api', 400)");
        var duplicate = () => ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source, ApiStartTs) VALUES ('dev', 401, 401, '[]', 'api', 400)");
        duplicate.Should().Throw<SqliteException>().Where(e => e.Message.Contains("UNIQUE"));
    }

    [Fact]
    public async Task Cycle_SourceApiTimesAndProfile_RoundTrip_BetweenAndPaged()
    {
        using var db = new TestDbFactory();
        var repo = new MieleRepository(db.CreateContext());
        var profile = new[] { new MieleEnergySlot(T0, 0.13), new MieleEnergySlot(T0.AddMinutes(15), 0.045) };
        var api = await repo.AddCycleAsync(new MieleCycle(0, "dev", T0, T0.AddHours(2), T0.AddHours(2), 13, "QuickPowerWash", Array.Empty<MielePhaseSpan>(), null, null, 310, null)
        {
            Source = MieleCycleSources.Api,
            ApiEnergyKwh = 0.29,
            ApiWaterL = 46,
            ApiStartUtc = T0,
            ApiEndUtc = T0.AddHours(2),
            EnergyProfile = profile,
        }, CancellationToken.None);
        var stored = (await repo.ListCyclesAsync("dev", 5, CancellationToken.None)).Should().ContainSingle().Subject;
        stored.Source.Should().Be(MieleCycleSources.Api);
        stored.ApiStartUtc.Should().Be(T0);
        stored.ApiEndUtc.Should().Be(T0.AddHours(2));
        stored.EnergyProfile.Should().BeEquivalentTo(profile);
        (stored.ApiEnergyKwh, stored.ApiWaterL, stored.EnergyKwh, stored.WaterL, stored.PlugEnergyWh).Should().Be((0.29, 46, null, null, 310), "three separate pairs");
        MieleRepository.ProfileToJson(profile).Should().StartWith("[{\"ts\":").And.Contain("\"kwh\":0.13");
        MieleRepository.ProfileFromJson("broken").Should().BeNull();

        // A plain observed cycle keeps the defaults.
        var observed = await repo.AddCycleAsync(new MieleCycle(0, "dev", T0.AddDays(1), null, T0.AddDays(1), 1, "Cottons", Array.Empty<MielePhaseSpan>(), null, null, null, null), CancellationToken.None);
        var o = (await repo.ListCyclesBetweenAsync("dev", T0.AddHours(12), T0.AddDays(2), CancellationToken.None)).Should().ContainSingle().Subject;
        o.Id.Should().Be(observed.Id);
        o.Source.Should().Be(MieleCycleSources.Observed);
        o.ApiStartUtc.Should().BeNull();
        o.EnergyProfile.Should().BeNull();
        await repo.UpdateCycleAsync(o with { Source = MieleCycleSources.Merged, ApiStartUtc = T0.AddDays(1).AddMinutes(3) }, CancellationToken.None);
        (await repo.ListCyclesBetweenAsync("dev", T0.AddHours(12), T0.AddDays(2), CancellationToken.None))[0].Source.Should().Be(MieleCycleSources.Merged);

        for (var i = 0; i < 60; i++)
            await repo.AddCycleAsync(new MieleCycle(0, "other", T0.AddDays(-i - 1), T0.AddDays(-i - 1).AddHours(1), T0, null, null, Array.Empty<MielePhaseSpan>(), null, null, null, null), CancellationToken.None);
        var page1 = await repo.ListCyclesPageAsync(new[] { "dev", "other" }, 0, 50, CancellationToken.None);
        page1.Total.Should().Be(62);
        page1.Items.Should().HaveCount(50);
        page1.Items[0].Id.Should().Be(observed.Id, "newest first");
        var page2 = await repo.ListCyclesPageAsync(new[] { "dev", "other" }, 50, 50, CancellationToken.None);
        page2.Items.Should().HaveCount(12);
        (await repo.ListCyclesPageAsync(new[] { "dev" }, 0, 50, CancellationToken.None)).Items.Select(c => c.Id).Should().Equal(observed.Id, api.Id);
        (await repo.ListCyclesPageAsync(Array.Empty<string>(), 0, 50, CancellationToken.None)).Total.Should().Be(0);
    }

    [Fact]
    public async Task Snapshots_StoredAndListed_LatestFirstForTheDevice()
    {
        using var db = new TestDbFactory();
        var repo = new MieleRepository(db.CreateContext());
        var v = new MieleStateValues(5, "În funcțiune", 0, "Normal", 1, "Cottons", 260, "Spălare principală", 3600, 600, null, 40, 1400, null,
            false, false, false, 0.3, 12, 0.4, 0.3, true, true, false);
        await repo.AddSnapshotAsync(new MieleStateSnapshot(T0, "dev", v), CancellationToken.None);
        await repo.AddSnapshotAsync(new MieleStateSnapshot(T0.AddMinutes(1), "dev", v with { RemainingSec = 3540 }), CancellationToken.None);
        (await repo.LatestSnapshotAsync("dev", CancellationToken.None))!.Values.RemainingSec.Should().Be(3540);
        var all = await repo.ListSnapshotsAsync("dev", T0, T0.AddHours(1), CancellationToken.None);
        all.Should().HaveCount(2);
        all[0].Values.Should().Be(v);
        (await repo.LatestSnapshotAsync("other", CancellationToken.None)).Should().BeNull();
    }
}
