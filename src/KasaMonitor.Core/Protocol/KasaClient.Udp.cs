using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Dtos;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol;

public sealed partial class KasaClient
{
    public async IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(
        IReadOnlyList<IPAddress> broadcastAddresses,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (broadcastAddresses.Count == 0) yield break;

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = KasaWireProtocol.EncodeUdp("{\"system\":{\"get_sysinfo\":{}}}");
        foreach (var addr in broadcastAddresses)
            await udp.SendAsync(request, new IPEndPoint(addr, _port), ct);

        var seenMacs = new HashSet<string>(StringComparer.Ordinal);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);

        while (!deadlineCts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(deadlineCts.Token);
            }
            catch (OperationCanceledException) { yield break; }
            catch (SocketException) { continue; }

            DiscoveredPlug? mapped;
            try
            {
                mapped = TryParseDiscoveryResponse(result);
            }
            catch (JsonException) { continue; }
            catch (KasaProtocolException) { continue; }

            if (mapped is null) continue;
            if (!seenMacs.Add(mapped.Info.Mac)) continue;
            yield return mapped;
        }
    }

    private static DiscoveredPlug? TryParseDiscoveryResponse(UdpReceiveResult result)
    {
        var responseJson = KasaWireProtocol.DecodeUdp(result.Buffer);
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("system", out var system) ||
            !system.TryGetProperty("get_sysinfo", out var inner))
            return null;
        var dto = inner.Deserialize<SysInfoDto>();
        if (dto is null) return null;
        return new DiscoveredPlug(result.RemoteEndPoint.Address, MapSysInfo(dto));
    }
}
