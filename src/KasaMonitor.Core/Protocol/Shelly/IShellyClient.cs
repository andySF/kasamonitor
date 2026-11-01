using System.Net;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>
/// Shelly Gen2/Gen3 local JSON-RPC over HTTP (GET <c>/rpc/&lt;Method&gt;</c>), read-only: only <c>*.Get*</c> methods are
/// ever called. <paramref name="password"/> parameters are the device's admin password (digest auth), null when unset.
/// </summary>
public interface IShellyClient
{
    /// <summary><c>Shelly.GetDeviceInfo</c> (no authentication needed). <paramref name="timeout"/> overrides the request timeout.</summary>
    Task<ShellyDeviceInfo> GetDeviceInfoAsync(IPAddress ip, CancellationToken ct, TimeSpan? timeout = null);

    /// <summary><c>Shelly.GetStatus</c>: the whole device status (all components) in one request.</summary>
    Task<JsonElement> GetStatusAsync(IPAddress ip, string? password, CancellationToken ct);

    /// <summary><c>Shelly.GetConfig</c>: names, CT type, relay defaults, time zone.</summary>
    Task<JsonElement> GetConfigAsync(IPAddress ip, string? password, CancellationToken ct);

    /// <summary><c>EM1Data.GetRecords</c>: which time ranges the device keeps for channel <paramref name="id"/>.</summary>
    Task<IReadOnlyList<ShellyDataBlock>> GetEm1RecordsAsync(IPAddress ip, string? password, int id, CancellationToken ct);

    /// <summary>
    /// <c>EM1Data.GetData</c> from <paramref name="fromTs"/> to <paramref name="toTs"/> (unix seconds), all pages.
    /// Throws <see cref="ShellyIncompleteDataException"/> when the page limit is reached with records still pending.
    /// </summary>
    Task<IReadOnlyList<ShellyEnergyRow>> GetEm1DataAsync(IPAddress ip, string? password, int id, long fromTs, long toTs, CancellationToken ct);

    /// <summary>mDNS query for <c>_shelly._tcp.local</c>; yields each answering instance once.</summary>
    IAsyncEnumerable<ShellyMdnsAnswer> DiscoverAsync(TimeSpan timeout, CancellationToken ct);
}
