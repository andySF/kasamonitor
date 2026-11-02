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
