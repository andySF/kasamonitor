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

    public KasaDbContext(DbContextOptions<KasaDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PlugEntity>(e =>
        {
            e.ToTable("Plugs");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Mac).IsUnique();
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
    }
}
