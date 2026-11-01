using System.Text.Json;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

/// <summary>
/// shelly_emg3_*.json: answers of a real Shelly EM Gen3 (S3EM-002CXCEU, fw 1.6.99) read on 2026-11-01, SSID replaced.
/// shelly_plusplugs / pmmini / pro3em: hand-written in the shape of the official Gen2+ component docs (Switch, PM1, EM/EMData).
/// </summary>
internal static class ShellyFixtures
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    public static JsonElement Json(string name)
    {
        using var doc = JsonDocument.Parse(Text(name));
        return doc.RootElement.Clone();
    }

    public static JsonElement EmG3Status => Json("shelly_emg3_status.json");
    public static JsonElement EmG3Config => Json("shelly_emg3_config.json");
    public static JsonElement EmG3DeviceInfo => Json("shelly_emg3_deviceinfo.json");

    public static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
