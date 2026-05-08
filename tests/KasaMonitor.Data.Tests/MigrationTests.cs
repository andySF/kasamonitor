using FluentAssertions;
using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
}
