using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class HistorySyncWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly IPlugRepository _plugRepo;
    private readonly IHistoryRepository _historyRepo;
    private readonly DateTime _now;
    private readonly ILogger<HistorySyncWorker> _log;

    public HistorySyncWorker(
        IKasaClient kasa,
        IPlugRepository plugRepo,
        IHistoryRepository historyRepo,
        DateTime now,
        ILogger<HistorySyncWorker> log)
    {
        _kasa = kasa;
        _plugRepo = plugRepo;
        _historyRepo = historyRepo;
        _now = now;
        _log = log;
    }

    public async Task SyncOneAsync(Guid plugId, CancellationToken ct)
    {
        var plug = (await _plugRepo.ListAllAsync(ct)).FirstOrDefault(p => p.Id == plugId);
        if (plug is null) return;

        try
        {
            var monthsThis = await _kasa.GetMonthStatAsync(plug.IpAddress, _now.Year, ct);
            var monthsPrev = await _kasa.GetMonthStatAsync(plug.IpAddress, _now.Year - 1, ct);
            await _historyRepo.UpsertMonthlyAsync(plugId, monthsThis, ct);
            await _historyRepo.UpsertMonthlyAsync(plugId, monthsPrev, ct);

            var daysThis = await _kasa.GetDayStatAsync(plug.IpAddress, _now.Year, _now.Month, ct);
            var prevMonth = _now.AddMonths(-1);
            var daysPrev = await _kasa.GetDayStatAsync(plug.IpAddress, prevMonth.Year, prevMonth.Month, ct);
            await _historyRepo.UpsertDailyAsync(plugId, daysThis, ct);
            await _historyRepo.UpsertDailyAsync(plugId, daysPrev, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "History sync failed for {PlugId}", plugId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var plugs = await _plugRepo.ListAllAsync(stoppingToken);
                foreach (var p in plugs.Where(p => !p.IsArchived))
                    await SyncOneAsync(p.Id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "History sync cycle failed"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
