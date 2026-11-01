using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

public sealed class TapoClient : ITapoClient
{
    private readonly IReadOnlyDictionary<string, ISmartTransport> _transports;
    private readonly int _discoveryPort;
    private readonly Lazy<string> _rsaPublicPem = new(() =>
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportSubjectPublicKeyInfoPem();
    });

    /// <param name="discoveryPortOverride">Test-only; production uses UDP 20002.</param>
    public TapoClient(IEnumerable<ISmartTransport> transports, int discoveryPortOverride = TapoDiscoveryProtocol.Port)
    {
        _transports = transports.ToDictionary(t => t.Protocol, StringComparer.OrdinalIgnoreCase);
        _discoveryPort = discoveryPortOverride;
    }

    public bool SupportsProtocol(string protocol) => _transports.ContainsKey(protocol);

    public async IAsyncEnumerable<TapoDiscoveryResponse> DiscoverAsync(
        IReadOnlyList<IPAddress> addresses, TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct)
    {
        if (addresses.Count == 0) yield break;

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        // Both probe flavours: the 16-byte one (older firmware / python-kasa < 0.7) and the RSA-key one (current python-kasa).
        var probes = new[] { TapoDiscoveryProtocol.BuildPlainProbe(), TapoDiscoveryProtocol.BuildRsaProbe(_rsaPublicPem.Value) };
        foreach (var addr in addresses)
        foreach (var probe in probes)
        {
            try { await udp.SendAsync(probe, new IPEndPoint(addr, _discoveryPort), ct); }
            catch (SocketException) { /* unreachable network: the other addresses may still answer */ }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (!deadline.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try { result = await udp.ReceiveAsync(deadline.Token); }
            catch (OperationCanceledException) { yield break; }
            catch (SocketException) { continue; }

            var parsed = TapoDiscoveryProtocol.ParseResponse(result.Buffer, result.RemoteEndPoint.Address);
            if (parsed is null || !seen.Add(parsed.Mac)) continue;
            yield return parsed;
        }
    }

    public async Task<PlugInfo> GetDeviceInfoAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct)
    {
        var result = await CallAsync(ip, protocol, credentials, "get_device_info", ct);
        return SmartMessages.MapDeviceInfo(result);
    }

    public async Task<EmeterReading> GetEnergyUsageAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct)
    {
        var usage = await CallAsync(ip, protocol, credentials, "get_energy_usage", ct);
        System.Text.Json.JsonElement? emeter = null;
        var key = ip.ToString();
        if (!_noEmeterData.ContainsKey(key))
        {
            try { emeter = await CallAsync(ip, protocol, credentials, "get_emeter_data", ct); }
            catch (KasaProtocolException ex) when (ex.ErrCode != 0)
            {
                // Device answered with an error code: method not supported by this model/firmware; do not ask again.
                _noEmeterData[key] = true;
            }
        }
        return SmartMessages.MapEnergyUsage(usage, emeter);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _noEmeterData = new();

    public async Task<IReadOnlyList<DailyKwh>> GetDailyEnergyAsync(IPAddress ip, string protocol, TapoCredentials credentials,
        DateOnly firstDay, DateOnly lastDay, DateOnly today, TimeZoneInfo tz, CancellationToken ct)
    {
        var result = await CallAsync(ip, protocol, credentials, "get_energy_data", ct,
            SmartMessages.EnergyDataParams(firstDay, lastDay, tz));
        return SmartMessages.MapDailyEnergyData(result, firstDay, today, tz);
    }

    public async Task<DeviceDetails> GetDeviceDetailsAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct)
    {
        var results = new Dictionary<string, System.Text.Json.JsonElement>();
        var errors = new List<string>();
        foreach (var method in DeviceDetailsParsers.SmartMethods)
        {
            try { results[method] = await CallAsync(ip, protocol, credentials, method, ct); }
            catch (KasaProtocolException ex) when (ex.ErrCode != 0) { errors.Add($"{method}: not supported by this device (error {ex.ErrCode})"); }
        }
        return DeviceDetailsParsers.ParseSmart(results, errors, DateTimeOffset.UtcNow);
    }

    private async Task<System.Text.Json.JsonElement> CallAsync(
        IPAddress ip, string protocol, TapoCredentials credentials, string method, CancellationToken ct,
        System.Text.Json.Nodes.JsonObject? parameters = null)
    {
        if (!_transports.TryGetValue(protocol, out var transport))
            throw new KasaProtocolException($"No transport for protocol '{protocol}'.", operation: method);
        var answer = await transport.SendAsync(ip, credentials, SmartMessages.BuildRequest(method, DateTimeOffset.UtcNow, parameters), ct);
        return SmartMessages.ParseResult(answer, method);
    }
}
