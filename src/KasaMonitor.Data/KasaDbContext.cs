using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KasaMonitor.Data;

public sealed class KasaDbContext : DbContext
{
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
            e.HasIndex(p => p.Role).IsUnique().HasFilter("\"Role\" = 1").HasDatabaseName("IX_Plugs_SingleMainMeter");
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
        });

        // Live stream: raw rows on change (retention LiveRawRetentionDays) and minute aggregates (kept forever).
        mb.Entity<LiveSampleEntity>(e =>
        {
            e.ToTable("LiveSamples");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedOnAdd();
            e.HasIndex(s => new { s.PlugId, s.Ts }).HasDatabaseName("IX_LiveSamples_PlugId_Ts");
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
        });

        // Heating (Viessmann boiler through the cloud API).
        mb.Entity<HeatingDeviceEntity>(e =>
        {
            e.ToTable("HeatingDevices");
            e.HasKey(d => d.Id);
            e.HasIndex(d => new { d.Provider, d.InstallationId, d.GatewaySerial, d.DeviceId }).IsUnique();
        });

        mb.Entity<HeatingReadingEntity>(e =>
        {
            e.ToTable("HeatingReadings");
            e.HasKey(r => new { r.DeviceId, r.Ts });
        });

        mb.Entity<HeatingEnergyDailyEntity>(e =>
        {
            e.ToTable("HeatingEnergyDaily");
            e.HasKey(d => new { d.DeviceId, d.Day });
        });

        mb.Entity<HeatingEnergyPeriodEntity>(e =>
        {
            e.ToTable("HeatingEnergyPeriods");
            e.HasKey(p => new { p.DeviceId, p.Kind, p.Purpose, p.Period });
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
        });

        mb.Entity<ApplianceSignatureEntity>(e =>
        {
            e.ToTable("ApplianceSignatures");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
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
        });

        mb.Entity<MieleStateSnapshotEntity>(e =>
        {
            e.ToTable("MieleStateSnapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.DeviceId, x.Ts });
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
        });

        // Climate (temperature / humidity per room): sensors of every source and their readings (on change, 30-min heartbeat).
        mb.Entity<ClimateSensorEntity>(e =>
        {
            e.ToTable("ClimateSensors");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
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
        });

        // Per-entity preferences of the generated device page (0.11): one row per entity key the user changed.
        mb.Entity<EntityPrefEntity>(e =>
        {
            e.ToTable("EntityPrefs");
            e.HasKey(x => x.Key);
            // Bounded strings (R10, round 2): the key fits an index on any provider (450 characters).
            e.Property(x => x.Key).HasMaxLength(450);
            e.Property(x => x.NameOverride).HasMaxLength(200);
            e.Property(x => x.ClassOverride).HasMaxLength(32);
            e.Property(x => x.UnitOverride).HasMaxLength(32);
        });

        // Generic devices of self-describing integrations (0.11, MQTT Discovery): devices, entities, raw values (on change,
        // 90 days by default) and hourly rollups (kept). Integer keys (R6); the two value tables are WITHOUT ROWID in the
        // migration (EF Core cannot declare it).
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
