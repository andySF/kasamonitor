using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>
/// Shelly Gen2/Gen3 helpers: Romanian "unsupported" texts, friendly model names, the probe-host list and the mapping
/// of one device to plugs (one <see cref="DiscoveredPlug"/> per measured channel).
/// </summary>
public static class ShellySupport
{
    public const string MissingPasswordReason = "parolă Shelly lipsă";
    public const string AuthFailedReason = "autentificare Shelly eșuată (verifică parola Shelly)";
    public const string NoMeterReason = "fără componentă de măsurare (em, em1, pm1 sau switch cu apower)";

    /// <summary>Maximum number of addresses a <c>ShellyProbeHosts</c> value may expand to.</summary>
    public const int MaxProbeHosts = 1024;

    /// <summary>Smallest CIDR prefix accepted in <c>ShellyProbeHosts</c> (/22 = 1022 hosts, within <see cref="MaxProbeHosts"/>).</summary>
    public const int MinCidrPrefix = 22;

    /// <summary>
    /// Friendly names by the <c>app</c> field of GetDeviceInfo. Only EMG3 is verified on a real device; the others
    /// follow Shelly's published app names. Unknown apps fall back to "Shelly &lt;app&gt;".
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AppNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["EMG3"] = "Shelly EM Gen3",
        ["ProEM"] = "Shelly Pro EM",
        ["Pro3EM"] = "Shelly Pro 3EM",
        ["PlugS"] = "Shelly Plus Plug S",
        ["PlusPlugS"] = "Shelly Plus Plug S",
        ["Plus1PM"] = "Shelly Plus 1PM",
        ["Plus2PM"] = "Shelly Plus 2PM",
        ["PlusPMMini"] = "Shelly Plus PM Mini",
        ["Pro1PM"] = "Shelly Pro 1PM",
        ["Pro2PM"] = "Shelly Pro 2PM",
        ["Pro4PM"] = "Shelly Pro 4PM",
    };

    /// <summary>"Shelly EM Gen3 (EMG3)" from app "EMG3"; the model code when the app is unknown and empty.</summary>
    public static string FriendlyModel(string model, string app)
    {
        if (string.IsNullOrWhiteSpace(app)) return model;
        var name = AppNames.TryGetValue(app, out var n) ? n : $"Shelly {app}";
        return $"{name} ({app})";
    }

    /// <summary>
    /// Maps one device to its plugs. A device without measuring components, or one that could not be read, becomes a
    /// single unsupported channel-0 entry so that it is listed with the reason.
    /// </summary>
    /// <param name="status">Answer of <c>Shelly.GetStatus</c>.</param>
    /// <param name="config">Answer of <c>Shelly.GetConfig</c> (names); optional.</param>
    public static IReadOnlyList<DiscoveredPlug> ToPlugs(IPAddress ip, ShellyDeviceInfo info, JsonElement status, JsonElement? config)
    {
        var readings = ShellyStatusParser.Parse(status);
        if (readings.Channels.Count == 0)
            return new[] { Unsupported(ip, info, NoMeterReason) };

        var componentType = ComponentType(readings.Kind);
        var rssi = ShellyStatusParser.Rssi(status);
        var uptime = ShellyStatusParser.Uptime(status);
        var many = readings.Channels.Count > 1;
        return readings.Channels.Select(c => new DiscoveredPlug(
            ip,
            Info(info, Alias(info, config, componentType, c.Channel, many), c.Reading.RelayOn ?? false, rssi, uptime),
            PlugProtocols.ShellyRpc,
            UnsupportedReason: null,
            Channel: c.Channel)).ToList();
    }

    /// <summary>Listed-but-not-polled placeholder (password missing / rejected, no meter, read error).</summary>
    public static DiscoveredPlug Unsupported(IPAddress ip, ShellyDeviceInfo info, string reason) =>
        new(ip, Info(info, DeviceName(info, null), false, 0, TimeSpan.Zero), PlugProtocols.ShellyRpc, reason, 0);

    /// <summary>
    /// Default alias: the channel's own name (<c>em1:N.name</c>, <c>switch:N.name</c>, ...) when set, otherwise
    /// <c>sys.device.name</c> / GetDeviceInfo name / device id, followed by " ch0", " ch1" on multi-channel devices.
    /// </summary>
    public static string Alias(ShellyDeviceInfo info, JsonElement? config, string? componentType, int channel, bool multiChannel)
    {
        if (componentType is not null
            && ShellyJson.Str(ShellyJson.Component(config, componentType, channel), "name") is { Length: > 0 } own)
            return own.Trim();
        var device = DeviceName(info, config);
        return multiChannel ? $"{device} ch{channel}" : device;
    }

    private static string DeviceName(ShellyDeviceInfo info, JsonElement? config)
    {
        var sysName = ShellyJson.Str(ShellyJson.Obj(ShellyJson.Obj(config, "sys"), "device"), "name");
        foreach (var candidate in new[] { sysName, info.Name, info.Id, info.Mac })
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        return "Shelly";
    }

    /// <summary>Config component carrying the channel name for each meter kind.</summary>
    public static string? ComponentType(ShellyMeterKind kind) => kind switch
    {
        ShellyMeterKind.Em => "em",
        ShellyMeterKind.Em1 => "em1",
        ShellyMeterKind.Pm1 => "pm1",
        ShellyMeterKind.Switch => "switch",
        _ => null,
    };

    /// <summary>PlugInfo of a Shelly channel: Model = model code (S3EM-002CXCEU), HwVer = app (EMG3), SwVer = firmware, OemId = "genN".</summary>
    private static PlugInfo Info(ShellyDeviceInfo info, string alias, bool relayOn, int rssi, TimeSpan uptime) =>
        new(info.Mac, alias, info.Model, info.App, info.Version, $"gen{info.Gen}", relayOn, rssi, uptime);

    /// <summary>
    /// Parses <c>ShellyProbeHosts</c>: comma-separated IPv4 addresses, ranges ("192.168.1.10-192.168.1.20" or
    /// "192.168.1.10-20") and CIDR blocks /22–/32 ("192.168.1.0/24", network and broadcast addresses skipped).
    /// Returns the addresses (deduplicated, at most <see cref="MaxProbeHosts"/>) and the rejected tokens.
    /// </summary>
    public static (IReadOnlyList<IPAddress> Hosts, IReadOnlyList<string> Invalid) ParseProbeHosts(string? text)
    {
        var hosts = new List<IPAddress>();
        var seen = new HashSet<uint>();
        var invalid = new List<string>();
        foreach (var token in (text ?? "").Split(new[] { ',', ';', ' ', '\n', '\r', '\t' },
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!TryExpand(token, out var first, out var last) || last < first || last - first + 1 > MaxProbeHosts)
            {
                invalid.Add(token);
                continue;
            }
            for (var a = first; a <= last && hosts.Count < MaxProbeHosts; a++)
            {
                if (seen.Add(a)) hosts.Add(FromUInt(a));
                if (a == uint.MaxValue) break;
            }
        }
        return (hosts, invalid);
    }

    private static bool TryExpand(string token, out uint first, out uint last)
    {
        first = last = 0;
        var slash = token.IndexOf('/');
        if (slash > 0)
        {
            if (!TryV4(token[..slash], out var net) || !int.TryParse(token[(slash + 1)..], out var prefix) || prefix is < MinCidrPrefix or > 32)
                return false;
            var mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
            first = net & mask;
            last = first | ~mask;
            if (prefix <= 30) { first++; last--; }   // skip network and broadcast
            return true;
        }
        var dash = token.IndexOf('-');
        if (dash > 0)
        {
            if (!TryV4(token[..dash], out first)) return false;
            var right = token[(dash + 1)..];
            if (TryV4(right, out last)) return true;
            if (byte.TryParse(right, out var lastOctet)) { last = (first & 0xFFFFFF00) | lastOctet; return true; }
            return false;
        }
        if (!TryV4(token, out first)) return false;
        last = first;
        return true;
    }

    private static bool TryV4(string s, out uint value)
    {
        value = 0;
        if (!IPAddress.TryParse(s.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || s.Count(c => c == '.') != 3)
            return false;
        var b = ip.GetAddressBytes();
        value = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        return true;
    }

    private static IPAddress FromUInt(uint v) => new(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
