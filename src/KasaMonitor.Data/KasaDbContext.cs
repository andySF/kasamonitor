using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KasaMonitor.Data;

public sealed class KasaDbContext : DbContext
{
    /// <summary>Length of the day keys „yyyy-MM-dd” (DailyEnergy.Date, HeatingEnergyDaily.Day, ClimateDaily.Day).</summary>
    public const int DayKeyLength = 10;
    /// <summary>Short codes (protocol, source, kind, purpose, period, end reason …).</summary>
    public const int CodeLength = 32;
    /// <summary>Identifiers given by devices and clouds (MAC, serials, installation and device ids, Miele fabrication number).</summary>
    public const int IdentifierLength = 64;
    /// <summary>Models, firmware versions, setting keys and other technical labels.</summary>
    public const int LabelLength = 128;
    /// <summary>Names a person reads or types (alias, appliance, room, program texts).</summary>
    public const int NameLength = 200;
    /// <summary>External ids of climate sensors (Govee device ids, Shelly ids, Open-Meteo coordinates).</summary>
    public const int ExternalIdLength = 256;
    /// <summary>Entity keys of EntityPrefs (device key + "/" + slug): 400 characters = 800 bytes, under the 900-byte clustered key.</summary>
    public const int EntityPrefKeyLength = 400;

    public DbSet<PlugEntity> Plugs => Set<PlugEntity>();
    public DbSet<SampleEntity> Samples => Set<SampleEntity>();
    public DbSet<OneMinuteAggregateEntity> OneMinuteAggregates => Set<OneMinuteAggregateEntity>();
    public DbSet<OneHourAggregateEntity> OneHourAggregates => Set<OneHourAggregateEntity>();
    public DbSet<DailyEnergyEntity> DailyEnergies => Set<DailyEnergyEntity>();
    public DbSet<MonthlyEnergyEntity> MonthlyEnergies => Set<MonthlyEnergyEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<LiveSampleEntity> LiveSamples => Set<LiveSampleEntity>();
    public DbSet<LiveMinuteAggregateEntity> LiveMinuteAggregates => Set<LiveMinuteAggregateEntity>();
    public DbSet<RelayCommandEntity> RelayCommands => Set<RelayCommandEntity>();
    public DbSet<HeatingDeviceEntity> HeatingDevices => Set<HeatingDeviceEntity>();
    public DbSet<HeatingReadingEntity> HeatingReadings => Set<HeatingReadingEntity>();
    public DbSet<HeatingEnergyDailyEntity> HeatingEnergyDaily => Set<HeatingEnergyDailyEntity>();
    public DbSet<HeatingEnergyPeriodEntity> HeatingEnergyPeriods => Set<HeatingEnergyPeriodEntity>();
    public DbSet<HeatingRawFeaturesEntity> HeatingRawFeatures => Set<HeatingRawFeaturesEntity>();
    public DbSet<MeterEventEntity> MeterEvents => Set<MeterEventEntity>();
    public DbSet<ApplianceSignatureEntity> ApplianceSignatures => Set<ApplianceSignatureEntity>();
    public DbSet<ApplianceRunEntity> ApplianceRuns => Set<ApplianceRunEntity>();
    public DbSet<MieleDeviceEntity> MieleDevices => Set<MieleDeviceEntity>();
    public DbSet<MieleStateSnapshotEntity> MieleStateSnapshots => Set<MieleStateSnapshotEntity>();
    public DbSet<MieleCycleEntity> MieleCycles => Set<MieleCycleEntity>();
    public DbSet<ClimateSensorEntity> ClimateSensors => Set<ClimateSensorEntity>();
    public DbSet<ClimateReadingEntity> ClimateReadings => Set<ClimateReadingEntity>();
    public DbSet<ClimateDailyEntity> ClimateDaily => Set<ClimateDailyEntity>();
    public DbSet<EntityPrefEntity> EntityPrefs => Set<EntityPrefEntity>();
    public DbSet<GenericDeviceEntity> GenericDevices => Set<GenericDeviceEntity>();
    public DbSet<GenericEntityEntity> GenericEntities => Set<GenericEntityEntity>();
    public DbSet<GenericReadingEntity> GenericReadings => Set<GenericReadingEntity>();
    public DbSet<GenericHourlyEntity> GenericHourly => Set<GenericHourlyEntity>();

    public KasaDbContext(DbContextOptions<KasaDbContext> options) : base(options) { }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareForStore();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareForStore();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// What SQL Server would refuse, made storable or refused with a clear message before the batch is sent (one refused
    /// value fails the whole SaveChanges, and a worker would fail again at every cycle):
    /// <list type="bullet">
    /// <item>a nullable <c>double</c> that is NaN or ±Infinity becomes NULL (<see cref="DbValues.Finite"/>; SQLite stored them);</item>
    /// <item>a text longer than its <c>HasMaxLength</c> is cut to it (names and texts from the UI and the clouds);</item>
    /// <item>a text of a key or an index longer than its length throws: cutting an identifier could merge two of them.</item>
    /// </list>
    /// </summary>
    private void PrepareForStore()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            foreach (var prop in entry.Properties)
            {
                var meta = prop.Metadata;
                if (meta.ClrType == typeof(double?))
                {
                    if (prop.CurrentValue is double d && !double.IsFinite(d)) prop.CurrentValue = null;
                }
                else if (meta.ClrType == typeof(string) && meta.GetMaxLength() is { } max && prop.CurrentValue is string text && text.Length > max)
                {
                    if (meta.IsKey() || meta.IsIndex() || meta.IsForeignKey())
                        throw new InvalidOperationException(
                            $"{entry.Metadata.GetTableName()}.{meta.Name}: a value of {text.Length} characters does not fit its {max} (an identifier is never cut).");
                    prop.CurrentValue = Cut(text, max);
                }
            }
        }
    }

    /// <summary>The first <paramref name="max"/> UTF-16 units, without splitting a surrogate pair.</summary>
    internal static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        var n = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;
        return text[..n];
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PlugEntity>(e =>
        {
            e.ToTable("Plugs");
            e.HasKey(p => p.Id);
            // Identity of a plug: MAC + measured channel (multi-channel Shelly meters). Existing rows migrate to channel 0.
            e.HasIndex(p => new { p.Mac, p.Channel }).IsUnique();
            e.Property(p => p.Protocol).HasDefaultValue("legacy");
            // At most one main meter (Role = 1) per installation, enforced by the database as well.
            // [Role] is understood by SQL Server and by SQLite (the in-memory test database).
            e.HasIndex(p => p.Role).IsUnique().HasFilter("[Role] = 1").HasDatabaseName("IX_Plugs_SingleMainMeter");
            // Explicit lengths: SQL Server cannot index nvarchar(max) and an unbounded key would become nvarchar(450).
            e.Property(p => p.Mac).HasMaxLength(IdentifierLength);
            e.Property(p => p.IpAddress).HasMaxLength(IdentifierLength);
            e.Property(p => p.Alias).HasMaxLength(NameLength);
            e.Property(p => p.Model).HasMaxLength(LabelLength);
            e.Property(p => p.HwVer).HasMaxLength(IdentifierLength);
            e.Property(p => p.SwVer).HasMaxLength(LabelLength);
            e.Property(p => p.OemId).HasMaxLength(LabelLength);
            e.Property(p => p.Protocol).HasMaxLength(CodeLength);
            e.Property(p => p.ApplianceName).HasMaxLength(NameLength);
            e.Property(p => p.ApplianceCategory).HasMaxLength(IdentifierLength);
        });

        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));

        mb.Entity<SampleEntity>(e =>
        {
            e.ToTable("Samples");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedOnAdd();
            e.Property(s => s.TimestampUtc).HasConversion(dateTimeOffsetConverter);
            e.HasIndex(s => new { s.PlugId, s.TimestampUtc })
                .IsDescending(false, true)
                .HasDatabaseName("idx_samples_plug_time");
        });

        mb.Entity<OneMinuteAggregateEntity>(e =>
        {
            e.ToTable("OneMinuteAggregates");
            e.HasKey(a => new { a.PlugId, a.BucketStartUtc });
            e.Property(a => a.BucketStartUtc).HasConversion(dateTimeOffsetConverter);
        });

        mb.Entity<OneHourAggregateEntity>(e =>
        {
            e.ToTable("OneHourAggregates");
            e.HasKey(a => new { a.PlugId, a.BucketStartUtc });
            e.Property(a => a.BucketStartUtc).HasConversion(dateTimeOffsetConverter);
        });

        mb.Entity<DailyEnergyEntity>(e =>
        {
            e.ToTable("DailyEnergy");
            e.HasKey(d => new { d.PlugId, d.Date });
            e.Property(d => d.Date).HasMaxLength(DayKeyLength);
        });

        mb.Entity<MonthlyEnergyEntity>(e =>
        {
            e.ToTable("MonthlyEnergy");
            e.HasKey(m => new { m.PlugId, m.Year, m.Month });
        });

        mb.Entity<SettingEntity>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(LabelLength);
        });

        // Live stream: raw rows on change (retention LiveRawRetentionDays) and minute aggregates (kept forever).
        mb.Entity<LiveSampleEntity>(e =>
        {
            e.ToTable("LiveSamples");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedOnAdd();
            // One row per plug and second (0.12.0): the natural key, so that a batch stored twice (a retried commit, a batch
            // put back in the queue) cannot duplicate rows. LiveRepository.SaveBatchAsync skips the keys already stored.
            e.HasIndex(s => new { s.PlugId, s.Ts }).IsUnique().HasDatabaseName("IX_LiveSamples_PlugId_Ts");
        });

        mb.Entity<LiveMinuteAggregateEntity>(e =>
        {
            e.ToTable("LiveMinuteAggregates");
            e.HasKey(a => new { a.PlugId, a.MinuteTs });
        });

        mb.Entity<RelayCommandEntity>(e =>
        {
            e.ToTable("RelayCommands");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedOnAdd();
            e.HasIndex(r => new { r.PlugId, r.Ts });
            e.Property(r => r.Source).HasMaxLength(CodeLength);
        });

        // Heating (Viessmann boiler through the cloud API).
        mb.Entity<HeatingDeviceEntity>(e =>
        {
            e.ToTable("HeatingDevices");
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.Provider, d.InstallationId, d.GatewaySerial, d.DeviceId }).IsUnique();
            e.Property(d => d.Provider).HasMaxLength(CodeLength);
            e.Property(d => d.InstallationId).HasMaxLength(IdentifierLength);
            e.Property(d => d.GatewaySerial).HasMaxLength(IdentifierLength);
            e.Property(d => d.DeviceId).HasMaxLength(IdentifierLength);
            e.Property(d => d.ModelId).HasMaxLength(LabelLength);
            e.Property(d => d.Name).HasMaxLength(NameLength);
        });

        mb.Entity<HeatingReadingEntity>(e =>
        {
            e.ToTable("HeatingReadings");
            e.HasKey(r => new { r.DeviceId, r.Ts });
            e.Property(r => r.OperatingMode).HasMaxLength(IdentifierLength);
            e.Property(r => r.ActiveProgram).HasMaxLength(IdentifierLength);
            e.Property(r => r.DhwMode).HasMaxLength(IdentifierLength);
            e.Property(r => r.StatusCode).HasMaxLength(IdentifierLength);
        });

        mb.Entity<HeatingEnergyDailyEntity>(e =>
        {
            e.ToTable("HeatingEnergyDaily");
            e.HasKey(d => new { d.DeviceId, d.Day });
            e.Property(d => d.Day).HasMaxLength(DayKeyLength);
            e.Property(d => d.Source).HasMaxLength(CodeLength);
        });

        mb.Entity<HeatingEnergyPeriodEntity>(e =>
        {
            e.ToTable("HeatingEnergyPeriods");
            e.HasKey(p => new { p.DeviceId, p.Kind, p.Purpose, p.Period });
            e.Property(p => p.Kind).HasMaxLength(CodeLength);
            e.Property(p => p.Purpose).HasMaxLength(CodeLength);
            e.Property(p => p.Period).HasMaxLength(CodeLength);
            e.Property(p => p.Unit).HasMaxLength(CodeLength);
        });

        mb.Entity<HeatingRawFeaturesEntity>(e =>
        {
            e.ToTable("HeatingRawFeatures");
            e.HasKey(r => r.DeviceId);
        });

        // Appliance detection (NILM): events of the main meter, signatures (one per appliance) and runs (on → off).
        mb.Entity<MeterEventEntity>(e =>
        {
            e.ToTable("MeterEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.PlugId, x.Ts }).IsUnique();
            e.HasIndex(x => x.Ts);
            // Per-signature aggregates over the window read (SignatureId, Ts) ranges only.
            e.HasIndex(x => new { x.SignatureId, x.Ts });
            e.Property(x => x.Label).HasMaxLength(NameLength);
        });

        mb.Entity<ApplianceSignatureEntity>(e =>
        {
            e.ToTable("ApplianceSignatures");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).HasMaxLength(NameLength);
            e.Property(x => x.Icon).HasMaxLength(IdentifierLength);
            e.Property(x => x.Category).HasMaxLength(IdentifierLength);
        });

        mb.Entity<ApplianceRunEntity>(e =>
        {
            e.ToTable("ApplianceRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.SignatureId);
            e.HasIndex(x => x.StartTs);
            e.HasIndex(x => x.EndTs);
        });

        // Miele appliances (3rd Party API): devices, state snapshots (on change) and program cycles.
        mb.Entity<MieleDeviceEntity>(e =>
        {
            e.ToTable("MieleDevices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(IdentifierLength);
            e.Property(x => x.TypeText).HasMaxLength(NameLength);
            e.Property(x => x.TechType).HasMaxLength(IdentifierLength);
            e.Property(x => x.DeviceName).HasMaxLength(NameLength);
            e.Property(x => x.Alias).HasMaxLength(NameLength);
        });

        mb.Entity<MieleStateSnapshotEntity>(e =>
        {
            e.ToTable("MieleStateSnapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.DeviceId, x.Ts });
            e.Property(x => x.DeviceId).HasMaxLength(IdentifierLength);
            e.Property(x => x.StatusText).HasMaxLength(NameLength);
            e.Property(x => x.ProgramTypeText).HasMaxLength(NameLength);
            e.Property(x => x.ProgramIdText).HasMaxLength(NameLength);
            e.Property(x => x.PhaseText).HasMaxLength(NameLength);
        });

        mb.Entity<MieleCycleEntity>(e =>
        {
            e.ToTable("MieleCycles");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.DeviceId, x.StartTs });
            e.HasIndex(x => x.EndTs);
            // A run of the API history is stored once per appliance (NULL for cycles the API has not reported).
            e.HasIndex(x => new { x.DeviceId, x.ApiStartTs }).IsUnique();
            e.Property(x => x.DeviceId).HasMaxLength(IdentifierLength);
            e.Property(x => x.Program).HasMaxLength(NameLength);
            e.Property(x => x.EndReason).HasMaxLength(CodeLength);
            e.Property(x => x.Source).HasMaxLength(CodeLength);
        });

        // Climate (temperature / humidity per room): sensors of every source and their readings (on change, 30-min heartbeat).
        mb.Entity<ClimateSensorEntity>(e =>
        {
            e.ToTable("ClimateSensors");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            e.Property(x => x.Source).HasMaxLength(CodeLength);
            e.Property(x => x.ExternalId).HasMaxLength(ExternalIdLength);
            e.Property(x => x.Name).HasMaxLength(NameLength);
            e.Property(x => x.Room).HasMaxLength(NameLength);
        });

        mb.Entity<ClimateReadingEntity>(e =>
        {
            e.ToTable("ClimateReadings");
            e.HasKey(x => new { x.SensorId, x.Ts });
            e.HasIndex(x => x.Ts);
        });

        // Daily climate stats per sensor (0.9.2, /history): filled from the readings for every finished day.
        mb.Entity<ClimateDailyEntity>(e =>
        {
            e.ToTable("ClimateDaily");
            e.HasKey(x => new { x.SensorId, x.Day });
            e.Property(x => x.Day).HasMaxLength(DayKeyLength);
        });

        // Per-entity preferences of the generated device page (0.11): one row per entity key the user changed.
        mb.Entity<EntityPrefEntity>(e =>
        {
            e.ToTable("EntityPrefs");
            e.HasKey(x => x.Key);
            // Bounded strings (R10, round 2): device key + "/" + slug; 400 characters = 800 bytes, under the 900-byte limit
            // of a SQL Server clustered key (and not the unbounded-key default nvarchar(450)).
            e.Property(x => x.Key).HasMaxLength(EntityPrefKeyLength);
            e.Property(x => x.NameOverride).HasMaxLength(200);
            e.Property(x => x.ClassOverride).HasMaxLength(32);
            e.Property(x => x.UnitOverride).HasMaxLength(32);
        });

        // Generic devices of self-describing integrations (0.11, MQTT Discovery): devices, entities, raw values (on change,
        // 90 days by default) and hourly rollups (kept). Integer keys (R6); on SQL Server the two value tables are clustered
        // on their composite primary key (EntityId, Ts|HourTs), the table itself is the key's index (SQLite needed WITHOUT ROWID).
        mb.Entity<GenericDeviceEntity>(e =>
        {
            e.ToTable("GenericDevices");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.Integration, x.ExternalId }).IsUnique();
            e.Property(x => x.Integration).HasMaxLength(32);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Room).HasMaxLength(100);
            e.Property(x => x.Manufacturer).HasMaxLength(200);
            e.Property(x => x.Model).HasMaxLength(200);
            e.Property(x => x.Firmware).HasMaxLength(200);
            e.Property(x => x.ViaDevice).HasMaxLength(200);
            e.Property(x => x.AutoCategory).HasMaxLength(32);
            e.Property(x => x.CategoryOverride).HasMaxLength(32);
        });
        mb.Entity<GenericEntityEntity>(e =>
        {
            e.ToTable("GenericEntities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.Integration, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.DeviceId);
            e.Property(x => x.Integration).HasMaxLength(32);
            e.Property(x => x.ExternalId).HasMaxLength(128);
            e.Property(x => x.Component).HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.DataType).HasMaxLength(32);
            e.Property(x => x.Class).HasMaxLength(32);
            e.Property(x => x.ClassRaw).HasMaxLength(64);
            e.Property(x => x.Unit).HasMaxLength(32);
            e.Property(x => x.StateClass).HasMaxLength(32);
            e.Property(x => x.Category).HasMaxLength(32);
            e.Property(x => x.Problem).HasMaxLength(500);
            e.Property(x => x.LastText).HasMaxLength(255);
        });
        mb.Entity<GenericReadingEntity>(e =>
        {
            e.ToTable("GenericReadings");
            e.HasKey(x => new { x.EntityId, x.Ts });
            e.Property(x => x.Text).HasMaxLength(255);
        });
        mb.Entity<GenericHourlyEntity>(e =>
        {
            e.ToTable("GenericHourly");
            e.HasKey(x => new { x.EntityId, x.HourTs });
        });
    }
}
