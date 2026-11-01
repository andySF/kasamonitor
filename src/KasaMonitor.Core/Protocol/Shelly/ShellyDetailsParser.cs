using System.Globalization;
using System.Text.Json;
using KasaMonitor.Core.Models;
using S = KasaMonitor.Core.Models.DeviceDetails.Sections;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>
/// Details card of a Shelly channel from <c>Shelly.GetDeviceInfo</c>, <c>Shelly.GetStatus</c> and <c>Shelly.GetConfig</c>
/// (each optional: a failed request only removes its items). Labels match the other protocols' cards.
/// </summary>
public static class ShellyDetailsParser
{
    public static DeviceDetails Parse(ShellyDeviceInfo? info, JsonElement? status, JsonElement? config, int channel,
        IEnumerable<string> errors, DateTimeOffset readAtUtc)
    {
        var items = new List<DeviceDetailItem>();
        void Add(string section, string label, string? value) { if (!string.IsNullOrEmpty(value)) items.Add(new(section, label, value)); }

        var sys = ShellyJson.Obj(status, "sys");
        // Metered switches (Plus 2PM, Pro 4PM): each channel has its own relay. Meters (em1/em): the relay belongs to
        // the device and is switch:0.
        var relayId = status is { } st && ShellyStatusParser.Parse(st).Kind == ShellyMeterKind.Switch ? channel : 0;
        var sw0 = ShellyJson.Component(status, "switch", relayId);
        var sw0Config = ShellyJson.Component(config, "switch", relayId);

        // State
        if (sw0 is { } s0)
            Add(S.State, "Relay", ShellyJson.Bool(s0, "output") switch { true => "on", false => "off", _ => null });
        Add(S.State, "Uptime", ShellyJson.Num(sys, "uptime") is { } up ? Duration((long)up) : null);
        if (sw0Config is { } c0)
        {
            Add(S.State, "Default state after power loss", ShellyJson.Str(c0, "initial_state") switch
            {
                "on" => "on",
                "off" => "off",
                "restore_last" => "last state",
                "match_input" => "match input",
                { } other => other,
                null => null,
            });
            Add(S.Timer, "Auto-on", AutoText(ShellyJson.Bool(c0, "auto_on"), ShellyJson.Num(c0, "auto_on_delay")));
            Add(S.Timer, "Auto-off", AutoText(ShellyJson.Bool(c0, "auto_off"), ShellyJson.Num(c0, "auto_off_delay")));
        }
        var temp = ShellyJson.Num(ShellyJson.Obj(sw0, "temperature"), "tC")
                   ?? ShellyJson.Num(ShellyJson.Obj(ShellyJson.Component(status, "pm1", channel), "temperature"), "tC");
        Add(S.State, "Temperature", temp is { } t ? $"{t.ToString("0.0", CultureInfo.InvariantCulture)} °C" : null);

        // Meter (the channel's own measuring component)
        var em1Config = ShellyJson.Component(config, "em1", channel) ?? ShellyJson.Component(config, "em", channel);
        if (em1Config is { } emc)
        {
            Add(S.Meter, "Channel", channel.ToString(CultureInfo.InvariantCulture));
            Add(S.Meter, "Channel name", ShellyJson.Str(emc, "name"));
            Add(S.Meter, "CT type", ShellyJson.Str(emc, "ct_type"));
            Add(S.Meter, "Reversed CT", ShellyJson.Bool(emc, "reverse") switch { true => "yes", false => "no", _ => null });
        }
        var em1 = ShellyJson.Component(status, "em1", channel);
        if (em1 is { } e)
        {
            Add(S.Meter, "Power factor", ShellyJson.Num(e, "pf")?.ToString("0.00", CultureInfo.InvariantCulture));
            Add(S.Meter, "Apparent power", ShellyJson.Num(e, "aprt_power") is { } va ? $"{va.ToString("0.0", CultureInfo.InvariantCulture)} VA" : null);
            Add(S.Meter, "Frequency", ShellyJson.Num(e, "freq") is { } f ? $"{f.ToString("0.0", CultureInfo.InvariantCulture)} Hz" : null);
            Add(S.Meter, "Calibration", ShellyJson.Str(e, "calibration"));
        }
        var em1Data = ShellyJson.Component(status, "em1data", channel);
        if (em1Data is { } d)
        {
            Add(S.Meter, "Energy consumed (device counter)", ShellyJson.Num(d, "total_act_energy") is { } wh ? Kwh(wh) : null);
            Add(S.Meter, "Energy returned (device counter)", ShellyJson.Num(d, "total_act_ret_energy") is { } rwh ? Kwh(rwh) : null);
        }

        // Network
        var wifi = ShellyJson.Obj(status, "wifi");
        Add(S.Network, "SSID", ShellyJson.Str(wifi, "ssid"));
        Add(S.Network, "Wi-Fi signal", ShellyJson.Num(wifi, "rssi") is { } rssi ? $"{rssi:0} dBm" : null);
        Add(S.Network, "IP", ShellyJson.Str(wifi, "sta_ip"));
        Add(S.Network, "Cloud", ShellyJson.Bool(ShellyJson.Obj(status, "cloud"), "connected") switch { true => "connected", false => "not connected", _ => null });
        Add(S.Network, "MQTT", ShellyJson.Bool(ShellyJson.Obj(status, "mqtt"), "connected") switch { true => "connected", false => "not connected", _ => null });

        // Device
        if (info is not null)
        {
            Add(S.Device, "Model", $"{ShellySupport.FriendlyModel(info.Model, info.App)}, {info.Model}");
            Add(S.Device, "Generation", $"Gen{info.Gen}");
            Add(S.Device, "Firmware", info.Version);
            Add(S.Device, "Device id", info.Id);
            Add(S.Device, "Authentication", info.AuthEnabled ? "enabled" : "disabled");
        }
        var update = ShellyJson.Str(ShellyJson.Obj(ShellyJson.Obj(sys, "available_updates"), "stable"), "version");
        Add(S.Device, "Update available", update);
        Add(S.Device, "Restart required", ShellyJson.Bool(sys, "restart_required") is true ? "yes" : null);

        // Time
        Add(S.Time, "Device time zone", ShellyJson.Str(ShellyJson.Obj(ShellyJson.Obj(config, "sys"), "location"), "tz"));
        if (ShellyJson.Num(sys, "unixtime") is { } unix)
        {
            var offset = TimeSpan.FromSeconds(ShellyJson.Num(sys, "utc_offset") ?? 0);
            Add(S.Time, "Device time", DateTimeOffset.FromUnixTimeSeconds((long)unix).ToOffset(offset)
                .ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        }

        return new DeviceDetails(readAtUtc, items, errors.ToList());
    }

    private static string? AutoText(bool? enabled, double? delaySeconds) => enabled switch
    {
        true => delaySeconds is { } d ? $"enabled, after {Duration((long)d)}" : "enabled",
        false => "disabled",
        _ => null,
    };

    private static string Kwh(double wh) => $"{(wh / 1000).ToString("0.000", CultureInfo.InvariantCulture)} kWh";

    private static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min"
             : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min"
             : t.TotalMinutes >= 1 ? $"{t.Minutes} min {t.Seconds} s"
             : $"{t.Seconds} s";
    }
}
