using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Protocol.Tapo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

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
    private readonly IShellyClient? _shelly;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _shellyInfoReadAt = new(StringComparer.Ordinal);

    /// <summary>How often Shelly.GetDeviceInfo + Shelly.GetConfig refresh a Shelly device's model, firmware and default aliases.</summary>
    public static readonly TimeSpan ShellyInfoRefresh = TimeSpan.FromHours(1);

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
        TimeZoneInfo? timeZone = null,
        IShellyClient? shelly = null)
    {
        _shelly = shelly;
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
        KasaRuntimeSettings? settings = null;
        if (plugs.Any(p => p.Protocol != PlugProtocols.Legacy))
            settings = await _settings.GetAsync(ct);
        var creds = settings?.TapoCredentials;

        var now = DateTimeOffset.UtcNow;
        var samplesToInsert = new List<Sample>();
        var shellyRefreshes = new System.Collections.Concurrent.ConcurrentBag<(IReadOnlyList<Plug> Channels, JsonElement Status)>();

        // One poll unit per device: a single plug, or all channels of one Shelly device (one Shelly.GetStatus for all).
        var units = plugs.Where(p => p.Protocol != PlugProtocols.ShellyRpc).Select(p => (IReadOnlyList<Plug>)new[] { p })
            .Concat(plugs.Where(p => p.Protocol == PlugProtocols.ShellyRpc)
                .GroupBy(p => p.Mac, StringComparer.Ordinal)
                .Select(g => (IReadOnlyList<Plug>)g.OrderBy(p => p.Channel).ToList()))
            .ToList();

        await Parallel.ForEachAsync(units, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (unit, innerCt) =>
            {
                if (unit[0].Protocol == PlugProtocols.ShellyRpc)
                {
                    await PollShellyAsync(unit, settings!, now, samplesToInsert, shellyRefreshes, innerCt);
                    return;
                }
                var plug = unit[0];
                try
                {
                    var reading = await ReadAsync(plug, creds, innerCt);
                    Succeeded(plug, reading, now, samplesToInsert);
                }
                catch (Exception ex) when (!innerCt.IsCancellationRequested)
                {
                    Failed(plug, ex.Message, now, samplesToInsert, log: true);
                }
            });

        if (samplesToInsert.Count > 0)
            await sampleRepo.InsertManyAsync(samplesToInsert, ct);

        // Hourly Shelly info/config refresh: after the samples are stored, so it never delays the readings.
        foreach (var (channels, status) in shellyRefreshes)
        {
            try { await RefreshShellyInfoAsync(channels, status, settings!, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug("Shelly info refresh for {Ip} failed: {Reason}", channels[0].IpAddress, ex.Message);
            }
        }
    }

    private void Succeeded(Plug plug, EmeterReading reading, DateTimeOffset now, List<Sample> samples)
    {
        var s = new Sample(0, plug.Id, now, true,
            reading.PowerMw, reading.VoltageMv, reading.CurrentMa, reading.TotalWh,
            Rssi: null, RelayOn: reading.RelayOn, ErrorMessage: null);
        _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, true, reading, now,
            PlugModelName.Short(plug.Model), plug.Protocol, plug.Channel, plug.Role));
        var ended = _failures.RecordSuccess(plug.Id);
        if (ended > 0)
            _log.LogInformation("Plug {Alias} ({Ip}) is responding again after {Failures} failed polls",
                plug.Alias, plug.IpAddress, ended);
        lock (samples) samples.Add(s);
    }

    /// <param name="log">False for the other channels of a Shelly device whose single request failed (one warning per device).</param>
    private void Failed(Plug plug, string reason, DateTimeOffset now, List<Sample> samples, bool log)
    {
        var s = new Sample(0, plug.Id, now, false, null, null, null, null, null, null, reason);
        _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, false, null, now,
            PlugModelName.Short(plug.Model), plug.Protocol, plug.Channel, plug.Role));
        var (shouldLog, count) = _failures.RecordFailure(plug.Id, now);
        if (shouldLog && log)
            _log.LogWarning("Poll failed for plug {Alias} ({Ip}, {Protocol}), {Failures} consecutive failures: {Reason}",
                plug.Alias, plug.IpAddress, plug.Protocol, count, reason);
        lock (samples) samples.Add(s);
    }

    /// <summary>
    /// All channels of one Shelly device from a single <c>Shelly.GetStatus</c> (timeout in the client, 3 s). Every
    /// <see cref="ShellyInfoRefresh"/> the device's info and config are read again (queued in <paramref name="refreshes"/>,
    /// run after the samples are inserted) to refresh model, firmware and the default aliases of the channels already
    /// in the database (custom aliases are kept).
    /// </summary>
    private async Task PollShellyAsync(IReadOnlyList<Plug> channels, KasaRuntimeSettings settings, DateTimeOffset now,
        List<Sample> samples, System.Collections.Concurrent.ConcurrentBag<(IReadOnlyList<Plug>, JsonElement)> refreshes, CancellationToken ct)
    {
        var ip = channels[0].IpAddress;
        JsonElement status;
        try
        {
            if (_shelly is null) throw new KasaProtocolException($"Protocol '{PlugProtocols.ShellyRpc}' is not supported.");
            status = await _shelly.GetStatusAsync(ip, settings.ShellyCredentials?.Password, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = ex is ShellyAuthenticationException auth
                ? (auth.PasswordMissing ? ShellySupport.MissingPasswordReason : ShellySupport.AuthFailedReason)
                : ex.Message;
            foreach (var plug in channels) Failed(plug, reason, now, samples, log: plug == channels[0]);
            return;
        }

        var readings = ShellyStatusParser.Parse(status);
        foreach (var plug in channels)
        {
            if (readings.For(plug.Channel) is { } reading) Succeeded(plug, reading, now, samples);
            else Failed(plug, $"canalul {plug.Channel} lipsește din Shelly.GetStatus", now, samples, log: true);
        }

        if (!_shellyInfoReadAt.TryGetValue(channels[0].Mac, out var last) || _clock() - last >= ShellyInfoRefresh)
            refreshes.Add((channels, status));
    }

    private async Task RefreshShellyInfoAsync(IReadOnlyList<Plug> channels, JsonElement status, KasaRuntimeSettings settings, CancellationToken ct)
    {
        var mac = channels[0].Mac;
        var now = _clock();
        if (_shellyInfoReadAt.TryGetValue(mac, out var last) && now - last < ShellyInfoRefresh) return;
        _shellyInfoReadAt[mac] = now;

        var ip = channels[0].IpAddress;
        var info = await _shelly!.GetDeviceInfoAsync(ip, ct);
        var mapped = await ShellyPlugReader.ReadAsync(_shelly, ip, info, settings.ShellyCredentials, ct, status);
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        foreach (var dp in mapped.Where(d => d.IsSupported && d.Info.Mac == mac && channels.Any(c => c.Channel == d.Channel)))
            await repo.UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, ct);
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
