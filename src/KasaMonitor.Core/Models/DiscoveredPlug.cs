using System.Net;

namespace KasaMonitor.Core.Models;

/// <param name="Protocol">One of <see cref="PlugProtocols"/>.</param>
/// <param name="UnsupportedReason">Non-null when the device answered discovery but cannot be queried (Romanian, shown in the UI).</param>
/// <param name="Channel">Measured channel of a multi-channel device (Shelly EM: 0 and 1); 0 for single-channel plugs.
/// The identity of a plug is (MAC, channel).</param>
/// <param name="Role">Role given to a newly added plug (Shelly em/em1 channels: <see cref="PlugRole.SubMeter"/>); an existing
/// plug keeps the role chosen in /dispozitive.</param>
public sealed record DiscoveredPlug(
    IPAddress IpAddress,
    PlugInfo Info,
    string Protocol = PlugProtocols.Legacy,
    string? UnsupportedReason = null,
    int Channel = 0,
    PlugRole Role = PlugRole.Plug)
{
    public bool IsSupported => UnsupportedReason is null;
}
