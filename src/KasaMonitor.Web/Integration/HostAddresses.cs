using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KasaMonitor.Web.Integration;

/// <summary>Addresses under which /metrics is reachable (with network_mode: host these are the Docker host's own IPs).</summary>
public static class HostAddresses
{
    private static readonly string[] VirtualPrefixes = { "docker", "br-", "veth", "virbr", "vEthernet", "lxc", "cni", "flannel", "zt", "tailscale" };

    /// <summary>Up, non-loopback IPv4 addresses; physical-looking interfaces first, Docker/virtual bridges last.</summary>
    public static IReadOnlyList<IPAddress> LocalIPv4()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => (Address: a.Address, Virtual: IsVirtual(n.Name))))
                .OrderBy(x => x.Virtual)
                .Select(x => x.Address)
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    private static bool IsVirtual(string name) =>
        VirtualPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Default scrape target: the host the browser used (unless it is localhost), otherwise the first LAN IPv4,
    /// with the port the browser used (8910 by default).
    /// </summary>
    public static string DefaultTarget(Uri browserBaseUri, IReadOnlyList<IPAddress> local)
    {
        var port = browserBaseUri.IsDefaultPort ? 8910 : browserBaseUri.Port;
        var host = browserBaseUri.Host;
        var isLocal = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                      || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (isLocal && local.Count > 0) host = local[0].ToString();
        else if (browserBaseUri.HostNameType == UriHostNameType.IPv6) host = $"[{host.Trim('[', ']')}]";
        return $"{host}:{port}";
    }
}
