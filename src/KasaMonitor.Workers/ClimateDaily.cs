using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KasaMonitor.Workers;

/// <summary>
/// <see cref="IClimateDailyStats"/> from the stored days (<c>ClimateDaily</c>, 0.9.2): the finished days come from the
/// table; a day not stored yet (the fill has not reached it) and today are computed from the raw readings
/// (<see cref="RawClimateDailyStats"/>), so the answer is always complete. Only days with a value are returned.
/// </summary>
public sealed class ClimateDailyStore : IClimateDailyStats
{
    private readonly IServiceScopeFactory _scopes;
    private readonly RawClimateDailyStats _raw;
    private readonly Func<DateTimeOffset> _clock;

    public ClimateDailyStore(IServiceScopeFactory scopes, Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _raw = new RawClimateDailyStats(scopes);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ClimateDayStats>> GetAsync(IReadOnlyCollection<Guid> sensorIds, DateOnly from, DateOnly to, TimeZoneInfo tz, CancellationToken ct)
    {
        if (sensorIds.Count == 0 || to < from) return Array.Empty<ClimateDayStats>();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock(), tz).DateTime);
        var result = new List<ClimateDayStats>();
        var lastFinished = to < today ? to : today.AddDays(-1);
        if (from <= lastFinished)
        {
            IReadOnlyList<ClimateDayStats> stored;
            using (var scope = _scopes.CreateScope())
                stored = await scope.ServiceProvider.GetRequiredService<IClimateRepository>().ListDailyAsync(sensorIds, from, lastFinished, ct);
            result.AddRange(stored.Where(d => d.Stats.AvgC is not null));
            // Days the fill has not stored yet (yesterday right after midnight, the days before a sensor's first reading): one
            // raw read per run of consecutive missing days of a sensor, never over the stored days between them.
            var have = stored.Select(d => (d.SensorId, d.Day)).ToHashSet();
            foreach (var id in sensorIds.Distinct())
                foreach (var (a, b) in Runs(Days(from, lastFinished).Where(d => !have.Contains((id, d)))))
                    result.AddRange(await _raw.GetAsync(new[] { id }, a, b, tz, ct));
        }
        if (to >= today && from <= today)
            result.AddRange(await _raw.GetAsync(sensorIds, today, today, tz, ct));
        return result.OrderBy(d => d.SensorId).ThenBy(d => d.Day).ToList();
    }

    private static IEnumerable<DateOnly> Days(DateOnly from, DateOnly to)
    {
        for (var d = from; d <= to; d = d.AddDays(1)) yield return d;
    }

    /// <summary>Consecutive days grouped into (first, last) runs.</summary>
    public static IEnumerable<(DateOnly First, DateOnly Last)> Runs(IEnumerable<DateOnly> days)
    {
        DateOnly? first = null, last = null;
        foreach (var d in days.OrderBy(x => x))
        {
            if (last is { } l && d == l.AddDays(1)) { last = d; continue; }
            if (first is { } f) yield return (f, last!.Value);
            first = last = d;
        }
        if (first is { } f2) yield return (f2, last!.Value);
    }
}

/// <summary>
/// Fills <c>ClimateDaily</c>: at startup every finished local day missing per sensor (from its first reading, or from the
/// day after its newest stored day) up to yesterday, then again shortly after each local midnight (and at least hourly), in
/// chunks of <see cref="ChunkDays"/> days from the raw readings. Days without a value are stored as empty rows. Every fill
/// also recomputes the last <see cref="RecomputeDays"/> finished days (the upsert is idempotent): readings that arrive late
/// (the boiler's source catches up from a cursor after a failure) still reach their day.
/// </summary>
public sealed class ClimateDailyWorker : BackgroundService
{
    public const int ChunkDays = 31;
    /// <summary>Finished days recomputed at every fill, for readings stored late.</summary>
    public const int RecomputeDays = 3;
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    /// <summary>After midnight: past the boiler source's safety lag (5 min) and its interval, so yesterday's last readings are in.</summary>
    public static readonly TimeSpan AfterMidnight = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeZoneInfo _tz;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;

    public ClimateDailyWorker(IServiceScopeFactory scopes, TimeZoneInfo? timeZone = null, ILogger<ClimateDailyWorker>? log = null, Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _tz = timeZone ?? TimeZoneInfo.Local;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>
    /// Stores every missing finished day of every sensor and recomputes the last <see cref="RecomputeDays"/>; returns the number
    /// of rows written (new and recomputed).
    /// </summary>
    public async Task<int> FillAsync(CancellationToken ct)
    {
        var now = _clock();
        var yesterday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _tz).DateTime).AddDays(-1);
        IReadOnlyDictionary<Guid, DateTimeOffset> first;
        IReadOnlyDictionary<Guid, DateOnly> last;
        using (var scope = _scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IClimateRepository>();
            first = await repo.FirstReadingsAsync(ct);
            last = await repo.LastDailyDaysAsync(ct);
        }
        var written = 0;
        var added = 0;
        foreach (var (sensor, firstTs) in first)
        {
            var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(firstTs, _tz).DateTime);
            var from = last.TryGetValue(sensor, out var l) ? l.AddDays(1) : firstDay;
            // Recompute the last finished days too (late readings), never before the sensor's first reading.
            var recompute = yesterday.AddDays(-(RecomputeDays - 1));
            if (recompute < from) from = recompute < firstDay ? firstDay : recompute;
            for (var start = from; start <= yesterday; start = start.AddDays(ChunkDays))
            {
                var end = start.AddDays(ChunkDays - 1) < yesterday ? start.AddDays(ChunkDays - 1) : yesterday;
                written += await FillChunkAsync(sensor, start, end, ct);
                var newFrom = last.TryGetValue(sensor, out var stored) ? stored.AddDays(1) : start;
                if (end >= newFrom) added += end.DayNumber - (start > newFrom ? start : newFrom).DayNumber + 1;
            }
        }
        if (added > 0) _log.LogInformation("Climate daily stats: {Rows} new days stored (up to {Day})", added, yesterday);
        else _log.LogDebug("Climate daily stats: {Rows} recent days recomputed", written);
        return written;
    }

    private async Task<int> FillChunkAsync(Guid sensor, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var days = ReportPeriod.Between(from, to).Buckets(_tz);
        IReadOnlyDictionary<Guid, IReadOnlyList<ClimateReading>> raw;
        using (var scope = _scopes.CreateScope())
            raw = await scope.ServiceProvider.GetRequiredService<IClimateRepository>()
                .ListReadingsAsync(new[] { sensor }, days[0].StartUtc - ClimateAggregation.MaxHold, days[^1].EndUtc, ct);
        var points = raw.TryGetValue(sensor, out var r) ? r.Select(x => new ClimatePoint(x.TsUtc, x.TemperatureC, x.HumidityPercent)).ToList() : new List<ClimatePoint>();
        var stats = ClimateWindows.Stats(points, days.Select(b => (b.StartUtc, b.EndUtc)).ToList());
        var rows = days.Select((b, i) => new ClimateDayStats(sensor, b.Day, stats[i])).ToList();
        using (var scope = _scopes.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IClimateRepository>().UpsertDailyAsync(rows, ct);
        return rows.Count;
    }

    /// <summary>How long to wait before the next fill: until just after the next local midnight, at most <see cref="MaxWait"/>.</summary>
    public TimeSpan NextWait(DateTimeOffset now)
    {
        var tomorrow = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _tz).DateTime).AddDays(1);
        var wait = ReportPeriod.Containing(ReportGranularity.Day, tomorrow).StartUtc(_tz) + AfterMidnight - now;
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait < MaxWait ? wait : MaxWait;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FillAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Climate daily stats could not be filled; next try in up to {Wait}", MaxWait);
            }
            try { await Task.Delay(NextWait(_clock()), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
