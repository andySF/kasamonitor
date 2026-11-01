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

    /// <summary>
    /// Daily energy (Wh per local day) from <c>get_energy_data</c> (interval 1440) for the days from
    /// <paramref name="firstDay"/> up to <paramref name="lastDay"/>; the device may answer fewer or more days.
    /// </summary>
    Task<IReadOnlyList<DailyKwh>> GetDailyEnergyAsync(IPAddress ip, string protocol, TapoCredentials credentials,
        DateOnly firstDay, DateOnly lastDay, DateOnly today, TimeZoneInfo tz, CancellationToken ct);

    bool SupportsProtocol(string protocol);
}
