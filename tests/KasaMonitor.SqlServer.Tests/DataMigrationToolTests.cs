using FluentAssertions;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using KasaMonitor.DataMigration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace KasaMonitor.SqlServer.Tests;

/// <summary>The SQLite → SQL Server copy (tools/KasaMonitor.DataMigration) on a synthetic kasa.db and, on request, on a real copy.</summary>
public class DataMigrationToolTests(ITestOutputHelper output)
{
    /// <summary>Path of a real kasa.db to copy (opt-in; the file is copied first, never opened in place).</summary>
    public const string RealSourceVariable = "KASA_MIGRATION_SOURCE_DB";
    /// <summary>Where to write the report of the real copy (optional).</summary>
    public const string RealReportVariable = "KASA_MIGRATION_REPORT";

    private static readonly Guid PlugA = Guid.Parse("6f1c3c1e-0000-0000-0000-00000000000a");
    private static readonly Guid PlugB = Guid.Parse("6f1c3c1e-0000-0000-0000-00000000000b");

    /// <summary>A kasa.db as 0.10/0.11 wrote it: the same model on SQLite (Guid and DateTimeOffset as TEXT, bool as INTEGER).</summary>
    private static string SyntheticSource(int samples)
    {
        var path = Path.Combine(Path.GetTempPath(), $"kasa-src-{Guid.NewGuid():N}.db");
        using var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite($"Data Source={path};Pooling=False").Options);
        ctx.Database.EnsureCreated();
        ctx.Database.ExecuteSqlRaw("CREATE TABLE \"__EFMigrationsHistory\" (\"MigrationId\" TEXT NOT NULL PRIMARY KEY, \"ProductVersion\" TEXT NOT NULL)");
        ctx.Database.ExecuteSqlRaw("INSERT INTO \"__EFMigrationsHistory\" VALUES ('20261102155409_AddClimateDaily', '10.0.14')");
        var seen = new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.FromHours(3)).AddTicks(1234567);
        PlugEntity Plug(Guid id, string mac, int role) => new()
        {
            Id = id, Mac = mac, IpAddress = "192.168.254.105", Alias = "Mașina de spălat „nouă”", Model = "KP115(EU)", HwVer = "1.0", SwVer = "1.0.20 Build 221125",
            OemId = "X", FirstSeenUtc = seen, LastSeenUtc = seen.AddDays(1), Role = role, StreamEnabled = role == 1, Notes = role == 1 ? null : "note",
        };
        ctx.Plugs.AddRange(Plug(PlugA, "aabbccddee00", 1), Plug(PlugB, "aabbccddee01", 0));
        var t0 = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < samples; i++)
            ctx.Samples.Add(new SampleEntity
            {
                PlugId = i % 2 == 0 ? PlugA : PlugB, TimestampUtc = t0.AddSeconds(10 * i), Online = i % 97 != 0, PowerMw = 1000 + i % 5000,
                TotalWh = i, RelayOn = i % 3 == 0 ? null : i % 2 == 0, ErrorMessage = i % 97 == 0 ? "timeout" : null,
            });
        for (var m = 0; m < 1500; m++)
            ctx.LiveMinuteAggregates.Add(new LiveMinuteAggregateEntity { PlugId = PlugA, MinuteTs = t0.ToUnixTimeSeconds() + 60 * m, MinW = 1.5, AvgW = 300.25 + m,
                MaxW = 900, AvgVoltageV = m % 10 == 0 ? null : 231.4, EnergyWh = 5.0041 + m * 0.001, SampleCount = 60, CoveredSeconds = 59.5 });
        ctx.Settings.Add(new SettingEntity { Key = "TariffRonPerKwh", Value = "1.30" });
        ctx.ClimateSensors.Add(new ClimateSensorEntity { Id = Guid.NewGuid(), Source = "govee", ExternalId = "AA:BB:CC:DD:EE:FF:00:31", Name = "Baie", Room = "Baie" });
        ctx.SaveChanges();
        // Ids with gaps, as after the retention deletes.
        ctx.Database.ExecuteSqlRaw("DELETE FROM Samples WHERE Id % 7 = 0");
        ctx.MieleCycles.Add(new MieleCycleEntity { DeviceId = "000148508574", StartTs = 100, LastRunningTs = 200, PhasesJson = "[]", Source = "observed" });
        ctx.SaveChanges();
        // 0.11: the generic devices (MQTT Discovery) and the entity preferences, ids with a gap.
        ctx.Database.ExecuteSqlRaw("""
            INSERT INTO GenericDevices (Id, Integration, ExternalId, Name, NameIsCustom, Room, AutoCategory, IsArchived, FirstSeenUtc)
            VALUES (3, 'mqtt', 'zigbee2mqtt_0x00158d0001', 'Senzor baie', 0, 'Baie', 'climate', 0, 1762160000),
                   (7, 'mqtt', 'shellyplug-s-aabbcc', 'Priză „birou”', 1, '', 'plug', 0, 1762160100)
            """);
        ctx.Database.ExecuteSqlRaw("""
            INSERT INTO GenericEntities (Id, DeviceId, Integration, ExternalId, Component, Name, Kind, DataType, Class, Category, Writable,
                                         StateClassInferred, DiscoveryJson, IsRemoved, LastNumber, LastTs)
            VALUES (11, 3, 'mqtt', '0x00158d0001_temperature', 'sensor', 'Temperatură', 'measurement', 'number', 'temperature', 'primary', 0, 0,
                    '{{"unit_of_measurement":"°C"}}', 0, 21.5, 1762160200)
            """);
        for (var i = 0; i < 2000; i++)
            ctx.GenericReadings.Add(new GenericReadingEntity { EntityId = 11, Ts = 1762160000 + 10 * i, Value = 20 + i % 30 / 10.0 });
        ctx.GenericHourly.Add(new GenericHourlyEntity { EntityId = 11, HourTs = 1762160400, Min = 20, Avg = 21.04, Max = 22.9, Last = 21.5, Count = 360, Delta = null });
        ctx.EntityPrefs.Add(new EntityPrefEntity { Key = "mqtt:7/power", Visible = false, NameOverride = "Birou" });
        ctx.SaveChanges();
        return path;
    }

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch (IOException) { }
    }

    [SqlServerFact]
    public async Task Copy_EveryRowAndValue_IdsKept_ThenNewRowsContinueAfterThem_AndTheReportSaysOk()
    {
        var source = SyntheticSource(samples: 12_345);
        try
        {
            var cs = await MsSql.NewDatabaseAsync("kasa_mig");
            var report = await new DataMigrator(output.WriteLine).RunAsync(new MigrationOptions(source, cs, BatchSize: 1000), CancellationToken.None);
            output.WriteLine(report.ToMarkdown());

            report.Ok.Should().BeTrue();
            report.Tables.Single(t => t.Table == "Samples").Should().Match<TableResult>(t => t.SourceRows == t.TargetRows && t.TargetRows == 12_345 - 12_345 / 7);
            report.Tables.Single(t => t.Table == "Samples").Sums.Select(s => s.Column).Should().Contain(["Id", "PowerMw", "TotalWh", "Online"]);
            report.Tables.Single(t => t.Table == "LiveMinuteAggregates").Sums.Select(s => s.Column).Should().Contain(["EnergyWh", "AvgW", "distinct(PlugId)"]);
            // Every column is checked: texts by their length, GUIDs by their distinct values, dates by their instants.
            report.Tables.Single(t => t.Table == "Plugs").Sums.Select(s => s.Column).Should().Contain(
                ["len(Alias)", "len(Notes)", "distinct(Id)", "time(FirstSeenUtc)", "time(LastSeenUtc)", "Role"]);
            report.Tables.Single(t => t.Table == "Settings").Sums.Select(s => s.Column).Should().Equal("len(Key)", "len(Value)");

            await using var src = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite($"Data Source={source};Pooling=False").Options);
            await using var dst = MsSql.Context(cs);
            (await dst.Plugs.OrderBy(p => p.Mac).ToListAsync()).Should().BeEquivalentTo(await src.Plugs.OrderBy(p => p.Mac).ToListAsync(),
                o => o.WithStrictOrdering(), "Guid, DateTimeOffset with its offset, bool, texts with diacritics");
            (await dst.Samples.OrderBy(s => s.Id).Take(500).ToListAsync()).Should().BeEquivalentTo(await src.Samples.OrderBy(s => s.Id).Take(500).ToListAsync(),
                o => o.WithStrictOrdering());
            (await dst.LiveMinuteAggregates.OrderBy(m => m.MinuteTs).ToListAsync()).Should().BeEquivalentTo(
                await src.LiveMinuteAggregates.OrderBy(m => m.MinuteTs).ToListAsync(), o => o.WithStrictOrdering());

            foreach (var (table, rows) in new[] { ("GenericDevices", 2L), ("GenericEntities", 1L), ("GenericReadings", 2000L), ("GenericHourly", 1L), ("EntityPrefs", 1L) })
                report.Tables.Single(t => t.Table == table).Should().Match<TableResult>(t => t.Ok && t.TargetRows == rows, table);
            (await dst.GenericDevices.OrderBy(d => d.Id).Select(d => d.Id).ToListAsync()).Should().Equal(3, 7);
            (await dst.GenericDevices.SingleAsync(d => d.Id == 7)).Name.Should().Be("Priză „birou”");
            dst.GenericDevices.Add(new GenericDeviceEntity { Integration = "mqtt", ExternalId = "new", Name = "n", Room = "", AutoCategory = "generic", FirstSeenUtc = 1 });
            await dst.SaveChangesAsync();
            (await dst.GenericDevices.MaxAsync(d => d.Id)).Should().Be(8, "the identity continues after the copied ids");

            // The identity continues after the copied ids (no collision with the first insert of the app).
            var maxId = await dst.Samples.MaxAsync(s => s.Id);
            dst.Samples.Add(new SampleEntity { PlugId = PlugA, TimestampUtc = DateTimeOffset.UtcNow, Online = true });
            await dst.SaveChangesAsync();
            (await dst.Samples.MaxAsync(s => s.Id)).Should().BeGreaterThan(maxId);
        }
        finally
        {
            Delete(source);
        }
    }

    [SqlServerFact]
    public async Task ANonEmptyTarget_IsRefused_Truncate_ReplacesIt_AndResume_KeepsTheVerifiedTables()
    {
        var source = SyntheticSource(samples: 3000);
        try
        {
            var cs = await MsSql.NewDatabaseAsync("kasa_mig2");
            (await new DataMigrator().RunAsync(new MigrationOptions(source, cs), CancellationToken.None)).Ok.Should().BeTrue();

            var again = () => new DataMigrator().RunAsync(new MigrationOptions(source, cs), CancellationToken.None);
            (await again.Should().ThrowAsync<MigrationException>()).Which.Message.Should().Contain("not empty").And.Contain("--truncate");

            // An interrupted copy: one table half full, another empty, a third with the right count but a text that differs.
            await MsSql.ExecAsync(cs, "DELETE FROM Samples WHERE Id > 1000; TRUNCATE TABLE LiveMinuteAggregates; UPDATE Settings SET Value = Value + N'x'");
            var resumed = await new DataMigrator().RunAsync(new MigrationOptions(source, cs, TargetMode.Resume), CancellationToken.None);
            resumed.Ok.Should().BeTrue();
            resumed.Tables.Single(t => t.Table == "Samples").Action.Should().Be(DataMigrator.ActionCopied);
            resumed.Tables.Single(t => t.Table == "LiveMinuteAggregates").Action.Should().Be(DataMigrator.ActionCopied);
            resumed.Tables.Single(t => t.Table == "Settings").Action.Should().Be(DataMigrator.ActionCopied, "the length check sees the changed text");
            resumed.Tables.Single(t => t.Table == "Plugs").Action.Should().Be(DataMigrator.ActionKept);
            (await MsSql.ScalarAsync<string>(cs, "SELECT Value FROM Settings")).Should().Be("1.30");

            var truncated = await new DataMigrator().RunAsync(new MigrationOptions(source, cs, TargetMode.Truncate), CancellationToken.None);
            truncated.Ok.Should().BeTrue();
            truncated.Tables.Should().OnlyContain(t => t.Action == DataMigrator.ActionCopied);
        }
        finally
        {
            Delete(source);
        }
    }

    [SqlServerFact]
    public async Task ASourceWithoutAColumnThatHasADefault_GetsTheDefault()
    {
        // An older source without Plugs.Protocol (before 0.2): the column is NOT NULL with the default 'legacy'.
        var source = SyntheticSource(samples: 10);
        try
        {
            await using (var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite($"Data Source={source};Pooling=False").Options))
                await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE Plugs DROP COLUMN Protocol");
            var cs = await MsSql.NewDatabaseAsync("kasa_mig4");
            var report = await new DataMigrator().RunAsync(new MigrationOptions(source, cs), CancellationToken.None);
            report.Ok.Should().BeTrue();
            (await MsSql.ScalarAsync<int>(cs, "SELECT COUNT(*) FROM Plugs WHERE Protocol = 'legacy'")).Should().Be(2);
        }
        finally
        {
            Delete(source);
        }
    }

    [SqlServerFact]
    public async Task AValueLongerThanItsColumn_StopsAtThatTable_AndSaysToResume()
    {
        var source = SyntheticSource(samples: 10);
        try
        {
            await using (var ctx = new KasaDbContext(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite($"Data Source={source};Pooling=False").Options))
                await ctx.Database.ExecuteSqlAsync($"UPDATE Plugs SET Alias = {new string('x', KasaDbContext.NameLength + 1)} WHERE Role = 0");
            var cs = await MsSql.NewDatabaseAsync("kasa_mig3");
            var report = await new DataMigrator().RunAsync(new MigrationOptions(source, cs), CancellationToken.None);
            report.Ok.Should().BeFalse();
            report.Tables.Last().Table.Should().Be("Plugs");
            report.Tables.Last().Error.Should().Contain("Plugs.Alias").And.Contain("nvarchar(200)");
            report.Notes.Should().Contain(n => n.Contains("--resume"));
        }
        finally
        {
            Delete(source);
        }
    }

    /// <summary>On a copy of a real kasa.db (KASA_MIGRATION_SOURCE_DB): the whole copy, verified, with the report. Skipped otherwise.</summary>
    [RealCopyFact]
    public async Task RealCopy_WhenGiven_IsCopiedAndVerified()
    {
        var real = Environment.GetEnvironmentVariable(RealSourceVariable)!;
        // A copy of the file alone: the -wal next to a copy taken at another moment would not match it.
        var copy = Path.Combine(Path.GetTempPath(), $"kasa-real-{Guid.NewGuid():N}.db");
        File.Copy(real, copy);
        try
        {
            var cs = await MsSql.NewDatabaseAsync("kasa_real");
            var report = await new DataMigrator(output.WriteLine).RunAsync(new MigrationOptions(copy, cs), CancellationToken.None);
            var md = report.ToMarkdown();
            output.WriteLine(md);
            if (Environment.GetEnvironmentVariable(RealReportVariable) is { Length: > 0 } reportPath) await File.WriteAllTextAsync(reportPath, md);
            report.Ok.Should().BeTrue();
        }
        finally
        {
            Delete(copy);
        }
    }
}

/// <summary>The test on a real kasa.db: needs Docker and <see cref="DataMigrationToolTests.RealSourceVariable"/> (an existing file).</summary>
public sealed class RealCopyFactAttribute : Xunit.FactAttribute
{
    public RealCopyFactAttribute()
    {
        var path = Environment.GetEnvironmentVariable(DataMigrationToolTests.RealSourceVariable);
        if (MsSql.SkipReason is { } why) Skip = "SQL Server tests skipped: " + why;
        else if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            Skip = $"{DataMigrationToolTests.RealSourceVariable} is not set to an existing kasa.db (opt-in test on a real copy)";
    }
}
