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
            "Plugs", "Samples", "Settings", "LiveSamples", "LiveMinuteAggregates", "RelayCommands",
            "MeterEvents", "ApplianceSignatures", "ApplianceRuns",
            "MieleDevices", "MieleStateSnapshots", "MieleCycles",
            "ClimateSensors", "ClimateReadings", "EntityPrefs"
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
    [Fact]
    public void AddPlugChannel_KeepsExistingRows_AsChannel0_AndAllowsASecondChannel()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options;
        using var ctx = new KasaDbContext(opts);
        var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        // Schema as deployed in 0.2.x, with one plug, one sample and one daily value.
        migrator.Migrate("20261101090240_AddPlugProtocol");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO Plugs (Id, Mac, IpAddress, Alias, AliasIsCustom, Model, HwVer, SwVer, OemId, FirstSeenUtc, LastSeenUtc, IsArchived, Protocol) " +
            "VALUES ('6f1c3c1e-0000-0000-0000-000000000001', 'c006c3702bfc', '192.168.254.105', 'Birou', 0, 'KP115(EU)', '1.0', '1.0.20', 'X', '2026-05-08 10:00:00+00:00', '2026-05-08 10:00:00+00:00', 0, 'legacy')");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO Samples (PlugId, TimestampUtc, Online, PowerMw) VALUES ('6f1c3c1e-0000-0000-0000-000000000001', 1, 1, 5000)");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO DailyEnergy (PlugId, Date, EnergyWh) VALUES ('6f1c3c1e-0000-0000-0000-000000000001', '2026-10-31', 1234)");

        ctx.Database.Migrate();

        ctx.Database.SqlQueryRaw<int>("SELECT Channel AS Value FROM Plugs").ToList().Should().Equal(0);
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Samples").ToList().Should().Equal(1);
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM DailyEnergy").ToList().Should().Equal(1);
        ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index' AND tbl_name = 'Plugs'")
            .ToList().Should().Contain("IX_Plugs_Mac_Channel").And.NotContain("IX_Plugs_Mac");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO Plugs (Id, Mac, IpAddress, Alias, AliasIsCustom, Model, HwVer, SwVer, OemId, FirstSeenUtc, LastSeenUtc, IsArchived, Protocol, Channel) " +
            "VALUES ('6f1c3c1e-0000-0000-0000-000000000002', 'c006c3702bfc', '192.168.254.105', 'Birou ch1', 0, 'KP115(EU)', '1.0', '1.0.20', 'X', '2026-05-08 10:00:00+00:00', '2026-05-08 10:00:00+00:00', 0, 'legacy', 1)");
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Plugs").ToList().Should().Equal(2);
    }

    [Fact]
    public void AddPlugAppliance_KeepsExistingPlugs_NoneMarkedAsASingleAppliance()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options;
        using var ctx = new KasaDbContext(opts);
        var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();

        // Schema as deployed in 0.9.0, with one plug and one event labelled by it.
        migrator.Migrate("20261102130647_AddClimate");
        ctx.Database.ExecuteSqlRaw(
            "INSERT INTO Plugs (Id, Mac, IpAddress, Alias, AliasIsCustom, Model, HwVer, SwVer, OemId, FirstSeenUtc, LastSeenUtc, IsArchived, Protocol, Channel, Role, StreamEnabled, NilmIgnored) " +
            "VALUES ('6f1c3c1e-0000-0000-0000-000000000001', 'aabbccddee00', '192.168.254.105', 'Frigider', 0, 'KP115(EU)', '1.0', '1.0.20', 'X', '2026-05-08 10:00:00+00:00', '2026-05-08 10:00:00+00:00', 0, 'legacy', 0, 0, 1, 0)");

        ctx.Database.Migrate();

        ctx.Database.SqlQueryRaw<int>("SELECT SingleAppliance AS Value FROM Plugs").ToList().Should().Equal(0);
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Plugs WHERE ApplianceName IS NULL AND ApplianceCategory IS NULL").ToList().Should().Equal(1);
        ctx.Database.SqlQueryRaw<string>("SELECT Alias AS Value FROM Plugs").ToList().Should().Equal("Frigider");
        ctx.Database.ExecuteSqlRaw("UPDATE Plugs SET SingleAppliance = 1, ApplianceName = 'Frigider', ApplianceCategory = 'fridge'");
        ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Plugs WHERE SingleAppliance = 1").ToList().Should().Equal(1);
    }

    [Fact]
    public void AddRolesAndLiveStream_ShellyMeterChannelsBecomeSubMetersWithoutStream_EverythingElseStaysAStreamedPlug()
    {
        // A temporary file, like kasa.db on the QNAP (0.3.0 schema with the two EM Gen3 channels and a Kasa plug).
        var path = Path.Combine(Path.GetTempPath(), $"kasa-roles-{Guid.NewGuid():N}.db");
        try
        {
            using (var conn = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                conn.Open();
                var opts = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options;
                using var ctx = new KasaDbContext(opts);
                ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate("20261101151623_AddPlugChannel");
                void Insert(string id, string mac, string model, string hw, string protocol, int channel) => ctx.Database.ExecuteSql(
                    $"INSERT INTO Plugs (Id, Mac, IpAddress, Alias, AliasIsCustom, Model, HwVer, SwVer, OemId, FirstSeenUtc, LastSeenUtc, IsArchived, Protocol, Channel) VALUES ({id}, {mac}, '192.168.254.1', 'x', 0, {model}, {hw}, '1', 'X', '2026-11-01 10:00:00+00:00', '2026-11-01 10:00:00+00:00', 0, {protocol}, {channel})");
                Insert("6f1c3c1e-0000-0000-0000-000000000001", "c006c3702bfc", "KP115(EU)", "1.0", "legacy", 0);
                Insert("6f1c3c1e-0000-0000-0000-000000000002", "aabbccddee00", "S3EM-002CXCEU", "EMG3", "shelly-rpc", 0);
                Insert("6f1c3c1e-0000-0000-0000-000000000003", "aabbccddee00", "S3EM-002CXCEU", "EMG3", "shelly-rpc", 1);
                Insert("6f1c3c1e-0000-0000-0000-000000000004", "aabbccddeeff", "SNPL-00112EU", "PlusPlugS", "shelly-rpc", 0);
                Insert("6f1c3c1e-0000-0000-0000-000000000005", "112233445566", "SPEM-003CEBEU", "Pro3EM", "shelly-rpc", 0);
                ctx.Database.ExecuteSqlRaw("INSERT INTO Samples (PlugId, TimestampUtc, Online, PowerMw) VALUES ('6f1c3c1e-0000-0000-0000-000000000002', 1, 1, 5000)");

                ctx.Database.Migrate();

                ctx.Database.SqlQueryRaw<int>("SELECT Role AS Value FROM Plugs ORDER BY Id").ToList().Should().Equal(0, 2, 2, 0, 2);
                ctx.Database.SqlQueryRaw<int>("SELECT StreamEnabled AS Value FROM Plugs ORDER BY Id").ToList().Should().Equal(1, 0, 0, 1, 0);
                ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Samples").ToList().Should().Equal(1);
                ctx.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Plugs WHERE Role = 1").ToList().Should().Equal(0);   // the main meter is chosen by the user
                // The database itself allows one main meter only (partial unique index on Role = 1).
                ctx.Database.ExecuteSqlRaw("UPDATE Plugs SET Role = 1 WHERE Id = '6f1c3c1e-0000-0000-0000-000000000002'");
                var second = () => ctx.Database.ExecuteSqlRaw("UPDATE Plugs SET Role = 1 WHERE Id = '6f1c3c1e-0000-0000-0000-000000000003'");
                second.Should().Throw<SqliteException>().Where(e => e.Message.Contains("UNIQUE"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }
}
