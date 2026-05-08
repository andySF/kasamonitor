using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class PollingWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlugStateBroadcaster _broadcaster;
    private readonly TimeSpan _interval;
    private readonly ILogger<PollingWorker> _log;

    public PollingWorker(
        IKasaClient kasa,
        IServiceScopeFactory scopeFactory,
        IPlugStateBroadcaster broadcaster,
        TimeSpan interval,
        ILogger<PollingWorker> log)
    {
        _kasa = kasa;
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _interval = interval;
        _log = log;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plugRepo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        var sampleRepo = scope.ServiceProvider.GetRequiredService<ISampleRepository>();

        var plugs = (await plugRepo.ListAllAsync(ct)).Where(p => !p.IsArchived).ToList();
        var now = DateTimeOffset.UtcNow;
        var samplesToInsert = new List<Sample>();

        await Parallel.ForEachAsync(plugs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (plug, innerCt) =>
            {
                Sample s;
                try
                {
                    var reading = await _kasa.GetRealtimeAsync(plug.IpAddress, innerCt);
                    s = new Sample(0, plug.Id, now, true,
                        reading.PowerMw, reading.VoltageMv, reading.CurrentMa, reading.TotalWh,
                        Rssi: null, RelayOn: null, ErrorMessage: null);
                    _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, true, reading, now));
                }
                catch (Exception ex)
                {
                    s = new Sample(0, plug.Id, now, false, null, null, null, null, null, null, ex.Message);
                    _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, false, null, now));
                }
                lock (samplesToInsert) samplesToInsert.Add(s);
            });

        if (samplesToInsert.Count > 0)
            await sampleRepo.InsertManyAsync(samplesToInsert, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Polling cycle failed"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
