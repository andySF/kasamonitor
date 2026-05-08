using System.Net;

namespace KasaMonitor.Core.Models;

public sealed record DiscoveredPlug(IPAddress IpAddress, PlugInfo Info);
