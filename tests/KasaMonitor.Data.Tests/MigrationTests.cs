using FluentAssertions;
using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class MigrationTests
{
    [Fact]
    public void Migrate_ProducesExpectedSchema()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();

        var opts = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options;
        using var ctx = new KasaDbContext(opts);
        ctx.Database.Migrate();

        var tables = ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name").ToList();

        tables.Should().Contain(new[]
        {
            "DailyEnergy", "MonthlyEnergy", "OneHourAggregates", "OneMinuteAggregates",
            "Plugs", "Samples", "Settings"
        });
    }

    [Fact]
    public void AddPlugProtocol_KeepsExistingRows_AndMarksThemLegacy()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options;
        using var ctx = new KasaDbContext(opts);
        var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        // Schema as deployed in 0.1.0, with one plug and one sample.
        migrator.Migrate("20260508152255_ConvertBucketStartUtcToLong");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO Plugs (Id, Mac, IpAddress, Alias, AliasIsCustom, Model, HwVer, SwVer, OemId, FirstSeenUtc, LastSeenUtc, IsArchived) " +
            "VALUES ('6f1c3c1e-0000-0000-0000-000000000001', 'c006c3702bfc', '192.168.254.105', 'Birou', 0, 'KP115(EU)', '1.0', '1.0.20', 'X', '2026-05-08 10:00:00+00:00', '2026-05-08 10:00:00+00:00', 0)");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO Samples (PlugId, TimestampUtc, Online, PowerMw) VALUES ('6f1c3c1e-0000-0000-0000-000000000001', 1, 1, 5000)");

        ctx.Database.Migrate();

        ctx.Database.SqlQueryRaw<string>("SELECT Protocol AS Value FROM Plugs").ToList().Should().Equal("legacy");
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Plugs WHERE UnsupportedReason IS NULL").ToList().Should().Equal(1);
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Samples").ToList().Should().Equal(1);
    }
}
