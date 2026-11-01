using System.Text.Json;

namespace KasaMonitor.Core.Heating;

public sealed record ViessmannGatewayDevice(string Id, string DeviceType, string ModelId, string Status, IReadOnlyList<string> Roles);

public sealed record ViessmannGateway(string Serial, string? AggregatedStatus, IReadOnlyList<ViessmannGatewayDevice> Devices);

public sealed record ViessmannInstallation(string Id, string? AggregatedStatus, IReadOnlyList<ViessmannGateway> Gateways);

/// <summary>The heating device KasaMonitor reads: installation, gateway and device ids plus the model.</summary>
public sealed record ViessmannBoilerRef(string InstallationId, string GatewaySerial, string DeviceId, string ModelId, string? AggregatedStatus);

/// <summary>Parser of <c>GET /equipment/installations?includeGateways=true</c> (addresses and owner data are ignored).</summary>
public static class ViessmannInstallations
{
    public static IReadOnlyList<ViessmannInstallation> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ViessmannInstallation>();
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var inst in data.EnumerateArray())
        {
            var id = Scalar(inst, "id");
            if (id is null) continue;
            var gateways = new List<ViessmannGateway>();
            if (inst.TryGetProperty("gateways", out var gws) && gws.ValueKind == JsonValueKind.Array)
            {
                foreach (var gw in gws.EnumerateArray())
                {
                    var serial = Scalar(gw, "serial");
                    if (serial is null) continue;
                    var devices = new List<ViessmannGatewayDevice>();
                    if (gw.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var d in devs.EnumerateArray())
                        {
                            var roles = d.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
                                ? r.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                                : new List<string>();
                            devices.Add(new ViessmannGatewayDevice(Scalar(d, "id") ?? "", Scalar(d, "deviceType") ?? "",
                                Scalar(d, "modelId") ?? "", Scalar(d, "status") ?? "", roles));
                        }
                    }
                    gateways.Add(new ViessmannGateway(serial, Scalar(gw, "aggregatedStatus"), devices));
                }
            }
            list.Add(new ViessmannInstallation(id, Scalar(inst, "aggregatedStatus"), gateways));
        }
        return list;
    }

    /// <summary>
    /// The first device of type <c>heating</c> (the boiler; on a Vitodens 050-W id "0", model <c>E3_Vitodens_050_0822</c>),
    /// or a device with the role <c>type:boiler</c>; null when the account has no heating device.
    /// </summary>
    public static ViessmannBoilerRef? PickBoiler(IReadOnlyList<ViessmannInstallation> installations)
    {
        foreach (var inst in installations)
            foreach (var gw in inst.Gateways)
                foreach (var d in gw.Devices)
                    if (d.DeviceType.Equals("heating", StringComparison.OrdinalIgnoreCase) || d.Roles.Contains("type:boiler"))
                        return new ViessmannBoilerRef(inst.Id, gw.Serial, d.Id, d.ModelId, gw.AggregatedStatus ?? inst.AggregatedStatus);
        return null;
    }

    /// <summary>"E3_Vitodens_050_0822" → "Vitodens 050" (the device label of the metrics); other ids are kept readable.</summary>
    public static string FriendlyModel(string modelId)
    {
        var parts = modelId.Split('_', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count > 1 && parts[0].Length <= 3 && parts[0].StartsWith('E')) parts.RemoveAt(0);          // "E3"
        if (parts.Count > 2 && parts[^1].Length == 4 && parts[^1].All(char.IsDigit)) parts.RemoveAt(parts.Count - 1); // "0822"
        return parts.Count == 0 ? modelId : string.Join(' ', parts);
    }

    private static string? Scalar(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }
}
