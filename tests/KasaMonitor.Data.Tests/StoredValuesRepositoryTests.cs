using FluentAssertions;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KasaMonitor.Data.Tests;

/// <summary>
/// What KasaDbContext does before a value reaches the database (0.12.0): texts longer than their HasMaxLength are cut (an
/// identifier is refused instead), non-finite nullable doubles become NULL. On SQLite here and on SQL Server in
/// KasaMonitor.SqlServer.Tests (where the database itself would refuse them).
/// </summary>
public class StoredValuesRepositoryTests
{
    private static PlugEntity Plug(string alias, string mac = "aabbccddee00") => new()
    {
        Id = Guid.NewGuid(), Mac = mac, IpAddress = "192.168.254.10", Alias = alias, Model = "KP115(EU)", HwVer = "1.0", SwVer = "1.0.20",
        OemId = "X", FirstSeenUtc = DateTimeOffset.UtcNow, LastSeenUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ALongName_IsCutToItsLength_OnInsertAndOnUpdate_WithoutSplittingAnEmoji()
    {
        using var db = new TestDbFactory();
        var plug = Plug(new string('a', KasaDbContext.NameLength + 50));
        await using (var ctx = db.CreateContext())
        {
            ctx.Plugs.Add(plug);
            ctx.ClimateSensors.Add(new ClimateSensorEntity { Id = Guid.NewGuid(), Source = "govee", ExternalId = "x", Name = new string('b', 300), Room = "Baie" });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = db.CreateContext())
        {
            (await ctx.Plugs.SingleAsync()).Alias.Should().Be(new string('a', KasaDbContext.NameLength));
            (await ctx.ClimateSensors.SingleAsync()).Name.Should().HaveLength(KasaDbContext.NameLength);
            var p = await ctx.Plugs.SingleAsync();
            p.Alias = new string('c', KasaDbContext.NameLength - 1) + "🔌";   // the emoji would straddle the limit
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = db.CreateContext())
            (await ctx.Plugs.SingleAsync()).Alias.Should().Be(new string('c', KasaDbContext.NameLength - 1));
    }

    [Fact]
    public async Task ATooLongIdentifier_IsRefusedWithTheColumn_NeverCut()
    {
        using var db = new TestDbFactory();
        await using var ctx = db.CreateContext();
        ctx.ClimateSensors.Add(new ClimateSensorEntity { Id = Guid.NewGuid(), Source = "govee", ExternalId = new string('x', KasaDbContext.ExternalIdLength + 1), Name = "n", Room = "r" });
        var save = () => ctx.SaveChangesAsync();
        (await save.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ClimateSensors.ExternalId").And.Contain("256");
    }

    [Fact]
    public async Task NonFiniteNullableDoubles_BecomeNull_InEveryTable()
    {
        using var db = new TestDbFactory();
        var device = Guid.NewGuid();
        var sensor = Guid.NewGuid();
        await using (var ctx = db.CreateContext())
        {
            ctx.HeatingReadings.Add(new HeatingReadingEntity { DeviceId = device, Ts = 1, BoilerTempC = double.NaN, PressureBar = double.PositiveInfinity, SupplyTempC = 40 });
            ctx.ClimateReadings.Add(new ClimateReadingEntity { SensorId = sensor, Ts = 1, TemperatureC = 21, HumidityPercent = double.NaN });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = db.CreateContext())
        {
            var r = await ctx.HeatingReadings.SingleAsync();
            r.BoilerTempC.Should().BeNull();
            r.PressureBar.Should().BeNull();
            r.SupplyTempC.Should().Be(40);
            (await ctx.ClimateReadings.SingleAsync()).HumidityPercent.Should().BeNull();
        }
    }
}
