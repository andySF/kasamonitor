using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Protocol.Tapo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net;

namespace KasaMonitor.Workers;

public enum DiscoveryStatus
{
    /// <summary>Not in the database (auto-add is off): can be added with one click.</summary>
    New,
    /// <summary>Was not in the database and has just been added (auto-add on, or added from the UI).</summary>
    Added,
    /// <summary>Already in the database (IP / last seen refreshed).</summary>
    Known,
    /// <summary>Answered discovery but cannot be queried (see <see cref="DiscoveredPlug.UnsupportedReason"/>).</summary>
    Unsupported,
}

public sealed record DiscoveryResultItem(DiscoveredPlug Plug, DiscoveryStatus Status, Guid? PlugId);

public sealed record DiscoveryScan(DateTimeOffset AtUtc, bool AutoAdd, IReadOnlyList<DiscoveryResultItem> Items)
{
    public int NewCount => Items.Count(i => i.Status is DiscoveryStatus.New or DiscoveryStatus.Added);
    public int UnsupportedCount => Items.Count(i => i.Status == DiscoveryStatus.Unsupported);
}

public sealed record AddByIpResult(bool Success, string Message, Guid? PlugId);

/// <summary>
/// Single owner of "find plugs and put them in the database": legacy UDP 9999 broadcast + Tapo UDP 20002 probe +
/// Shelly mDNS / probe hosts, classification (supported / unsupported), auto-add, one-click add and add-by-IP.
/// A plug's identity is (MAC, channel): a multi-channel Shelly meter yields one entry per measured channel. Used by <see cref="DiscoveryWorker"/>
/// and by the /plugs page ("Scanează acum").
/// </summary>
public sealed class PlugDiscoveryService
{
    private readonly IKasaClient _kasa;
    private readonly ITapoClient _tapo;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKasaSettingsService _settings;
    private readonly IPlugDiscoveredChannel _channel;
    private readonly TimeSpan _scanTimeout;
    private readonly ILogger<PlugDiscoveryService> _log;
    private readonly IShellyClient? _shelly;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Timeout of the Shelly.GetDeviceInfo probe of each mDNS answer / <c>ShellyProbeHosts</c> address.</summary>
    public static readonly TimeSpan ShellyProbeTimeout = TimeSpan.FromSeconds(1);

    public PlugDiscoveryService(
        IKasaClient kasa,
        ITapoClient tapo,
        IServiceScopeFactory scopeFactory,
        IKasaSettingsService settings,
        IPlugDiscoveredChannel channel,
        TimeSpan scanTimeout,
        ILogger<PlugDiscoveryService> log,
        IShellyClient? shelly = null)
    {
        _shelly = shelly;
        _kasa = kasa;
        _tapo = tapo;
        _scopeFactory = scopeFactory;
        _settings = settings;
        _channel = channel;
        _scanTimeout = scanTimeout;
        _log = log;
    }

    /// <summary>Result of the most recent scan (worker or manual), for the /plugs page.</summary>
    public DiscoveryScan? LastScan { get; private set; }

    public async Task<DiscoveryScan> ScanAsync(CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        var found = await DiscoverAllAsync(settings, ct);

        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
            var existing = (await repo.ListAllAsync(ct)).ToDictionary(p => (p.Mac, p.Channel));
            var now = DateTimeOffset.UtcNow;
            var items = new List<DiscoveryResultItem>();

            foreach (var dp in found)
            {
                existing.TryGetValue((dp.Info.Mac, dp.Channel), out var known);
                if (known is not null)
                {
                    var id = await repo.UpsertFromDiscoveryAsync(dp, now, ct);
                    if (!known.IsSupported && dp.IsSupported)
                        _channel.Publish(id);   // just became pollable (e.g. credentials added): history sync now
                    var stillUnsupported = !dp.IsSupported && !known.IsSupported;
                    items.Add(new DiscoveryResultItem(dp, stillUnsupported ? DiscoveryStatus.Unsupported : DiscoveryStatus.Known, id));
                }
                else if (settings.AutoAddDiscoveredPlugs)
                {
                    var id = await repo.UpsertFromDiscoveryAsync(dp, now, ct);
                    _channel.Publish(id);
                    _log.LogInformation("Discovered new plug {Mac} ({Alias}, {Model}, {Protocol}) at {Ip} -> Id {Id}{Unsupported}",
                        dp.Info.Mac, dp.Info.Alias, dp.Info.Model, dp.Protocol, dp.IpAddress, id,
                        dp.IsSupported ? "" : $" [nesuportat: {dp.UnsupportedReason}]");
                    items.Add(new DiscoveryResultItem(dp, dp.IsSupported ? DiscoveryStatus.Added : DiscoveryStatus.Unsupported, id));
                }
                else
                {
                    items.Add(new DiscoveryResultItem(dp, dp.IsSupported ? DiscoveryStatus.New : DiscoveryStatus.Unsupported, null));
                }
            }

            var scan = new DiscoveryScan(now, settings.AutoAddDiscoveredPlugs, items);
            LastScan = scan;
            _log.LogInformation(
                "Discovery on {Networks}: found {Found} (Shelly channels {Shelly}), new {New}, unsupported {Unsupported} (auto-add {AutoAdd})",
                string.Join(",", settings.BroadcastAddresses), items.Count, items.Count(i => i.Plug.Protocol == PlugProtocols.ShellyRpc),
                scan.NewCount, scan.UnsupportedCount, settings.AutoAddDiscoveredPlugs ? "on" : "off");
            return scan;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds (or refreshes) one plug from the last scan, from the UI.</summary>
    public async Task<Guid> AddAsync(DiscoveredPlug plug, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
            var before = await repo.GetByMacAsync(plug.Info.Mac, plug.Channel, ct);
            var wasKnown = before is not null;
            var id = await repo.UpsertFromDiscoveryAsync(plug, DateTimeOffset.UtcNow, ct);
            if (wasKnown && !before!.IsSupported && plug.IsSupported)
                _channel.Publish(id);
            if (!wasKnown)
            {
                _channel.Publish(id);
                _log.LogInformation("Added plug {Mac} channel {Channel} ({Alias}, {Protocol}) at {Ip} -> Id {Id}",
                    plug.Info.Mac, plug.Channel, plug.Info.Alias, plug.Protocol, plug.IpAddress, id);
            }
            if (LastScan is { } scan)
            {
                LastScan = scan with
                {
                    Items = scan.Items.Select(i => i.Plug.Info.Mac == plug.Info.Mac && i.Plug.Channel == plug.Channel
                        ? i with { Status = plug.IsSupported ? DiscoveryStatus.Added : DiscoveryStatus.Unsupported, PlugId = id }
                        : i).ToList()
                };
            }
            return id;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Manual add: legacy sysinfo on TCP 9999, then the Tapo probe on UDP 20002 (+ KLAP/AES), then KLAP and AES directly,
    /// then Shelly.GetDeviceInfo over HTTP (one entry per measured channel).
    /// </summary>
    public async Task<AddByIpResult> AddByIpAsync(IPAddress ip, CancellationToken ct)
    {
        var settings = await _settings.GetAsync(ct);
        DiscoveredPlug? dp = null;
        string? legacyError = null;
        try
        {
            var info = await _kasa.GetSysInfoAsync(ip, ct);
            dp = new DiscoveredPlug(ip, info, PlugProtocols.Legacy);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            legacyError = ex.Message;
        }

        if (dp is null)
        {
            await foreach (var r in _tapo.DiscoverAsync(new[] { ip }, _scanTimeout, ct))
            {
                dp = await ResolveTapoAsync(r with { IpAddress = ip }, settings.TapoCredentials, ct);
                if (dp is not null) break;
            }
        }

        // Device did not answer UDP 20002 (e.g. other subnet): try each SMART transport directly.
        if (dp is null && settings.TapoCredentials is { } creds)
        {
            foreach (var protocol in new[] { PlugProtocols.Klap, PlugProtocols.Aes }.Where(_tapo.SupportsProtocol))
            {
                try
                {
                    var info = await _tapo.GetDeviceInfoAsync(ip, protocol, creds, ct);
                    dp = new DiscoveredPlug(ip, info, protocol);
                    break;
                }
                catch (TapoAuthenticationException)
                {
                    return new AddByIpResult(false, $"{ip}: răspunde pe {protocol.ToUpperInvariant()}, dar {TapoSupport.AuthFailedReason}.", null);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogInformation("Add by IP {Ip}: {Protocol} failed: {Reason}", ip, protocol, ex.Message);
                }
            }
        }

        if (dp is null && _shelly is not null)
        {
            var shellyPlugs = await ResolveShellyAsync(ip, settings.ShellyCredentials, ct, timeout: null);
            if (shellyPlugs.Count > 0)
            {
                Guid? firstId = null;
                foreach (var sp in shellyPlugs)
                {
                    var spId = await AddAsync(sp, ct);
                    firstId ??= spId;
                }
                var first = shellyPlugs[0];
                var shellyMsg = first.IsSupported
                    ? $"{ip}: adăugat {ShellySupport.FriendlyModel(first.Info.Model, first.Info.HwVer)}, {shellyPlugs.Count} canal(e): " +
                      string.Join(", ", shellyPlugs.Select(s => $"„{s.Info.Alias}”")) + "."
                    : $"{ip}: adăugat în listă ca {TapoSupport.Describe(first.Info.Model, first.UnsupportedReason!)}.";
                return new AddByIpResult(true, shellyMsg, firstId);
            }
        }

        if (dp is null)
            return new AddByIpResult(false,
                $"{ip}: nu răspunde nici pe protocolul legacy (TCP 9999: {legacyError}), nici la descoperirea Tapo (UDP 20002) / KLAP / AES" +
                (_shelly is null ? "." : ", nici ca Shelly Gen2+ (HTTP /rpc/Shelly.GetDeviceInfo)."), null);

        var id = await AddAsync(dp, ct);
        var msg = dp.IsSupported
            ? $"{ip}: adăugată „{dp.Info.Alias}” ({dp.Info.Model}, {dp.Protocol})."
            : $"{ip}: adăugată în listă ca {TapoSupport.Describe(dp.Info.Model, dp.UnsupportedReason!)}.";
        return new AddByIpResult(true, msg, id);
    }

    private async Task<IReadOnlyList<DiscoveredPlug>> DiscoverAllAsync(KasaRuntimeSettings settings, CancellationToken ct)
    {
        var addresses = settings.BroadcastAddresses;
        var creds = settings.TapoCredentials;
        var legacyTask = CollectAsync(_kasa.DiscoverAsync(addresses, _scanTimeout, ct));
        var tapoTask = CollectAsync(_tapo.DiscoverAsync(addresses, _scanTimeout, ct));
        var shellyTask = _shelly is not null && settings.ShellyDiscoveryEnabled
            ? DiscoverShellyAsync(settings, ct)
            : Task.FromResult<IReadOnlyList<DiscoveredPlug>>(Array.Empty<DiscoveredPlug>());
        await Task.WhenAll(legacyTask, tapoTask, shellyTask);

        var byId = new Dictionary<(string Mac, int Channel), DiscoveredPlug>();
        foreach (var dp in legacyTask.Result) byId.TryAdd((dp.Info.Mac, dp.Channel), dp);   // legacy wins for devices that answer both
        foreach (var r in tapoTask.Result)
        {
            if (byId.ContainsKey((r.Mac, 0))) continue;
            var dp = await ResolveTapoAsync(r, creds, ct);
            if (dp is not null) byId.TryAdd((dp.Info.Mac, dp.Channel), dp);
        }
        foreach (var dp in shellyTask.Result) byId.TryAdd((dp.Info.Mac, dp.Channel), dp);
        return byId.Values.ToList();
    }

    /// <summary>
    /// Shelly Gen2+: mDNS (<c>_shelly._tcp.local</c>) plus the optional <c>ShellyProbeHosts</c> list; every address that
    /// answers Shelly.GetDeviceInfo is read (GetStatus + GetConfig) and mapped to one entry per measured channel.
    /// </summary>
    private async Task<IReadOnlyList<DiscoveredPlug>> DiscoverShellyAsync(KasaRuntimeSettings settings, CancellationToken ct)
    {
        var shelly = _shelly!;
        var addresses = new List<IPAddress>();
        try
        {
            await foreach (var answer in shelly.DiscoverAsync(_scanTimeout, ct))
                if (!addresses.Contains(answer.Address)) addresses.Add(answer.Address);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Shelly mDNS discovery failed: {Reason}", ex.Message);
        }
        var fromMdns = addresses.Count;
        foreach (var ip in settings.ShellyProbeList)
            if (!addresses.Contains(ip)) addresses.Add(ip);

        var results = new System.Collections.Concurrent.ConcurrentBag<DiscoveredPlug>();
        await Parallel.ForEachAsync(addresses, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = ct },
            async (ip, innerCt) =>
            {
                foreach (var dp in await ResolveShellyAsync(ip, settings.ShellyCredentials, innerCt, ShellyProbeTimeout))
                    results.Add(dp);
            });
        _log.LogDebug("Shelly discovery: {Mdns} via mDNS, {Probed} probe hosts, {Channels} channels",
            fromMdns, settings.ShellyProbeList.Count, results.Count);
        return results.ToList();
    }

    /// <summary>One Shelly address to plugs; empty when it is not a Shelly Gen2+ device (no answer to GetDeviceInfo).</summary>
    private async Task<IReadOnlyList<DiscoveredPlug>> ResolveShellyAsync(IPAddress ip, ShellyCredentials? creds, CancellationToken ct, TimeSpan? timeout)
    {
        ShellyDeviceInfo info;
        try
        {
            info = await _shelly!.GetDeviceInfoAsync(ip, ct, timeout);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogDebug("{Ip} is not a Shelly Gen2+ device: {Reason}", ip, ex.Message);
            return Array.Empty<DiscoveredPlug>();
        }
        return await ShellyPlugReader.ReadAsync(_shelly, ip, info, creds, ct);
    }

    /// <summary>Turns a UDP 20002 answer into a plug: queries it over KLAP/AES when possible, otherwise an unsupported placeholder.</summary>
    private async Task<DiscoveredPlug?> ResolveTapoAsync(TapoDiscoveryResponse r, TapoCredentials? creds, CancellationToken ct)
    {
        var (route, reason) = TapoSupport.Classify(r);
        switch (route)
        {
            case TapoSupport.Route.Legacy:
                try
                {
                    var info = await _kasa.GetSysInfoAsync(r.IpAddress, ct);
                    return new DiscoveredPlug(r.IpAddress, info, PlugProtocols.Legacy);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _log.LogDebug("Device {Mac} at {Ip} answered UDP 20002 as {Type}/XOR but not TCP 9999: {Reason}",
                        r.Mac, r.IpAddress, r.DeviceType, ex.Message);
                    return null;
                }

            case TapoSupport.Route.Klap or TapoSupport.Route.Aes
                when TapoSupport.ProtocolFor(route) is { } protocol && _tapo.SupportsProtocol(protocol):
                if (creds is null)
                    return new DiscoveredPlug(r.IpAddress, TapoSupport.PlaceholderInfo(r), protocol, TapoSupport.MissingCredentialsReason);
                try
                {
                    var info = await _tapo.GetDeviceInfoAsync(r.IpAddress, protocol, creds, ct);
                    // Keep the MAC of the discovery answer as the identity (same normalization).
                    return new DiscoveredPlug(r.IpAddress, info with { Mac = r.Mac }, protocol);
                }
                catch (TapoAuthenticationException)
                {
                    return new DiscoveredPlug(r.IpAddress, TapoSupport.PlaceholderInfo(r), protocol, TapoSupport.AuthFailedReason);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    return new DiscoveredPlug(r.IpAddress, TapoSupport.PlaceholderInfo(r), protocol,
                        $"eroare {protocol.ToUpperInvariant()}: {ex.Message}");
                }

            default:
                return new DiscoveredPlug(r.IpAddress, TapoSupport.PlaceholderInfo(r), PlugProtocols.Unsupported,
                    reason ?? $"protocol {r.EncryptType}");
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var x in source) list.Add(x);
        return list;
    }
}
