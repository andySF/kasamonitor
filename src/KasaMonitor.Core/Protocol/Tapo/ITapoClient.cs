using System.Net;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>TP-Link SMART (Tapo) devices: UDP 20002 discovery and JSON requests over a per-protocol transport.</summary>
public interface ITapoClient
{
    IAsyncEnumerable<TapoDiscoveryResponse> DiscoverAsync(IReadOnlyList<IPAddress> addresses, TimeSpan timeout, CancellationToken ct);

    /// <param name="protocol">Transport name (<see cref="PlugProtocols.Klap"/>).</param>
    Task<PlugInfo> GetDeviceInfoAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct);

    Task<EmeterReading> GetEnergyUsageAsync(IPAddress ip, string protocol, TapoCredentials credentials, CancellationToken ct);

    bool SupportsProtocol(string protocol);
}
