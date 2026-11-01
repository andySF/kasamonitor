using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
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

    public HistorySyncWorker(
        IKasaClient kasa,
        IServiceScopeFactory scopeFactory,
        Func<DateTime> nowProvider,
        ILogger<HistorySyncWorker> log)
    {
        _kasa = kasa;
        _scopeFactory = scopeFactory;
        _nowProvider = nowProvider;
        _log = log;
    }

    public async Task SyncOneAsync(Guid plugId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plugRepo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        var historyRepo = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();

        var plug = (await plugRepo.ListAllAsync(ct)).FirstOrDefault(p => p.Id == plugId);
        if (plug is null) return;

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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var plugRepo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
                var plugs = await plugRepo.ListAllAsync(stoppingToken);
                // Daily/monthly history exists only on the legacy protocol (emeter get_daystat/get_monthstat).
                foreach (var p in plugs.Where(p => PollingWorker.IsActive(p) && p.Protocol == PlugProtocols.Legacy))
                    await SyncOneAsync(p.Id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "History sync cycle failed"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
