using System.Diagnostics;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// The power aggregates and the retention of the raw readings. Every <see cref="CycleInterval"/>: the raw readings
/// (Samples) since the previous cycle are rolled into minute rows (OneMinuteAggregates) and those minutes into hour rows
/// (OneHourAggregates, 0.14.0); once per UTC day also the whole of yesterday, then the retention of Samples and LiveSamples.
/// The first cycle after a start rolls the last <see cref="CatchUpDays"/> days (a restart after a stop) and, once, every
/// past day that has minutes but no hours (the hourly backfill: idempotent, one plug-day per write). A stored minute or hour
/// is never replaced by one built from less data (<see cref="PowerHistory.Replaces"/>) and an aggregate is never deleted,
/// so the readings deleted by the retention cannot take the minutes and hours of their days with them.
/// </summary>
public sealed class RollupWorker : BackgroundService
{
    /// <summary>How often the aggregates are brought up to date (the „Putere” chart of today lags at most this much).</summary>
    public static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(15);

    /// <summary>Re-read before the previous cycle's time: readings written late still reach their minute.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>The days the first cycle after a start rolls again (from the raw readings, which the retention keeps for months).</summary>
    public const int CatchUpDays = 7;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RollupWorker> _log;
    private readonly ReadingsRetentionService? _retention;
    private readonly IKasaSettingsService? _settings;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _lastCycleUtc;
    private bool _backfilled;

    /// <param name="retention">Optional daily purge of raw readings by the ReadingsRetentionMonths setting (0 = keep all).</param>
    public RollupWorker(IServiceScopeFactory scopeFactory, ILogger<RollupWorker> log,
        ReadingsRetentionService? retention = null, IKasaSettingsService? settings = null, Func<DateTimeOffset>? clock = null)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _retention = retention;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    private static DateTimeOffset DayStart(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static DateOnly UtcDay(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime);

    private static DateTimeOffset Floor(DateTimeOffset t, long ticks) => new(t.UtcTicks / ticks * ticks, TimeSpan.Zero);

    /// <summary>The minute rows of a whole UTC day from its raw readings.</summary>
    public Task<int> RollupSamplesToMinutesAsync(DateOnly day, CancellationToken ct) =>
        RollupSamplesToMinutesAsync(DayStart(day), DayStart(day.AddDays(1)), ct);

    /// <summary>
    /// The minute rows of [from, to) (from is taken back to its minute) from the raw readings with a power, plug by plug (a
    /// seek on idx_samples_plug_time). A new minute is added; a stored one is replaced only when it was built from at most as
    /// many readings; a minute without readings is left as it is. Returns the rows added or replaced.
    /// </summary>
    public async Task<int> RollupSamplesToMinutesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = Floor(fromUtc, TimeSpan.TicksPerMinute);
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
        var plugIds = await ctx.Plugs.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        var written = 0;
        foreach (var plugId in plugIds)
        {
            // Compare DateTimeOffset directly so the value converter handles the long-ticks column translation.
            var rows = await ctx.Samples.AsNoTracking()
                .Where(s => s.PlugId == plugId && s.PowerMw != null && s.TimestampUtc >= from && s.TimestampUtc < toUtc)
                .Select(s => new { s.TimestampUtc, s.PowerMw, s.Online })
                .ToListAsync(ct);
            if (rows.Count == 0) continue;
            var minutes = rows
                .GroupBy(s => s.TimestampUtc.UtcTicks / TimeSpan.TicksPerMinute)
                .Select(g => new OneMinuteAggregateEntity
                {
                    PlugId = plugId,
                    BucketStartUtc = new DateTimeOffset(g.Key * TimeSpan.TicksPerMinute, TimeSpan.Zero),
                    PAvgMw = (long)g.Average(s => s.PowerMw!.Value),
                    PMaxMw = g.Max(s => s.PowerMw!.Value),
                    PMinMw = g.Min(s => s.PowerMw!.Value),
                    SampleCount = g.Count(),
                    OnlineCount = g.Count(s => s.Online),
                })
                .ToList();
            var first = minutes.Min(m => m.BucketStartUtc);
            var last = minutes.Max(m => m.BucketStartUtc);
            ctx.ChangeTracker.Clear();
            var stored = (await ctx.OneMinuteAggregates
                    .Where(a => a.PlugId == plugId && a.BucketStartUtc >= first && a.BucketStartUtc <= last)
                    .ToListAsync(ct))
                .ToDictionary(a => a.BucketStartUtc.UtcTicks);
            foreach (var m in minutes)
            {
                if (!stored.TryGetValue(m.BucketStartUtc.UtcTicks, out var e))
                {
                    ctx.OneMinuteAggregates.Add(m);
                    written++;
                }
                else if (PowerHistory.Replaces(e.SampleCount, m.SampleCount)
                         && (e.PAvgMw, e.PMaxMw, e.PMinMw, e.SampleCount, e.OnlineCount) != (m.PAvgMw, m.PMaxMw, m.PMinMw, m.SampleCount, m.OnlineCount))
                {
                    (e.PAvgMw, e.PMaxMw, e.PMinMw, e.SampleCount, e.OnlineCount) = (m.PAvgMw, m.PMaxMw, m.PMinMw, m.SampleCount, m.OnlineCount);
                    written++;
                }
            }
            await ctx.SaveChangesAsync(ct);
        }
        return written;
    }

    /// <summary>The hour rows of a whole UTC day from its minute rows.</summary>
    public Task<int> RollupMinutesToHoursAsync(DateOnly day, CancellationToken ct) =>
        RollupMinutesToHoursAsync(DayStart(day), DayStart(day.AddDays(1)), ct);

    /// <summary>
    /// The hour rows of the hours touched by [from, to) (from is taken back to its hour) from the minute rows, plug by plug
    /// (<see cref="PowerHistory.HoursFromMinutes"/>); an hour without minutes is left as it is. Returns the rows written.
    /// </summary>
    public async Task<int> RollupMinutesToHoursAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = Floor(fromUtc, TimeSpan.TicksPerHour);
        var to = Floor(toUtc.AddTicks(TimeSpan.TicksPerHour - 1), TimeSpan.TicksPerHour);
        using var scope = _scopeFactory.CreateScope();
        var plugIds = await scope.ServiceProvider.GetRequiredService<KasaDbContext>().Plugs.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        var repo = scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>();
        var written = 0;
        foreach (var plugId in plugIds)
            written += await HoursOfPlugAsync(repo, plugId, from, to, ct);
        return written;
    }

    private static async Task<int> HoursOfPlugAsync(IPowerAggregateRepository repo, Guid plugId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var minutes = await repo.ListMinutesAsync(plugId, from, to, ct);
        return minutes.Count == 0 ? 0 : await repo.UpsertHoursAsync(plugId, PowerHistory.HoursFromMinutes(minutes), ct);
    }

    /// <summary>
    /// The one-time hourly backfill: for every plug, every UTC day between its first and its last minute row that has no
    /// hour row yet gets its hours (one read and one write per plug-day). Idempotent: a second run finds nothing to do.
    /// Returns the hour rows written.
    /// </summary>
    public async Task<int> BackfillHoursAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var scope = _scopeFactory.CreateScope();
        var plugIds = await scope.ServiceProvider.GetRequiredService<KasaDbContext>().Plugs.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        var repo = scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>();
        int written = 0, days = 0;
        foreach (var plugId in plugIds)
        {
            if (await repo.MinuteSpanAsync(plugId, ct) is not { } span) continue;
            var done = await repo.HourDaysAsync(plugId, ct);
            for (var d = UtcDay(span.First); d <= UtcDay(span.Last); d = d.AddDays(1))
            {
                if (done.Contains(d)) continue;
                var n = await HoursOfPlugAsync(repo, plugId, DayStart(d), DayStart(d.AddDays(1)), ct);
                if (n > 0) days++;
                written += n;
            }
        }
        if (written > 0)
            _log.LogInformation("Hourly power aggregates backfilled: {Hours} hours on {Days} plug-days in {Ms} ms", written, days, sw.ElapsedMilliseconds);
        return written;
    }

    /// <summary>
    /// One cycle at <paramref name="nowUtc"/>: the minutes and hours since the previous cycle (the first one: the last
    /// <see cref="CatchUpDays"/> days; a new UTC day: also the whole of yesterday), the retention once per UTC day, the
    /// hourly backfill once. Each part fails on its own (logged) without holding back the others.
    /// </summary>
    public async Task RunCycleAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var today = UtcDay(nowUtc);
        var newDay = _lastCycleUtc is not { } prev || UtcDay(prev) != today;
        var from = _lastCycleUtc is { } p ? p - Overlap : DayStart(today.AddDays(-CatchUpDays));
        if (newDay && from > DayStart(today.AddDays(-1))) from = DayStart(today.AddDays(-1));
        var rolled = false;
        try
        {
            // Day by day: one day of readings in memory at a time.
            for (var start = from; start < nowUtc; start = DayStart(UtcDay(start).AddDays(1)))
            {
                var end = DayStart(UtcDay(start).AddDays(1));
                await RollupSamplesToMinutesAsync(start, end < nowUtc ? end : nowUtc, ct);
            }
            await RollupMinutesToHoursAsync(from, nowUtc, ct);
            rolled = true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "Rollup cycle failed"); }
        if (newDay)
        {
            try { await ApplyRetentionAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "Readings retention failed"); }
            // On its own: a failure above must not skip the live-sample retention of the day (and the other way round).
            try { await ApplyLiveRetentionAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "Live sample retention failed"); }
        }
        // A failed roll is tried again from the same point at the next cycle.
        if (rolled) _lastCycleUtc = nowUtc;
        if (!_backfilled)
        {
            try
            {
                await BackfillHoursAsync(ct);
                _backfilled = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "Hourly power backfill failed"); }
        }
    }

    /// <summary>Raw readings are kept forever unless ReadingsRetentionMonths &gt; 0 (up to and including 0.2.1 they were cut at 30 days).</summary>
    public async Task<int> ApplyRetentionAsync(CancellationToken ct)
    {
        if (_retention is null || _settings is null) return 0;
        var months = (await _settings.GetAsync(ct)).ReadingsRetentionMonths;
        return await _retention.ApplyAsync(months, ct);
    }

    /// <summary>Raw live samples older than LiveRawRetentionDays (default 30, 0 = keep all); minute aggregates stay.</summary>
    public async Task<int> ApplyLiveRetentionAsync(CancellationToken ct)
    {
        if (_retention is null || _settings is null) return 0;
        var days = (await _settings.GetAsync(ct)).LiveRawRetentionDays;
        return await _retention.ApplyLiveRawAsync(days, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(_clock(), stoppingToken);
            try { await Task.Delay(CycleInterval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
