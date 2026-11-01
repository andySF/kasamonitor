using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Protocol.Tapo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class HistorySyncWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Func<DateTime> _nowProvider;
    private readonly ILogger<HistorySyncWorker> _log;
    private readonly ITapoClient? _tapo;
    private readonly IKasaSettingsService? _settings;
    private readonly TimeZoneInfo _tz;
    private readonly IPlugDiscoveredChannel? _discovered;
    private readonly IShellyClient? _shelly;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    /// <summary>Days of Shelly history read at the first sync of a channel; later syncs start from the last known day.</summary>
    public const int ShellyHistoryDays = 60;

    public static readonly TimeSpan CycleInterval = TimeSpan.FromHours(6);
    /// <summary>Retry interval of the periodic cycle while the database has no plug with history (fresh install).</summary>
    public static readonly TimeSpan EmptyRetryInterval = TimeSpan.FromMinutes(1);

    /// <summary>Days of Tapo history fetched on every sync (upserted, so overlapping syncs are harmless).</summary>
    public const int TapoHistoryDays = 60;

    /// <param name="tapo">Optional: without it (or without <paramref name="settings"/>) only legacy plugs are synced.</param>
    /// <param name="timeZone">Time zone of the plugs' "day" (default: the process time zone, TZ in the container).</param>
    public HistorySyncWorker(
        IKasaClient kasa,
        IServiceScopeFactory scopeFactory,
        Func<DateTime> nowProvider,
        ILogger<HistorySyncWorker> log,
        ITapoClient? tapo = null,
        IKasaSettingsService? settings = null,
        TimeZoneInfo? timeZone = null,
        IPlugDiscoveredChannel? discovered = null,
        IShellyClient? shelly = null)
    {
        _shelly = shelly;
        _discovered = discovered;
        _kasa = kasa;
        _scopeFactory = scopeFactory;
        _nowProvider = nowProvider;
        _log = log;
        _tapo = tapo;
        _settings = settings;
        _tz = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>
    /// Legacy plugs (emeter daystat/monthstat), KLAP/AES plugs when a Tapo client is configured, and Shelly channels
    /// when a Shelly client is configured (device history for em1 meters, own readings for the others).
    /// </summary>
    public bool HasHistory(Plug p) =>
        PollingWorker.IsActive(p)
        && (p.Protocol == PlugProtocols.Legacy
            || (p.Protocol == PlugProtocols.ShellyRpc && _shelly is not null && _settings is not null)
            || (_tapo is not null && _settings is not null && _tapo.SupportsProtocol(p.Protocol)));

    public async Task SyncOneAsync(Guid plugId, CancellationToken ct)
    {
        // Periodic cycle and discovery-triggered syncs share the database: one plug at a time.
        await _syncGate.WaitAsync(ct);
        try { await SyncOneCoreAsync(plugId, ct); }
        finally { _syncGate.Release(); }
    }

    private async Task SyncOneCoreAsync(Guid plugId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plugRepo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        var historyRepo = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();

        var plug = (await plugRepo.ListAllAsync(ct)).FirstOrDefault(p => p.Id == plugId);
        if (plug is null) return;

        if (plug.Protocol == PlugProtocols.ShellyRpc)
        {
            await SyncShellyAsync(plug, historyRepo, scope.ServiceProvider.GetRequiredService<ISampleRepository>(), ct);
            return;
        }
        if (plug.Protocol != PlugProtocols.Legacy)
        {
            await SyncTapoAsync(plug, historyRepo, ct);
            return;
        }

        var now = _nowProvider();
        try
        {
            var monthsThis = await _kasa.GetMonthStatAsync(plug.IpAddress, now.Year, ct);
            var monthsPrev = await _kasa.GetMonthStatAsync(plug.IpAddress, now.Year - 1, ct);
            await historyRepo.UpsertMonthlyAsync(plugId, monthsThis, ct);
            await historyRepo.UpsertMonthlyAsync(plugId, monthsPrev, ct);

            var daysThis = await _kasa.GetDayStatAsync(plug.IpAddress, now.Year, now.Month, ct);
            var prevMonth = now.AddMonths(-1);
            var daysPrev = await _kasa.GetDayStatAsync(plug.IpAddress, prevMonth.Year, prevMonth.Month, ct);
            await historyRepo.UpsertDailyAsync(plugId, daysThis, ct);
            await historyRepo.UpsertDailyAsync(plugId, daysPrev, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "History sync failed for plug {Alias} ({Ip}, {PlugId})", plug.Alias, plug.IpAddress, plugId);
        }
    }

    /// <summary>
    /// Tapo: daily Wh from <c>get_energy_data</c> (interval 1440) for the last <see cref="TapoHistoryDays"/> days,
    /// one request per calendar month (first day of the month to the last day / today). If the device rejects a
    /// month-aligned request (error code), the quarter containing that month is requested instead, since some
    /// firmware only accept a quarter's first day as start for daily data. Values are upserted per plug and day.
    /// </summary>
    private async Task SyncTapoAsync(Plug plug, IHistoryRepository historyRepo, CancellationToken ct)
    {
        if (_tapo is null || _settings is null || !_tapo.SupportsProtocol(plug.Protocol)) return;
        var creds = (await _settings.GetAsync(ct)).TapoCredentials;
        if (creds is null)
        {
            _log.LogDebug("History sync skipped for {Alias}: no TP-Link credentials", plug.Alias);
            return;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(_nowProvider(), DateTimeKind.Utc), _tz));
        var windowStart = today.AddDays(-(TapoHistoryDays - 1));
        var done = new HashSet<DateOnly>();
        var total = 0;
        try
        {
            for (var month = new DateOnly(windowStart.Year, windowStart.Month, 1); month <= today; month = month.AddMonths(1))
            {
                if (done.Contains(month)) continue;
                var last = Min(month.AddMonths(1).AddDays(-1), today);
                IReadOnlyList<DailyKwh> days;
                try
                {
                    days = await _tapo.GetDailyEnergyAsync(plug.IpAddress, plug.Protocol, creds, month, last, today, _tz, ct);
                }
                catch (KasaProtocolException ex) when (ex.ErrCode != 0)
                {
                    var quarter = new DateOnly(month.Year, (month.Month - 1) / 3 * 3 + 1, 1);
                    _log.LogDebug("get_energy_data for {Alias} from {Month} returned error {Code}; retrying from quarter start {Quarter}",
                        plug.Alias, month, ex.ErrCode, quarter);
                    days = await _tapo.GetDailyEnergyAsync(plug.IpAddress, plug.Protocol, creds, quarter,
                        Min(quarter.AddMonths(3).AddDays(-1), today), today, _tz, ct);
                    for (var m = quarter; m < quarter.AddMonths(3); m = m.AddMonths(1)) done.Add(m);
                }
                done.Add(month);

                var inWindow = days.Where(d => new DateOnly(d.Year, d.Month, d.Day) >= windowStart).ToList();
                await historyRepo.UpsertDailyAsync(plug.Id, inWindow, ct);
                total += inWindow.Count;
            }
            _log.LogInformation("History sync for {Alias} ({Protocol}): {Days} daily values", plug.Alias, plug.Protocol, total);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "History sync failed for plug {Alias} ({Ip}, {PlugId})", plug.Alias, plug.IpAddress, plug.Id);
        }
    }

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;

    /// <summary>
    /// Shelly: daily Wh per local day, from the last day already stored (re-read, it may have been partial) or
    /// <see cref="ShellyHistoryDays"/> days back on the first sync, up to today. em1 meters (EM Gen3, Pro EM) keep
    /// per-minute records: one <c>EM1Data.GetData</c> window per day (local midnight to midnight) for the days covered
    /// by a block of <c>EM1Data.GetRecords</c>, summed per day.
    /// Other Shelly devices (switch/pm1/em) keep no history: the day is the sum of the increases of our own readings
    /// of the device counter (<c>aenergy.total</c>).
    /// </summary>
    private async Task SyncShellyAsync(Plug plug, IHistoryRepository historyRepo, ISampleRepository sampleRepo, CancellationToken ct)
    {
        if (_shelly is null || _settings is null) return;
        var password = (await _settings.GetAsync(ct)).ShellyCredentials?.Password;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(_nowProvider(), DateTimeKind.Utc), _tz));
        var windowStart = today.AddDays(-(ShellyHistoryDays - 1));
        try
        {
            var known = await historyRepo.GetDailyRangeAsync(plug.Id, windowStart, today, ct);
            var start = known.Count > 0 ? known.Max(d => new DateOnly(d.Year, d.Month, d.Day)) : windowStart;
            var stored = known.ToDictionary(d => new DateOnly(d.Year, d.Month, d.Day), d => d.EnergyWh);
            // A re-read day never replaces a stored value with a smaller one (shorter device retention, partial read,
            // purged samples): the larger of the two is kept.
            List<DailyKwh> NotSmaller(IEnumerable<DailyKwh> days) =>
                days.Where(d => !stored.TryGetValue(new DateOnly(d.Year, d.Month, d.Day), out var old) || d.EnergyWh > old).ToList();
            var nowUtc = new DateTimeOffset(DateTime.SpecifyKind(_nowProvider(), DateTimeKind.Utc));

            var readings = ShellyStatusParser.Parse(await _shelly.GetStatusAsync(plug.IpAddress, password, ct));
            var total = 0;
            if (readings.HasDeviceHistory && readings.For(plug.Channel) is not null)
            {
                var blocks = (await _shelly.GetEm1RecordsAsync(plug.IpAddress, password, plug.Channel, ct)).Where(b => b.Records > 0).ToList();
                if (blocks.Count == 0)
                {
                    _log.LogInformation("History sync for {Alias} (shelly em1:{Channel}): the device has no records yet", plug.Alias, plug.Channel);
                    return;
                }
                var firstDay = ShellyEnergyData.LocalDay(DateTimeOffset.FromUnixTimeSeconds(blocks.Min(b => b.Ts)), _tz);
                if (firstDay > start) start = firstDay;
                for (var day = start; day <= today; day = day.AddDays(1))
                {
                    var (from, to) = ShellyEnergyData.DayWindow(day, _tz);
                    if (!blocks.Any(b => b.Ts <= to && b.Ts + (long)b.Records * b.Period > from)) continue;   // nothing stored that day
                    IReadOnlyList<ShellyEnergyRow> rows;
                    try
                    {
                        rows = await _shelly.GetEm1DataAsync(plug.IpAddress, password, plug.Channel, from, to, ct);
                    }
                    catch (ShellyIncompleteDataException ex)
                    {
                        _log.LogWarning("History sync for {Alias}: {Day} not stored, {Reason}", plug.Alias, day, ex.Message);
                        continue;
                    }
                    // Records stamped in the future (device clock ahead, e.g. before its first time sync) are dropped.
                    var maxTs = nowUtc.AddMinutes(5).ToUnixTimeSeconds();
                    var daily = NotSmaller(ShellyEnergyData.DailyWh(rows.Where(r => r.Ts <= maxTs), _tz, today)
                        .Where(d => new DateOnly(d.Year, d.Month, d.Day) == day));
                    if (daily.Count == 0) continue;
                    await historyRepo.UpsertDailyAsync(plug.Id, daily, ct);
                    total += daily.Count;
                }
                _log.LogInformation("History sync for {Alias} (shelly em1:{Channel}): {Days} daily values from the device", plug.Alias, plug.Channel, total);
                return;
            }

            for (var day = start; day <= today; day = day.AddDays(1))
            {
                // Start a little before midnight so the first increase of the day has a previous reading.
                var from = ShellyEnergyData.LocalMidnightUtc(day, _tz).AddMinutes(-15);
                var to = ShellyEnergyData.LocalMidnightUtc(day.AddDays(1), _tz).AddTicks(-1);
                var samples = await sampleRepo.ListByPlugAsync(plug.Id, from, to, ct);
                var points = samples.Where(s => s.Online && s.TotalWh is not null).Select(s => (s.TimestampUtc, s.TotalWh!.Value)).ToList();
                var daily = NotSmaller(ShellyEnergyData.DailyFromCumulative(points, _tz).Where(d => new DateOnly(d.Year, d.Month, d.Day) == day));
                if (daily.Count == 0) continue;
                await historyRepo.UpsertDailyAsync(plug.Id, daily, ct);
                total += daily.Count;
            }
            _log.LogInformation("History sync for {Alias} (shelly, own readings): {Days} daily values", plug.Alias, total);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "History sync failed for plug {Alias} ({Ip}, {PlugId})", plug.Alias, plug.IpAddress, plug.Id);
        }
    }

    /// <summary>Delay before the next periodic cycle: 1 minute while there is nothing to sync, otherwise 6 hours.</summary>
    public static TimeSpan NextCycleDelay(int plugsWithHistory) => plugsWithHistory == 0 ? EmptyRetryInterval : CycleInterval;

    /// <summary>One periodic cycle; returns how many plugs were synced.</summary>
    public async Task<int> RunCycleAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plugRepo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        var plugs = (await plugRepo.ListAllAsync(ct)).Where(HasHistory).ToList();
        foreach (var p in plugs)
            await SyncOneAsync(p.Id, ct);
        return plugs.Count;
    }

    /// <summary>Syncs plugs announced by discovery (new plug, manual add, plug that just became supported) right away.</summary>
    private async Task ConsumeDiscoveredAsync(IPlugDiscoveredChannel channel, CancellationToken ct)
    {
        try
        {
            await foreach (var plugId in channel.ReadAllAsync(ct))
            {
                try
                {
                    Plug? plug;
                    using (var scope = _scopeFactory.CreateScope())
                        plug = (await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct))
                            .FirstOrDefault(p => p.Id == plugId);
                    if (plug is null || !HasHistory(plug)) continue;
                    _log.LogInformation("History sync for newly added plug {Alias}", plug.Alias);
                    await SyncOneAsync(plugId, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogWarning(ex, "History sync for discovered plug {PlugId} failed", plugId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumer = _discovered is null ? Task.CompletedTask : Task.Run(() => ConsumeDiscoveredAsync(_discovered, stoppingToken), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var synced = 0;
            try { synced = await RunCycleAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "History sync cycle failed"); }
            try { await Task.Delay(NextCycleDelay(synced), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
        try { await consumer; } catch (OperationCanceledException) { }
    }
}
