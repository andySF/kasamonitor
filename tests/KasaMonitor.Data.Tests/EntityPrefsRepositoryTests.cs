using FluentAssertions;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace KasaMonitor.Data.Tests;

/// <summary>0.11, F1: the per-entity preferences (table EntityPrefs), keyed by the stable entity key.</summary>
public class EntityPrefsRepositoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task Save_ListByDevicePrefix_AndAnEmptyPreferenceDeletesTheRow()
    {
        using var db = new TestDbFactory();
        await using (var ctx = db.CreateContext())
        {
            var repo = new EntityPrefsRepository(ctx);
            await repo.SaveAsync(new EntityPrefs("plug:a/power", Visible: false, NameOverride: "  Frigider  ", PrecisionOverride: 9, ClassOverride: DeviceClass.Power), Ct);
            await repo.SaveAsync(new EntityPrefs("plug:a/voltage", InHistory: true), Ct);
            await repo.SaveAsync(new EntityPrefs("plug:ab/power", InMetrics: false), Ct);
        }
        await using (var ctx = db.CreateContext())
        {
            var repo = new EntityPrefsRepository(ctx);
            var a = await repo.ListAsync("plug:a/", Ct);
            a.Keys.Should().BeEquivalentTo("plug:a/power", "plug:a/voltage");
            a["plug:a/power"].Should().Be(new EntityPrefs("plug:a/power", false, null, null, "Frigider", 6, DeviceClass.Power, null, false));
            (await repo.ListAllAsync(Ct)).Should().HaveCount(3);

            await repo.SaveAsync(new EntityPrefs("plug:a/voltage"), Ct);
            (await repo.ListAsync("plug:a/", Ct)).Keys.Should().Equal("plug:a/power");
        }
    }

    [Fact]
    public void Migration_AddEntityPrefs_IsAdditive_OnTheDeployedSchema()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options;
        using var ctx = new KasaDbContext(opts);
        var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        migrator.Migrate("20261102155409_AddClimateDaily");
        ctx.Database.ExecuteSqlRaw("INSERT INTO Settings (Key, Value) VALUES ('Tariff', '1.30')");

        ctx.Database.Migrate();

        ctx.Database.SqlQueryRaw<string>("SELECT Value FROM Settings WHERE Key = 'Tariff'").ToList().Should().Equal("1.30");
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM EntityPrefs").ToList().Should().Equal(0);
    }
}
