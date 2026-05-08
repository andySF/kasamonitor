using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace KasaMonitor.Workers;

public sealed class DiscoveryWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly IPlugRepository _plugRepo;
    private readonly IPlugDiscoveredChannel _channel;
    private readonly IReadOnlyList<IPAddress> _broadcastAddresses;
    private readonly TimeSpan _scanTimeout;
    private readonly TimeSpan _interval;
    private readonly ILogger<DiscoveryWorker> _log;

    public DiscoveryWorker(
        IKasaClient kasa,
        IPlugRepository plugRepo,
        IPlugDiscoveredChannel channel,
        IReadOnlyList<IPAddress> broadcastAddresses,
        TimeSpan scanTimeout,
        TimeSpan interval,
        ILogger<DiscoveryWorker> log)
    {
        _kasa = kasa;
        _plugRepo = plugRepo;
        _channel = channel;
        _broadcastAddresses = broadcastAddresses;
        _scanTimeout = scanTimeout;
        _interval = interval;
        _log = log;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existingMacs = (await _plugRepo.ListAllAsync(ct)).Select(p => p.Mac).ToHashSet(StringComparer.Ordinal);

        await foreach (var dp in _kasa.DiscoverAsync(_broadcastAddresses, _scanTimeout, ct))
        {
            var wasNew = !existingMacs.Contains(dp.Info.Mac);
            var id = await _plugRepo.UpsertFromDiscoveryAsync(dp, now, ct);
            if (wasNew)
            {
                _channel.Publish(id);
                _log.LogInformation("Discovered new plug {Mac} ({Alias}) -> Id {Id}", dp.Info.Mac, dp.Info.Alias, id);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "Discovery cycle failed");
            }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
