using System.Net;

namespace KasaMonitor.Core.Models;

/// <param name="Protocol">One of <see cref="PlugProtocols"/>.</param>
/// <param name="UnsupportedReason">Non-null when the device answered discovery but cannot be queried (Romanian, shown in the UI).</param>
public sealed record DiscoveredPlug(
    IPAddress IpAddress,
    PlugInfo Info,
    string Protocol = PlugProtocols.Legacy,
    string? UnsupportedReason = null)
{
    public bool IsSupported => UnsupportedReason is null;
}
