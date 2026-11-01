using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// Periodic automatic discovery. Enabled flag and interval are read from the database settings on every tick,
/// so changes made in /settings apply without a restart.
/// </summary>
public sealed class DiscoveryWorker : BackgroundService
{
    private readonly PlugDiscoveryService _discovery;
    private readonly IKasaSettingsService _settings;
    private readonly TimeSpan _tick;
    private readonly ILogger<DiscoveryWorker> _log;

    public DiscoveryWorker(
        PlugDiscoveryService discovery,
        IKasaSettingsService settings,
        ILogger<DiscoveryWorker> log,
        TimeSpan? tick = null)
    {
        _discovery = discovery;
        _settings = settings;
        _log = log;
        _tick = tick ?? TimeSpan.FromSeconds(30);
    }

    public Task<DiscoveryScan> RunOnceAsync(CancellationToken ct) => _discovery.ScanAsync(ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateTimeOffset? lastRun = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var s = await _settings.GetAsync(stoppingToken);
                var due = lastRun is null || DateTimeOffset.UtcNow - lastRun.Value >= TimeSpan.FromMinutes(s.AutoDiscoveryIntervalMinutes);
                if (s.AutoDiscoveryEnabled && due)
                {
                    lastRun = DateTimeOffset.UtcNow;
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Discovery cycle failed"); }
            try { await Task.Delay(_tick, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
