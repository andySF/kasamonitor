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
    }
}
