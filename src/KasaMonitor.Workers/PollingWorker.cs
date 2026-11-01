using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Tapo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class PollingWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly ITapoClient _tapo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPlugStateBroadcaster _broadcaster;
    private readonly IKasaSettingsService _settings;
    private readonly TimeSpan _interval;
    private readonly ILogger<PollingWorker> _log;
    private readonly PollFailureTracker _failures = new();

    public PollingWorker(
        IKasaClient kasa,
        ITapoClient tapo,
        IServiceScopeFactory scopeFactory,
        IPlugStateBroadcaster broadcaster,
        IKasaSettingsService settings,
        TimeSpan interval,
        ILogger<PollingWorker> log)
    {
        _kasa = kasa;
        _tapo = tapo;
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _settings = settings;
        _interval = interval;
        _log = log;
    }

    /// <summary>Plugs that are polled and exported: not withdrawn (archived) and speaking a supported protocol.</summary>
    public static bool IsActive(Plug p) => !p.IsArchived && p.IsSupported;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var plugRepo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        var sampleRepo = scope.ServiceProvider.GetRequiredService<ISampleRepository>();

        var all = await plugRepo.ListAllAsync(ct);
        foreach (var inactive in all.Where(p => !IsActive(p)))
            _broadcaster.Remove(inactive.Id);   // drops its /metrics series and dashboard card

        var plugs = all.Where(IsActive).ToList();
        TapoCredentials? creds = null;
        if (plugs.Any(p => p.Protocol != PlugProtocols.Legacy))
            creds = (await _settings.GetAsync(ct)).TapoCredentials;

        var now = DateTimeOffset.UtcNow;
        var samplesToInsert = new List<Sample>();

        await Parallel.ForEachAsync(plugs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (plug, innerCt) =>
            {
                var model = PlugModelName.Short(plug.Model);
                Sample s;
                try
                {
                    var reading = await ReadAsync(plug, creds, innerCt);
                    s = new Sample(0, plug.Id, now, true,
                        reading.PowerMw, reading.VoltageMv, reading.CurrentMa, reading.TotalWh,
                        Rssi: null, RelayOn: null, ErrorMessage: null);
                    _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, true, reading, now, model, plug.Protocol));
                    var ended = _failures.RecordSuccess(plug.Id);
                    if (ended > 0)
                        _log.LogInformation("Plug {Alias} ({Ip}) is responding again after {Failures} failed polls",
                            plug.Alias, plug.IpAddress, ended);
                }
                catch (Exception ex) when (!innerCt.IsCancellationRequested)
                {
                    s = new Sample(0, plug.Id, now, false, null, null, null, null, null, null, ex.Message);
                    _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, false, null, now, model, plug.Protocol));
                    var (shouldLog, count) = _failures.RecordFailure(plug.Id, now);
                    if (shouldLog)
                        _log.LogWarning("Poll failed for plug {Alias} ({Ip}, {Protocol}), {Failures} consecutive failures: {Reason}",
                            plug.Alias, plug.IpAddress, plug.Protocol, count, ex.Message);
                }
                lock (samplesToInsert) samplesToInsert.Add(s);
            });

        if (samplesToInsert.Count > 0)
            await sampleRepo.InsertManyAsync(samplesToInsert, ct);
    }

    private Task<EmeterReading> ReadAsync(Plug plug, TapoCredentials? creds, CancellationToken ct)
    {
        if (plug.Protocol == PlugProtocols.Legacy)
            return _kasa.GetRealtimeAsync(plug.IpAddress, ct);
        if (!_tapo.SupportsProtocol(plug.Protocol))
            throw new KasaProtocolException($"Protocol '{plug.Protocol}' is not supported.");
        if (creds is null)
            throw new InvalidOperationException(TapoSupport.MissingCredentialsReason);
        return _tapo.GetEnergyUsageAsync(plug.IpAddress, plug.Protocol, creds, ct);
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
