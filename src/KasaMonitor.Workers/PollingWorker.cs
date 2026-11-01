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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, LegacyCounters> _legacyCounters = new();
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _tz;

    /// <summary>How often a legacy plug's emeter.get_daystat is asked for today's / this month's Wh.</summary>
    public static readonly TimeSpan LegacyCountersRefresh = TimeSpan.FromMinutes(5);

    private sealed record LegacyCounters(DateTimeOffset ReadAtUtc, DateOnly Day, long? TodayWh, long? MonthWh);

    public PollingWorker(
        IKasaClient kasa,
        ITapoClient tapo,
        IServiceScopeFactory scopeFactory,
        IPlugStateBroadcaster broadcaster,
        IKasaSettingsService settings,
        TimeSpan interval,
        ILogger<PollingWorker> log,
        Func<DateTimeOffset>? clock = null,
        TimeZoneInfo? timeZone = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tz = timeZone ?? TimeZoneInfo.Local;
        _kasa = kasa;
        _tapo = tapo;
        _scopeFactory = scopeFactory;
        _broadcaster = broadcaster;
        _settings = settings;
        _interval = interval;
        _log = log;
    }

    /// <summary>Plugs that are polled and exported: not withdrawn (archived) and speaking a supported protocol.</summary>
    public static bool IsActive(Plug p) => p.IsPolled;

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

    private async Task<EmeterReading> ReadAsync(Plug plug, TapoCredentials? creds, CancellationToken ct)
    {
        if (plug.Protocol == PlugProtocols.Legacy)
        {
            var reading = await _kasa.GetRealtimeAsync(plug.IpAddress, ct);
            var counters = await LegacyCountersAsync(plug, ct);
            return counters is null ? reading : reading with { TodayWh = counters.TodayWh, MonthWh = counters.MonthWh };
        }
        if (!_tapo.SupportsProtocol(plug.Protocol))
            throw new KasaProtocolException($"Protocol '{plug.Protocol}' is not supported.");
        if (creds is null)
            throw new InvalidOperationException(TapoSupport.MissingCredentialsReason);
        return await _tapo.GetEnergyUsageAsync(plug.IpAddress, plug.Protocol, creds, ct);
    }

    /// <summary>
    /// Today's and this month's Wh of a legacy plug from emeter.get_daystat (the device's own day counters),
    /// refreshed every <see cref="LegacyCountersRefresh"/> or when the local day changes. A failed refresh keeps the
    /// previous values for the same day (the live reading must not fail because of it).
    /// </summary>
    private async Task<LegacyCounters?> LegacyCountersAsync(Plug plug, CancellationToken ct)
    {
        var now = _clock();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _tz).DateTime);
        if (_legacyCounters.TryGetValue(plug.Id, out var cached) && cached.Day == today && now - cached.ReadAtUtc < LegacyCountersRefresh)
            return cached;
        try
        {
            var days = await _kasa.GetDayStatAsync(plug.IpAddress, today.Year, today.Month, ct);
            var thisMonth = days.Where(d => d.Year == today.Year && d.Month == today.Month).ToList();
            var fresh = new LegacyCounters(now, today,
                thisMonth.Where(d => d.Day == today.Day).Sum(d => (long?)d.EnergyWh) ?? 0,
                thisMonth.Sum(d => d.EnergyWh));
            _legacyCounters[plug.Id] = fresh;
            return fresh;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("get_daystat for {Alias} failed: {Reason}", plug.Alias, ex.Message);
            return cached is not null && cached.Day == today ? cached : null;
        }
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
