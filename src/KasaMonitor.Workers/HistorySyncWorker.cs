using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
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
    private readonly SemaphoreSlim _syncGate = new(1, 1);

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
        IPlugDiscoveredChannel? discovered = null)
    {
        _discovered = discovered;
        _kasa = kasa;
        _scopeFactory = scopeFactory;
        _nowProvider = nowProvider;
        _log = log;
        _tapo = tapo;
        _settings = settings;
        _tz = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>Legacy plugs (emeter daystat/monthstat) and, when a Tapo client is configured, KLAP/AES plugs.</summary>
    public bool HasHistory(Plug p) =>
        PollingWorker.IsActive(p)
        && (p.Protocol == PlugProtocols.Legacy || (_tapo is not null && _settings is not null && _tapo.SupportsProtocol(p.Protocol)));

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
