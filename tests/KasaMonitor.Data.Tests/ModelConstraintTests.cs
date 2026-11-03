using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KasaMonitor.Data.Tests;

/// <summary>
/// The constraints the database enforces by itself, on the model (SQLite, <c>EnsureCreated</c>). Since 0.12.0 the
/// migrations are SQL Server's (one InitialCreate); the same checks on SQL Server are in KasaMonitor.SqlServer.Tests.
/// The former step-by-step SQLite migration tests (0.1 → 0.9) went with the SQLite migrations.
/// </summary>
public class ModelConstraintTests
{
    private static void InsertPlug(KasaDbContext ctx, string id, string mac, int channel, int role) => ctx.Database.ExecuteSql(
        $"INSERT INTO Plugs (Id, Mac, IpAddress, Alias, AliasIsCustom, Model, HwVer, SwVer, OemId, FirstSeenUtc, LastSeenUtc, IsArchived, Channel, Role, StreamEnabled, NilmIgnored, SingleAppliance) VALUES ({id}, {mac}, '192.168.254.1', 'x', 0, 'S3EM-002CXCEU', 'EMG3', '1', 'X', '2026-11-01 10:00:00+00:00', '2026-11-01 10:00:00+00:00', 0, {channel}, {role}, 1, 0, 0)");

    [Fact]
    public void Plugs_ProtocolDefaultsToLegacy_MacAndChannelUnique_AndOneMainMeterOnly()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        InsertPlug(ctx, "6F1C3C1E-0000-0000-0000-000000000001", "aabbccddee00", 0, 2);
        InsertPlug(ctx, "6F1C3C1E-0000-0000-0000-000000000002", "aabbccddee00", 1, 2);   // a second channel of the same meter
        ctx.Database.SqlQueryRaw<string>("SELECT Protocol AS Value FROM Plugs").ToList().Should().Equal("legacy", "legacy");

        var sameChannel = () => InsertPlug(ctx, "6F1C3C1E-0000-0000-0000-000000000003", "aabbccddee00", 1, 0);
        sameChannel.Should().Throw<SqliteException>().Where(e => e.Message.Contains("UNIQUE"));

        // The database itself allows one main meter only (filtered unique index on Role = 1).
        ctx.Database.ExecuteSqlRaw("UPDATE Plugs SET Role = 1 WHERE Id = '6F1C3C1E-0000-0000-0000-000000000001'");
        var second = () => ctx.Database.ExecuteSqlRaw("UPDATE Plugs SET Role = 1 WHERE Id = '6F1C3C1E-0000-0000-0000-000000000002'");
        second.Should().Throw<SqliteException>().Where(e => e.Message.Contains("UNIQUE"));
    }

    [Fact]
    public void MieleCycles_ApiStartUniquePerDevice_NullsNeverCollide()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source) VALUES ('dev', 100, 100, '[]', 'observed')");
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source) VALUES ('dev', 300, 300, '[]', 'observed')");
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source, ApiStartTs) VALUES ('dev', 400, 400, '[]', 'api', 400)");
        ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source, ApiStartTs) VALUES ('other', 400, 400, '[]', 'api', 400)");
        var duplicate = () => ctx.Database.ExecuteSqlRaw("INSERT INTO MieleCycles (DeviceId, StartTs, LastRunningTs, PhasesJson, Source, ApiStartTs) VALUES ('dev', 401, 401, '[]', 'api', 400)");
        duplicate.Should().Throw<SqliteException>().Where(e => e.Message.Contains("UNIQUE"));
    }

    [Fact]
    public void StringKeysAndIndexedStrings_HaveAnExplicitLength()
    {
        // SQL Server cannot index nvarchar(max), and an unbounded key becomes nvarchar(450) (and overflows the 900-byte key
        // of HeatingEnergyPeriods). Every string in a key or an index has a length in the model.
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var missing = new List<string>();
        foreach (var entity in ctx.Model.GetEntityTypes())
        {
            var indexed = entity.GetKeys().SelectMany(k => k.Properties).Concat(entity.GetIndexes().SelectMany(i => i.Properties));
            missing.AddRange(indexed.Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is null)
                .Select(p => entity.GetTableName() + "." + p.Name).Distinct());
        }
        missing.Should().BeEmpty();
    }
}
