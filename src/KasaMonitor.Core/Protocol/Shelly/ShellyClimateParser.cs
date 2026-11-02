using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>One temperature sensor of a Shelly device (<c>temperature:N</c>, with <c>humidity:N</c> of the same id when present).</summary>
public sealed record ShellyClimateChannel(int Id, double? TemperatureC, double? HumidityPercent, string? Name);

/// <summary>
/// The climate components of a <c>Shelly.GetStatus</c> answer: the sensors, the battery (<c>devicepower:0.battery.percent</c>)
/// and whether external power is present (<c>devicepower:0.external.present</c>; null when the device has no devicepower).
/// </summary>
public sealed record ShellyClimateStatus(IReadOnlyList<ShellyClimateChannel> Channels, int? BatteryPercent, bool? ExternalPower)
{
    /// <summary>Runs on batteries (H&amp;T Gen3 without USB): it sleeps and answers only when it wakes.</summary>
    public bool OnBattery => BatteryPercent is not null && ExternalPower != true;
}

/// <summary>
/// Climate components of Shelly Gen2+ devices, from the Gen2+ API docs (not yet verified on hardware):
/// <c>Temperature.GetStatus</c> → <c>{"id":0,"tC":21.5,"tF":70.7}</c> (<c>tC</c> null and <c>errors</c> when the probe
/// fails) and <c>Humidity.GetStatus</c> → <c>{"id":0,"rh":45.3}</c>. The H&amp;T Gen3 has <c>temperature:0</c>,
/// <c>humidity:0</c> and <c>devicepower:0</c>; the Plus Add-on adds <c>temperature:100…104</c> (DS18B20) or
/// <c>temperature:100</c> + <c>humidity:100</c> (DHT22). A humidity without a temperature of the same id is ignored
/// (a stored reading always has a temperature).
/// </summary>
public static class ShellyClimateParser
{
    /// <param name="config">Answer of <c>Shelly.GetConfig</c> (component names); optional.</param>
    public static ShellyClimateStatus Parse(JsonElement status, JsonElement? config = null)
    {
        var temps = new SortedDictionary<int, JsonElement>();
        var hums = new Dictionary<int, JsonElement>();
        JsonElement? power = null;
        foreach (var (type, id, value) in ShellyJson.Components(status))
        {
            if (value.ValueKind != JsonValueKind.Object) continue;
            switch (type)
            {
                case "temperature": temps[id] = value; break;
                case "humidity": hums[id] = value; break;
                case "devicepower" when power is null: power = value; break;
            }
        }

        var channels = temps.Select(t =>
        {
            var rh = hums.TryGetValue(t.Key, out var h) ? ShellyJson.Num(h, "rh") : null;
            var name = ShellyJson.Str(ShellyJson.Component(config, "temperature", t.Key), "name")
                       ?? ShellyJson.Str(ShellyJson.Component(config, "humidity", t.Key), "name");
            var tc = ShellyJson.Num(t.Value, "tC");
            return new ShellyClimateChannel(t.Key, tc is { } c ? Math.Round(c, 2) : null, rh is { } r ? Math.Round(r, 1) : null,
                string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        }).ToList();

        var battery = ShellyJson.Num(ShellyJson.Obj(power, "battery"), "percent");
        var external = ShellyJson.Bool(ShellyJson.Obj(power, "external"), "present");
        return new ShellyClimateStatus(channels, battery is { } b ? (int)Math.Round(Math.Clamp(b, 0, 100)) : null, external);
    }

    /// <summary>The device's own name: <c>sys.device.name</c> of the config, else the GetDeviceInfo name, id or MAC.</summary>
    public static string DeviceName(ShellyDeviceInfo info, JsonElement? config)
    {
        var sysName = ShellyJson.Str(ShellyJson.Obj(ShellyJson.Obj(config, "sys"), "device"), "name");
        foreach (var candidate in new[] { sysName, info.Name, info.Id, info.Mac })
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate.Trim();
        return "Shelly";
    }

    /// <summary>Default name of a sensor: the component's name, else the device name (+ the id when the device has several).</summary>
    public static string SensorName(ShellyDeviceInfo info, JsonElement? config, ShellyClimateChannel channel, bool many) =>
        channel.Name ?? (many ? $"{DeviceName(info, config)} #{channel.Id}" : DeviceName(info, config));

    /// <summary>Identity of a sensor: MAC (normalized) + component id.</summary>
    public static string ExternalId(string mac, int id) => mac + ":" + id;
}
