using System.Net;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>
/// Shelly Gen2/Gen3 local JSON-RPC over HTTP (GET <c>/rpc/&lt;Method&gt;</c>). Reads use only <c>*.Get*</c> methods; the
/// single write is <see cref="SetSwitchAsync"/> (<c>Switch.Set</c>, the relay command asked from the UI). Nothing
/// else is ever written (no SetConfig, Reboot, Update, Reset). <c>password</c> parameters are the device's admin
/// password (digest auth), null when unset.
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

    /// <summary>
    /// <c>Switch.Set {id, on}</c>: turns relay <paramref name="switchId"/> on or off. The only write the application
    /// performs, called only by the relay command service after the user confirmed it in the UI.
    /// </summary>
    Task SetSwitchAsync(IPAddress ip, string? password, int switchId, bool on, CancellationToken ct);

    /// <summary>mDNS query for <c>_shelly._tcp.local</c>; yields each answering instance once.</summary>
    IAsyncEnumerable<ShellyMdnsAnswer> DiscoverAsync(TimeSpan timeout, CancellationToken ct);
}
