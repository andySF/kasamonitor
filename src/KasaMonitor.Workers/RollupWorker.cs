using KasaMonitor.Core.Abstractions;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class RollupWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RollupWorker> _log;
    private readonly ReadingsRetentionService? _retention;
    private readonly IKasaSettingsService? _settings;

    /// <param name="retention">Optional daily purge of raw readings by the ReadingsRetentionMonths setting (0 = keep all).</param>
    public RollupWorker(IServiceScopeFactory scopeFactory, ILogger<RollupWorker> log,
        ReadingsRetentionService? retention = null, IKasaSettingsService? settings = null)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _retention = retention;
        _settings = settings;
    }

    public async Task RollupSamplesToMinutesAsync(DateOnly day, CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var ticksPerMinute = TimeSpan.TicksPerMinute;

        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
        // Pull samples for the day; compare DateTimeOffset directly so the value converter
        // handles the long-ticks column translation (s.TimestampUtc.UtcTicks is not EF-translatable).
        var rows = await ctx.Samples.AsNoTracking()
            .Where(s => s.PowerMw != null
                        && s.TimestampUtc >= dayStart
                        && s.TimestampUtc <  dayEnd)
            .ToListAsync(ct);

        var grouped = rows
            .GroupBy(s => new { s.PlugId, BucketTicks = s.TimestampUtc.UtcTicks / ticksPerMinute * ticksPerMinute })
            .Select(g => new OneMinuteAggregateEntity
            {
                PlugId = g.Key.PlugId,
                BucketStartUtc = new DateTimeOffset(g.Key.BucketTicks, TimeSpan.Zero),
                PAvgMw = (long)g.Average(s => s.PowerMw!.Value),
                PMaxMw = g.Max(s => s.PowerMw!.Value),
                PMinMw = g.Min(s => s.PowerMw!.Value),
                SampleCount = g.Count(),
                OnlineCount = g.Count(s => s.Online)
            })
            .ToList();

        foreach (var agg in grouped)
        {
            var existing = await ctx.OneMinuteAggregates.FindAsync(new object?[] { agg.PlugId, agg.BucketStartUtc }, ct);
            if (existing is null)
            {
                ctx.OneMinuteAggregates.Add(agg);
            }
            else
            {
                existing.PAvgMw = agg.PAvgMw;
                existing.PMaxMw = agg.PMaxMw;
                existing.PMinMw = agg.PMinMw;
                existing.SampleCount = agg.SampleCount;
                existing.OnlineCount = agg.OnlineCount;
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    /// <summary>Raw readings are kept forever unless ReadingsRetentionMonths &gt; 0 (up to and including 0.2.1 they were cut at 30 days).</summary>
    public async Task<int> ApplyRetentionAsync(CancellationToken ct)
    {
        if (_retention is null || _settings is null) return 0;
        var months = (await _settings.GetAsync(ct)).ReadingsRetentionMonths;
        return await _retention.ApplyAsync(months, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
                await RollupSamplesToMinutesAsync(yesterday, stoppingToken);
                await ApplyRetentionAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Rollup cycle failed"); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
