using FluentAssertions;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace KasaMonitor.SqlServer.Tests;

/// <summary>The migrations, the database of create-database.sql and the constraints, on SQL Server.</summary>
public class SchemaTests
{
    [SqlServerFact]
    public async Task TheMigrations_CreateEveryTableOfTheModel()
    {
        var cs = await MsSql.NewDatabaseAsync("kasa_schema");
        await using var ctx = MsSql.Context(cs);
        ctx.Database.GetMigrations().Select(m => m[(m.IndexOf('_') + 1)..]).Should().Equal("InitialCreate", "DurationBands");

        await ctx.Database.MigrateAsync();

        (await ctx.Database.GetAppliedMigrationsAsync()).Should().HaveCount(2);
        ctx.Database.HasPendingModelChanges().Should().BeFalse("the migration matches the model");
        var tables = await ctx.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sys.tables WHERE name NOT LIKE '__EF%'").ToListAsync();
        tables.Should().BeEquivalentTo(ctx.Model.GetEntityTypes().Select(e => e.GetTableName()));
        tables.Should().Contain(["Plugs", "Samples", "LiveSamples", "LiveMinuteAggregates", "MeterEvents", "HeatingReadings", "MieleCycles",
            "ClimateSensors", "ClimateReadings", "ClimateDaily"]);
    }

    [SqlServerFact]
    public async Task DurationBands_AppliesOverExistingData_AndLeavesEverySignatureWithoutABand()
    {
        var cs = await MsSql.NewDatabaseAsync("kasa_bands");
        await using var ctx = MsSql.Context(cs);
        var migrator = ctx.GetService<IMigrator>();
        var first = ctx.Database.GetMigrations().First();
        await migrator.MigrateAsync(first);
        await ctx.Database.ExecuteSqlRawAsync("""
            INSERT INTO ApplianceSignatures (Name, Icon, Category, MeanDeltaP, MeanDeltaQ, MeanTransient, Count, LabelSource, Hidden)
            VALUES (N'Fierbător', 'bi-fire', 'cooking', 1430, 90, 1, 120, 2, 0)
            """);

        await ctx.Database.MigrateAsync();

        var kept = await ctx.ApplianceSignatures.AsNoTracking().SingleAsync();
        kept.Name.Should().Be("Fierbător");
        kept.Count.Should().Be(120);
        kept.MinRunSeconds.Should().BeNull();
        kept.MaxRunSeconds.Should().BeNull();
        kept.SplitFromId.Should().BeNull();

        kept.MaxRunSeconds = 40;
        kept.SplitFromId = 7;
        ctx.ApplianceSignatures.Update(kept);
        await ctx.SaveChangesAsync();
        (await ctx.ApplianceSignatures.AsNoTracking().SingleAsync()).MaxRunSeconds.Should().Be(40);
    }

    [SqlServerFact]
    public async Task TheGenericValueTables_AreClusteredOnTheirCompositeKey_AndTheIdsAreIdentities()
    {
        // 0.11 (R6): on SQLite these tables were WITHOUT ROWID; on SQL Server the clustered primary key is the same layout.
        var cs = await MsSql.AppConnectionStringAsync();
        foreach (var (table, columns) in new[] { ("GenericReadings", "EntityId,Ts"), ("GenericHourly", "EntityId,HourTs") })
            (await MsSql.ScalarAsync<string>(cs, $"""
                SELECT CONVERT(nvarchar(60), i.type_desc) COLLATE DATABASE_DEFAULT + ':'
                     + STRING_AGG(CONVERT(nvarchar(128), c.name) COLLATE DATABASE_DEFAULT, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
                FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('{table}') AND i.is_primary_key = 1 GROUP BY i.type_desc
                """)).Should().Be("CLUSTERED:" + columns, table);
        foreach (var table in new[] { "GenericDevices", "GenericEntities" })
            (await MsSql.ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM sys.identity_columns WHERE object_id = OBJECT_ID('{table}') AND name = 'Id'")).Should().Be(1, table);
    }

    [SqlServerFact]
    public async Task NoColumnIsUnbounded_ExceptTheFreeTexts_AndNoKeyUsesTheDefault450()
    {
        var cs = await MsSql.AppConnectionStringAsync();
        var unbounded = await MsSql.Context(cs).Database.SqlQueryRaw<string>("""
            SELECT t.name + '.' + c.name AS Value FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
            WHERE c.max_length = -1 AND t.name NOT LIKE '__EF%'
            """).ToListAsync();
        // Free texts: errors, notes, JSON, setting values, relay results.
        unbounded.Should().BeEquivalentTo("HeatingDevices.LastError", "HeatingReadings.ActiveError", "HeatingRawFeatures.Json",
            "MieleCycles.PhasesJson", "MieleCycles.EnergyProfileJson", "Plugs.Notes", "Plugs.UnsupportedReason", "RelayCommands.Result",
            "Samples.ErrorMessage", "Settings.Value", "GenericEntities.DiscoveryJson");
        (await MsSql.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM sys.columns WHERE max_length = 900 AND system_type_id = 231 AND object_id IN (SELECT object_id FROM sys.tables WHERE name NOT LIKE '__EF%')"))
            .Should().Be(0, "no nvarchar(450) key");
    }

    [SqlServerFact]
    public async Task TheDatabase_FromCreateDatabaseSql_IsCaseAndAccentSensitive_WhateverTheServer()
    {
        var cs = await MsSql.AppConnectionStringAsync();
        await MsSql.ResetAsync();
        (await MsSql.ScalarAsync<string>(cs, "SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'))")).Should().Be(MsSql.Collation);
        (await MsSql.ScalarAsync<string>(cs, "SELECT CONVERT(nvarchar(60), DATABASEPROPERTYEX(DB_NAME(), 'Recovery'))")).Should().Be("SIMPLE");
        (await MsSql.ScalarAsync<int>(cs, "SELECT IS_ROLEMEMBER('db_owner')")).Should().Be(1, "the login owns its database only");
        (await MsSql.ScalarAsync<int>(cs, "SELECT IS_SRVROLEMEMBER('sysadmin')")).Should().Be(0);

        await using (var ctx = MsSql.Context(cs))
        {
            // Like SQLite: ids that differ only by case are two rows, and a lookup is exact.
            ctx.ClimateSensors.Add(new ClimateSensorEntity { Id = Guid.NewGuid(), Source = "govee", ExternalId = "AA:BB:CC:DD:EE:FF:00:31", Name = "Baie" });
            ctx.ClimateSensors.Add(new ClimateSensorEntity { Id = Guid.NewGuid(), Source = "govee", ExternalId = "aa:bb:cc:dd:ee:ff:00:31", Name = "Băi" });
            ctx.Settings.Add(new SettingEntity { Key = "TapoUsername", Value = "a" });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = MsSql.Context(cs))
        {
            // A second context: EF's SQL Server provider compares string keys case-insensitively in its change tracker.
            ctx.Settings.Add(new SettingEntity { Key = "tapousername", Value = "b" });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = MsSql.Context(cs))
        {
            (await ctx.ClimateSensors.CountAsync(s => s.ExternalId == "aa:bb:cc:dd:ee:ff:00:31")).Should().Be(1);
            (await ctx.ClimateSensors.CountAsync(s => s.Name == "Bai")).Should().Be(0, "accent-sensitive: „Băi” is not „Bai”");
            (await ctx.Settings.SingleAsync(s => s.Key == "TapoUsername")).Value.Should().Be("a");
        }
    }

    [SqlServerFact]
    public async Task Constraints_OneMainMeter_MacAndChannel_MieleApiStartWithNulls()
    {
        var cs = await MsSql.AppConnectionStringAsync();
        await MsSql.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        PlugEntity Plug(int channel, int role) => new()
        {
            Id = Guid.NewGuid(), Mac = "aabbccddee00", Channel = channel, Role = role, IpAddress = "192.168.254.1", Alias = "x", Model = "S3EM-002CXCEU",
            HwVer = "EMG3", SwVer = "1", OemId = "X", FirstSeenUtc = now, LastSeenUtc = now,
        };
        await using (var ctx = MsSql.Context(cs))
        {
            ctx.Plugs.AddRange(Plug(0, 1), Plug(1, 2));
            await ctx.SaveChangesAsync();
            (await ctx.Plugs.Select(p => p.Protocol).ToListAsync()).Should().Equal("legacy", "legacy");
        }
        await using (var ctx = MsSql.Context(cs))
        {
            ctx.Plugs.Add(Plug(1, 0));
            var sameChannel = () => ctx.SaveChangesAsync();
            (await sameChannel.Should().ThrowAsync<DbUpdateException>()).WithInnerException<SqlException>().Where(e => e.Number == 2601);
        }
        await using (var ctx = MsSql.Context(cs))
        {
            ctx.Plugs.Add(Plug(2, 1));
            var secondMain = () => ctx.SaveChangesAsync();
            (await secondMain.Should().ThrowAsync<DbUpdateException>()).WithInnerException<SqlException>().Where(e => e.Number == 2601,
                "IX_Plugs_SingleMainMeter: one main meter per installation");
        }
        await using (var ctx = MsSql.Context(cs))
        {
            MieleCycleEntity Cycle(string device, long start, long? apiStart) => new()
            {
                DeviceId = device, StartTs = start, LastRunningTs = start, PhasesJson = "[]", Source = apiStart is null ? "observed" : "api", ApiStartTs = apiStart,
            };
            // SQL Server treats NULLs as equal in a unique index: EF filters the index (WHERE ApiStartTs IS NOT NULL).
            ctx.MieleCycles.AddRange(Cycle("dev", 100, null), Cycle("dev", 300, null), Cycle("dev", 400, 400), Cycle("other", 400, 400));
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = MsSql.Context(cs))
        {
            ctx.MieleCycles.Add(new MieleCycleEntity { DeviceId = "dev", StartTs = 401, LastRunningTs = 401, PhasesJson = "[]", Source = "api", ApiStartTs = 400 });
            var duplicate = () => ctx.SaveChangesAsync();
            (await duplicate.Should().ThrowAsync<DbUpdateException>()).WithInnerException<SqlException>().Where(e => e.Number == 2601);
        }
    }

    [SqlServerFact]
    public async Task Timestamps_TicksAndDateTimeOffset_RoundTrip()
    {
        var cs = await MsSql.AppConnectionStringAsync();
        await MsSql.ResetAsync();
        var plug = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 11, 3, 9, 15, 30, 123, TimeSpan.Zero).AddTicks(4567);
        var local = new DateTimeOffset(2026, 11, 3, 11, 15, 30, TimeSpan.FromHours(2));
        await using (var ctx = MsSql.Context(cs))
        {
            ctx.Samples.Add(new SampleEntity { PlugId = plug, TimestampUtc = t, Online = true, PowerMw = 5000 });
            ctx.Plugs.Add(new PlugEntity { Id = plug, Mac = "aabbccddee01", IpAddress = "1", Alias = "x", Model = "m", HwVer = "1", SwVer = "1", OemId = "o",
                FirstSeenUtc = local, LastSeenUtc = t });
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = MsSql.Context(cs))
        {
            (await ctx.Samples.SingleAsync()).TimestampUtc.Should().Be(t);
            (await MsSql.ScalarAsync<long>(cs, "SELECT TimestampUtc FROM Samples")).Should().Be(t.UtcTicks, "ticks in a bigint, as in SQLite");
            var p = await ctx.Plugs.SingleAsync();
            p.FirstSeenUtc.Should().Be(local);
            p.FirstSeenUtc.Offset.Should().Be(TimeSpan.FromHours(2), "datetimeoffset keeps the offset");
        }
    }
}
