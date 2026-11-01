using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>Answer of <c>Shelly.GetDeviceInfo</c> (available without authentication).</summary>
/// <param name="Mac">Normalized (<see cref="MacFormat.Normalize"/>), the identity stored in <c>Plugs.Mac</c>.</param>
public sealed record ShellyDeviceInfo(
    string Id,
    string Mac,
    string Model,
    int Gen,
    string Version,
    string App,
    bool AuthEnabled,
    string? Name)
{
    public static ShellyDeviceInfo Parse(JsonElement root)
    {
        var mac = ShellyJson.Str(root, "mac");
        if (string.IsNullOrWhiteSpace(mac))
            throw new KasaProtocolException("Shelly.GetDeviceInfo: missing mac", operation: "Shelly.GetDeviceInfo");
        var gen = (int)(ShellyJson.Num(root, "gen") ?? 0);
        if (gen < 2)
            throw new KasaProtocolException($"Shelly.GetDeviceInfo: generation {gen} is not Gen2+", operation: "Shelly.GetDeviceInfo");
        return new ShellyDeviceInfo(
            ShellyJson.Str(root, "id") ?? "",
            MacFormat.Normalize(mac),
            ShellyJson.Str(root, "model") ?? "",
            gen,
            ShellyJson.Str(root, "ver") ?? ShellyJson.Str(root, "fw_id") ?? "",
            ShellyJson.Str(root, "app") ?? "",
            ShellyJson.Bool(root, "auth_en") ?? false,
            ShellyJson.Str(root, "name"));
    }
}
