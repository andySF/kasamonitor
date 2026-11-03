using FluentAssertions;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class KasaDbContextTests
{
    [Fact]
    public void EnsureCreated_BuildsAllTables()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();

        var tables = ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").ToList();

        tables.Should().Contain(new[]
        {
            "DailyEnergy", "MonthlyEnergy", "OneHourAggregates", "OneMinuteAggregates",
            "Plugs", "Samples", "Settings",
            "HeatingDevices", "HeatingEnergyDaily", "HeatingEnergyPeriods", "HeatingRawFeatures", "HeatingReadings"
        });
        tables.Should().BeEquivalentTo(ctx.Model.GetEntityTypes().Select(e => e.GetTableName()), "every table of the model, nothing else");
    }

    [Fact]
    public void InsertAndReadPlug_RoundTrips()
    {
        using var f = new TestDbFactory();
        using (var ctx = f.CreateContext())
        {
            ctx.Plugs.Add(new PlugEntity
            {
                Id = Guid.NewGuid(),
                Mac = "c006c3702bfc",
                IpAddress = "192.168.254.105",
                Alias = "Birou",
                Model = "KP115(EU)",
                HwVer = "1.0",
                SwVer = "1.0.20 Build 221125 Rel.092759",
                OemId = "X",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow
            });
            ctx.SaveChanges();
        }

        using (var ctx = f.CreateContext())
        {
            var plug = ctx.Plugs.Single();
            plug.Mac.Should().Be("c006c3702bfc");
            plug.Alias.Should().Be("Birou");
        }
    }
}
