using System.Diagnostics;
using System.Globalization;
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
/// The power aggregates and the retention of the raw readings. Every <see cref="CycleInterval"/>, plug by plug: the raw
/// readings (Samples) from the plug's last stored minute (less <see cref="Overlap"/>; a plug without minutes: from its first
/// reading) are rolled into minute rows (OneMinuteAggregates) and those minutes into hour rows (OneHourAggregates, 0.14.0),
/// so a stop of any length is caught up at the next start; once per UTC day also the whole of yesterday and then — after a
/// roll without errors — the retention of Samples and LiveSamples. Once (marker <see cref="SettingKeys.PowerBackfillDoneUtc"/>)
/// the backfill fills the minutes of every past day that has readings but no minutes and the hours of every day that has
/// minutes but no hours. A stored minute or hour is never replaced by one built from less data
/// (<see cref="PowerHistory.Replaces"/>) and an aggregate is never deleted, so the readings deleted by the retention cannot
/// take the minutes and hours of their days with them. A plug that fails does not stop the others; it is retried with a
/// growing pause (up to <see cref="MaxBackoff"/>) and logged once until it recovers.
/// </summary>
public sealed class RollupWorker : BackgroundService
{
    /// <summary>How often the aggregates are brought up to date (the „Putere” chart of today lags at most this much).</summary>
    public static readonly TimeSpan CycleInterval = TimeSpan.FromMinutes(15);

    /// <summary>Re-read before the last stored minute: readings written late still reach their minute.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    /// <summary>A period that ended less than this ago may still get its last minutes (a cycle plus the overlap).</summary>
    public static readonly TimeSpan Settling = CycleInterval + Overlap;

    /// <summary>The longest pause before a failing plug (or the backfill) is tried again.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RollupWorker> _log;
    private readonly ReadingsRetentionService? _retention;
    private readonly IKasaSettingsService? _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly PowerAggregatesVersion? _version;
    private readonly Dictionary<string, (int Failures, DateTimeOffset NextTry)> _failing = new();
    private DateOnly? _lastFullDay;
    private DateOnly? _lastRetentionDay;
    private bool _backfillDone;

    /// <param name="retention">Optional daily purge of raw readings by the ReadingsRetentionMonths setting (0 = keep all).</param>
    /// <param name="version">Bumped when rows of a past period were written (the cache of the „Putere” chart).</param>
    public RollupWorker(IServiceScopeFactory scopeFactory, ILogger<RollupWorker> log,
        ReadingsRetentionService? retention = null, IKasaSettingsService? settings = null, Func<DateTimeOffset>? clock = null,
        PowerAggregatesVersion? version = null)
    {
        _scopeFactory = scopeFactory;
        _log = log;
        _retention = retention;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _version = version;
    }

    private static DateTimeOffset DayStart(DateOnly day) => new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static DateOnly UtcDay(DateTimeOffset t) => DateOnly.FromDateTime(t.UtcDateTime);

    private static DateTimeOffset Floor(DateTimeOffset t, long ticks) => new(t.UtcTicks / ticks * ticks, TimeSpan.Zero);

    private static DateTimeOffset? Min(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static async Task<List<Guid>> PlugIdsAsync(KasaDbContext ctx, CancellationToken ct) =>
        await ctx.Plugs.AsNoTracking().Select(p => p.Id).ToListAsync(ct);

    // ------------------------------------------------------------------ minutes from the raw readings

    /// <summary>The minute rows of a whole UTC day from its raw readings, every plug.</summary>
    public Task<int> RollupSamplesToMinutesAsync(DateOnly day, CancellationToken ct) =>
        RollupSamplesToMinutesAsync(DayStart(day), DayStart(day.AddDays(1)), ct);

    /// <summary>The minute rows of [from, to) from the raw readings, every plug (<see cref="MinutesOfPlugAsync"/>). Returns the rows written.</summary>
    public async Task<int> RollupSamplesToMinutesAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
        var written = 0;
        foreach (var plugId in await PlugIdsAsync(ctx, ct))
            written += (await MinutesOfPlugAsync(ctx, plugId, fromUtc, toUtc, ct)).Written;
        return written;
    }

    /// <summary>
    /// The minute rows of one plug in [from, to) (from is taken back to its minute) from its raw readings with a power (a seek
    /// on idx_samples_plug_time). A new minute is added; a stored one is replaced only when it was built from at most as many
    /// readings; a minute without readings is left as it is. Returns the rows written and the earliest of them.
    /// </summary>
    private static async Task<(int Written, DateTimeOffset? Earliest)> MinutesOfPlugAsync(KasaDbContext ctx, Guid plugId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = Floor(fromUtc, TimeSpan.TicksPerMinute);
        // Compare DateTimeOffset directly so the value converter handles the long-ticks column translation.
        var rows = await ctx.Samples.AsNoTracking()
            .Where(s => s.PlugId == plugId && s.PowerMw != null && s.TimestampUtc >= from && s.TimestampUtc < toUtc)
            .Select(s => new { s.TimestampUtc, s.PowerMw, s.Online })
            .ToListAsync(ct);
        if (rows.Count == 0) return (0, null);
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
        var written = 0;
        DateTimeOffset? earliest = null;
        foreach (var m in minutes)
        {
            if (!stored.TryGetValue(m.BucketStartUtc.UtcTicks, out var e))
                ctx.OneMinuteAggregates.Add(m);
            else if (PowerHistory.Replaces(e.SampleCount, m.SampleCount)
                     && (e.PAvgMw, e.PMaxMw, e.PMinMw, e.SampleCount, e.OnlineCount) != (m.PAvgMw, m.PMaxMw, m.PMinMw, m.SampleCount, m.OnlineCount))
                (e.PAvgMw, e.PMaxMw, e.PMinMw, e.SampleCount, e.OnlineCount) = (m.PAvgMw, m.PMaxMw, m.PMinMw, m.SampleCount, m.OnlineCount);
            else
                continue;
            written++;
            earliest = Min(earliest, m.BucketStartUtc);
        }
        await ctx.SaveChangesAsync(ct);
        return (written, earliest);
    }

    /// <summary>The first (or last) reading with a power of a plug in [from, to): a seek on idx_samples_plug_time.</summary>
    private static async Task<DateTimeOffset?> SampleAtAsync(KasaDbContext ctx, Guid plugId, DateTimeOffset from, DateTimeOffset to, bool last, CancellationToken ct)
    {
        var q = ctx.Samples.AsNoTracking().Where(s => s.PlugId == plugId && s.PowerMw != null && s.TimestampUtc >= from && s.TimestampUtc < to);
        q = last ? q.OrderByDescending(s => s.TimestampUtc) : q.OrderBy(s => s.TimestampUtc);
        return await q.Select(s => (DateTimeOffset?)s.TimestampUtc).FirstOrDefaultAsync(ct);
    }

    /// <summary>The first (or last) minute row of a plug in [from, to): a seek on the key.</summary>
    private static async Task<DateTimeOffset?> MinuteAtAsync(KasaDbContext ctx, Guid plugId, DateTimeOffset from, DateTimeOffset to, bool last, CancellationToken ct)
    {
        var q = ctx.OneMinuteAggregates.AsNoTracking().Where(a => a.PlugId == plugId && a.BucketStartUtc >= from && a.BucketStartUtc < to);
        q = last ? q.OrderByDescending(a => a.BucketStartUtc) : q.OrderBy(a => a.BucketStartUtc);
        return await q.Select(a => (DateTimeOffset?)a.BucketStartUtc).FirstOrDefaultAsync(ct);
    }

    // ------------------------------------------------------------------ hours from the minutes

    /// <summary>The hour rows of a whole UTC day from its minute rows, every plug.</summary>
    public Task<int> RollupMinutesToHoursAsync(DateOnly day, CancellationToken ct) =>
        RollupMinutesToHoursAsync(DayStart(day), DayStart(day.AddDays(1)), ct);

    /// <summary>The hour rows of the hours touched by [from, to), every plug (<see cref="HoursOfPlugAsync"/>). Returns the rows written.</summary>
    public async Task<int> RollupMinutesToHoursAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plugIds = await PlugIdsAsync(scope.ServiceProvider.GetRequiredService<KasaDbContext>(), ct);
        var repo = scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>();
        var written = 0;
        foreach (var plugId in plugIds)
            written += await HoursOfPlugAsync(repo, plugId, fromUtc, toUtc, ct);
        return written;
    }

    /// <summary>
    /// The hour rows of one plug for the hours touched by [from, to) (from back to its hour, to up to the next one) from its
    /// minute rows (<see cref="PowerHistory.HoursFromMinutes"/>); an hour without minutes is left as it is.
    /// </summary>
    private static async Task<int> HoursOfPlugAsync(IPowerAggregateRepository repo, Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var from = Floor(fromUtc, TimeSpan.TicksPerHour);
        var to = Floor(toUtc.AddTicks(TimeSpan.TicksPerHour - 1), TimeSpan.TicksPerHour);
        var minutes = await repo.ListMinutesAsync(plugId, from, to, ct);
        return minutes.Count == 0 ? 0 : await repo.UpsertHoursAsync(plugId, PowerHistory.HoursFromMinutes(minutes), ct);
    }

    // ------------------------------------------------------------------ catch-up, backfill

    /// <summary>
    /// One plug up to now: its readings from its last stored minute less <see cref="Overlap"/> (none stored: from its first
    /// reading; <paramref name="alsoFrom"/> earlier: from there) into minutes, one UTC day of readings at a time and jumping
    /// over days without readings, then the hours of what was rolled. Returns the minutes written and the earliest of them.
    /// </summary>
    public async Task<(int Written, DateTimeOffset? Earliest)> CatchUpPlugAsync(Guid plugId, DateTimeOffset nowUtc, DateTimeOffset? alsoFrom, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>();
        var last = (await repo.MinuteSpanAsync(plugId, ct))?.Last;
        var cursor = last is { } l ? l - Overlap : DateTimeOffset.MinValue;
        if (alsoFrom is { } f && f < cursor) cursor = f;
        int written = 0;
        DateTimeOffset? earliest = null, firstRolled = null;
        while (cursor < nowUtc && await SampleAtAsync(ctx, plugId, cursor, nowUtc, last: false, ct) is { } next)
        {
            var start = Floor(next, TimeSpan.TicksPerMinute);
            var end = DayStart(UtcDay(next).AddDays(1));
            if (end > nowUtc) end = nowUtc;
            var (w, e) = await MinutesOfPlugAsync(ctx, plugId, start, end, ct);
            written += w;
            earliest = Min(earliest, e);
            firstRolled ??= start;
            cursor = end;
        }
        if (firstRolled is { } from) await HoursOfPlugAsync(repo, plugId, from, nowUtc, ct);
        return (written, earliest);
    }

    /// <summary>
    /// The one-time backfill, skipped once <see cref="SettingKeys.PowerBackfillDoneUtc"/> is stored: for every plug (1) every
    /// past UTC day that has readings but no minutes, or readings before its first or after its last minute, gets its minutes
    /// and then explicitly its hours; (2) every day between its first and its last minute that has no hour row gets its hours.
    /// A few seeks per plug-day (idx_samples_plug_time, the keys); days without readings are jumped over. A plug that fails is
    /// logged and the marker is not written (the backfill runs again at a later cycle). Returns the rows written.
    /// </summary>
    public async Task<int> BackfillAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        if (await settings.GetAsync(SettingKeys.PowerBackfillDoneUtc, ct) is not null) return 0;
        var sw = Stopwatch.StartNew();
        var plugIds = await PlugIdsAsync(scope.ServiceProvider.GetRequiredService<KasaDbContext>(), ct);
        int minutes = 0, hours = 0, minuteDays = 0, hourDays = 0;
        var failed = false;
        foreach (var plugId in plugIds)
        {
            try
            {
                var (m, md) = await BackfillMinutesOfPlugAsync(plugId, DayStart(UtcDay(nowUtc)), ct);
                var (h, hd) = await BackfillHoursOfPlugAsync(plugId, ct);
                (minutes, minuteDays, hours, hourDays) = (minutes + m, minuteDays + md, hours + h, hourDays + hd);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed = true;
                _log.LogError(ex, "Power backfill failed for plug {Plug}; the others go on, it is tried again later", plugId);
            }
        }
        if (minutes + hours > 0)
            _log.LogInformation("Power aggregates backfilled: {Minutes} minutes on {MinuteDays} plug-days, {Hours} hours on {HourDays} plug-days in {Ms} ms",
                minutes, minuteDays, hours, hourDays, sw.ElapsedMilliseconds);
        if (failed) throw new InvalidOperationException("The power backfill did not finish for every plug.");
        await settings.SetAsync(SettingKeys.PowerBackfillDoneUtc, nowUtc.ToString("O", CultureInfo.InvariantCulture), ct);
        return minutes + hours;
    }

    private async Task<(int Rows, int Days)> BackfillMinutesOfPlugAsync(Guid plugId, DateTimeOffset beforeUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>();
        int rows = 0, days = 0;
        var cursor = DateTimeOffset.MinValue;
        while (cursor < beforeUtc && await SampleAtAsync(ctx, plugId, cursor, beforeUtc, last: false, ct) is { } first)
        {
            var dayStart = DayStart(UtcDay(first));
            var dayEnd = dayStart.AddDays(1);
            var lastSample = (await SampleAtAsync(ctx, plugId, dayStart, dayEnd, last: true, ct))!.Value;
            var firstMinute = await MinuteAtAsync(ctx, plugId, dayStart, dayEnd, last: false, ct);
            var lastMinute = await MinuteAtAsync(ctx, plugId, dayStart, dayEnd, last: true, ct);
            if (firstMinute is null || Floor(first, TimeSpan.TicksPerMinute) < firstMinute || Floor(lastSample, TimeSpan.TicksPerMinute) > lastMinute)
            {
                var (w, _) = await MinutesOfPlugAsync(ctx, plugId, dayStart, dayEnd, ct);
                rows += w + await HoursOfPlugAsync(repo, plugId, dayStart, dayEnd, ct);
                days++;
            }
            cursor = dayEnd;
        }
        return (rows, days);
    }

    private async Task<(int Rows, int Days)> BackfillHoursOfPlugAsync(Guid plugId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>();
        if (await repo.MinuteSpanAsync(plugId, ct) is not { } span) return (0, 0);
        var done = await repo.HourDaysAsync(plugId, ct);
        int rows = 0, days = 0;
        for (var d = UtcDay(span.First); d <= UtcDay(span.Last); d = d.AddDays(1))
        {
            if (done.Contains(d)) continue;
            var n = await HoursOfPlugAsync(repo, plugId, DayStart(d), DayStart(d.AddDays(1)), ct);
            if (n > 0) days++;
            rows += n;
        }
        return (rows, days);
    }

    // ------------------------------------------------------------------ the cycle

    private bool Due(string key, DateTimeOffset now) => !_failing.TryGetValue(key, out var f) || now >= f.NextTry;

    /// <summary>A failure: logged at Error the first time, at Debug while it repeats; the next try after 1, 2, 4 … cycles (at most <see cref="MaxBackoff"/>).</summary>
    private void Failed(string key, DateTimeOffset now, Exception ex)
    {
        var failures = _failing.TryGetValue(key, out var f) ? f.Failures + 1 : 1;
        var pause = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, CycleInterval.Ticks * (1L << Math.Min(failures - 1, 16))));
        _failing[key] = (failures, now + pause);
        if (failures == 1) _log.LogError(ex, "Power rollup of {What} failed; tried again with a growing pause (logged again when it recovers)", key);
        else _log.LogDebug(ex, "Power rollup of {What} failed again ({Failures} times)", key, failures);
    }

    private void Succeeded(string key)
    {
        if (_failing.Remove(key, out var f)) _log.LogInformation("Power rollup of {What} works again after {Failures} failures", key, f.Failures);
    }

    /// <summary>
    /// One cycle at <paramref name="nowUtc"/>: every plug caught up (<see cref="CatchUpPlugAsync"/>; a new UTC day: also the
    /// whole of yesterday), on its own; the retention once per UTC day after a roll without errors; the backfill until it is
    /// done. Rows written for a period older than <see cref="Settling"/> bump <see cref="PowerAggregatesVersion"/>.
    /// </summary>
    public async Task RunCycleAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        var today = UtcDay(nowUtc);
        var newDay = _lastFullDay != today;
        List<Guid> plugIds;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            plugIds = await PlugIdsAsync(scope.ServiceProvider.GetRequiredService<KasaDbContext>(), ct);
            Succeeded("the plug list");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Failed("the plug list", nowUtc, ex);
            return;
        }
        var failed = false;
        DateTimeOffset? earliest = null;
        foreach (var plugId in plugIds)
        {
            var key = "plug " + plugId;
            if (!Due(key, nowUtc)) { failed = true; continue; }
            try
            {
                var (_, e) = await CatchUpPlugAsync(plugId, nowUtc, newDay ? DayStart(today.AddDays(-1)) : null, ct);
                earliest = Min(earliest, e);
                Succeeded(key);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failed = true;
                Failed(key, nowUtc, ex);
            }
        }
        if (earliest is { } old && old < nowUtc - Settling) _version?.Bump();
        if (newDay && !failed) _lastFullDay = today;
        if (!failed && _lastRetentionDay != today)
        {
            _lastRetentionDay = today;
            try { await ApplyRetentionAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "Readings retention failed"); }
            // On its own: a failure above must not skip the live-sample retention of the day (and the other way round).
            try { await ApplyLiveRetentionAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogError(ex, "Live sample retention failed"); }
        }
        if (!_backfillDone && Due("the backfill", nowUtc))
        {
            try
            {
                if (await BackfillAsync(nowUtc, ct) > 0) _version?.Bump();
                _backfillDone = true;
                Succeeded("the backfill");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { Failed("the backfill", nowUtc, ex); }
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
