using System.Globalization;
using System.Text;
using System.Text.Json;
using KasaMonitor.Core.Models;
using S = KasaMonitor.Core.Models.DeviceDetails.Sections;

namespace KasaMonitor.Core.Protocol;

/// <summary>
/// Turns the raw answers of the device-settings requests into display items. Method and field names follow the
/// TP-Link local APIs as exercised by python-kasa (smart/modules: auto_off, power_protection, led, time, cloud,
/// device; iot: system, time, cnCloud, schedule, anti_theft). Missing fields are skipped, never guessed.
/// </summary>
public static class DeviceDetailsParsers
{
    public const string SmartDeviceInfo = "get_device_info";
    public const string SmartAutoOff = "get_auto_off_config";
    public const string SmartProtection = "get_protection_power";
    public const string SmartMaxPower = "get_max_power";
    public const string SmartLed = "get_led_info";
    public const string SmartTime = "get_device_time";
    public const string SmartCloud = "get_connect_cloud_state";
    public const string SmartCountdown = "get_countdown_rules";
    public const string SmartSchedule = "get_schedule_rules";

    /// <summary>Requests sent to a Tapo SMART plug when its details card is opened.</summary>
    public static readonly IReadOnlyList<string> SmartMethods = new[]
    {
        SmartDeviceInfo, SmartAutoOff, SmartProtection, SmartMaxPower, SmartLed, SmartTime, SmartCloud, SmartCountdown, SmartSchedule,
    };

    /// <summary>One legacy (port 9999) request for all modules; modules a device lacks answer with err_code.</summary>
    public const string LegacyDetailsRequest =
        "{\"system\":{\"get_sysinfo\":{}},\"time\":{\"get_time\":{},\"get_timezone\":{}},\"cnCloud\":{\"get_info\":{}}," +
        "\"schedule\":{\"get_next_action\":{},\"get_rules\":{}},\"anti_theft\":{\"get_rules\":{}}}";

    /// <param name="results">Successful <c>result</c> objects by method name.</param>
    /// <param name="errors">Methods that failed, with the reason (shown as-is).</param>
    public static DeviceDetails ParseSmart(IReadOnlyDictionary<string, JsonElement> results, IEnumerable<string> errors, DateTimeOffset readAtUtc)
    {
        var items = new List<DeviceDetailItem>();
        void Add(string section, string label, string? value) { if (!string.IsNullOrEmpty(value)) items.Add(new(section, label, value)); }

        if (results.TryGetValue(SmartDeviceInfo, out var di))
        {
            Add(S.State, "Relay", Bool(di, "device_on") switch { true => "on", false => "off", _ => null });
            Add(S.State, "Uptime", Long(di, "on_time") is { } secs ? Duration(secs) : null);
            Add(S.State, "Default state after power loss", DefaultState(di));
            Add(S.State, "Overheated", YesNo(Bool(di, "overheated")));
            Add(S.Protection, "Power protection status", Str(di, "power_protection_status"));
            Add(S.Protection, "Overcurrent status", Str(di, "overcurrent_status"));
            Add(S.Timer, "Auto-off timer running", Str(di, "auto_off_status") is { } st ? (st == "on" ? "yes" : "no") : null);
            if (Str(di, "auto_off_status") == "on" && Long(di, "auto_off_remain_time") is { } remain)
                Add(S.Timer, "Auto-off in", Duration(remain));
            Add(S.Network, "Wi-Fi signal", Long(di, "rssi") is { } rssi
                ? $"{rssi} dBm" + (Long(di, "signal_level") is { } lvl ? $" (level {lvl}/3)" : "") : null);
            Add(S.Network, "SSID", Base64(Str(di, "ssid")));
            Add(S.Network, "IP", Str(di, "ip"));
            Add(S.Device, "Model", Str(di, "model"));
            Add(S.Device, "Firmware", Str(di, "fw_ver"));
            Add(S.Device, "Hardware", Str(di, "hw_ver"));
            Add(S.Time, "Region", Str(di, "region"));
        }

        if (results.TryGetValue(SmartAutoOff, out var ao))
        {
            Add(S.Timer, "Auto-off", Bool(ao, "enable") switch
            {
                true => Long(ao, "delay_min") is { } m ? $"enabled, after {m} min" : "enabled",
                false => "disabled",
                _ => null,
            });
        }

        if (results.TryGetValue(SmartCountdown, out var cd))
        {
            var rules = Array(cd, "rule_list");
            Add(S.Timer, "Countdown", Bool(cd, "enable") switch
            {
                true when rules > 0 => $"enabled ({rules} rule{(rules == 1 ? "" : "s")})",
                true => "enabled, no rule",
                false => "disabled",
                _ => null,
            });
        }

        if (results.TryGetValue(SmartProtection, out var pp))
        {
            var enabled = Bool(pp, "enabled");
            var threshold = Long(pp, "protection_power");
            Add(S.Protection, "Power protection", enabled switch
            {
                true => threshold is > 0 ? $"enabled, {threshold} W" : "enabled",
                false => "disabled",
                _ => null,
            });
        }
        if (results.TryGetValue(SmartMaxPower, out var mp) && Long(mp, "max_power") is { } max)
            Add(S.Protection, "Max. protection threshold", $"{max} W");

        if (results.TryGetValue(SmartLed, out var led))
        {
            Add(S.State, "LED", Str(led, "led_rule") switch
            {
                "always" => "always on",
                "never" => "off",
                "auto" => "night mode (off at night)",
                { } other => other,
                null => YesNo(Bool(led, "led_status"), "on", "off"),
            });
        }

        if (results.TryGetValue(SmartTime, out var tm) && Long(tm, "timestamp") is { } ts)
        {
            var offset = TimeSpan.FromMinutes(Long(tm, "time_diff") ?? 0);
            Add(S.Time, "Device time", DateTimeOffset.FromUnixTimeSeconds(ts).ToOffset(offset).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            if (Str(tm, "region") is not null) items.RemoveAll(i => i.Label == "Region");
            Add(S.Time, "Region", Str(tm, "region"));
        }

        if (results.TryGetValue(SmartCloud, out var cl) && Long(cl, "status") is { } cs)
            Add(S.Network, "Cloud", cs == 0 ? "connected" : $"not connected (status {cs})");

        if (results.TryGetValue(SmartSchedule, out var sc))
        {
            var rules = Array(sc, "rule_list");
            Add(S.Schedules, "Schedules", Bool(sc, "enable") switch
            {
                true => $"enabled, {rules} rule{(rules == 1 ? "" : "s")}",
                false => rules > 0 ? $"disabled ({rules} rules)" : "none",
                _ => null,
            });
        }

        return new DeviceDetails(readAtUtc, items, errors.ToList());
    }

    /// <summary>Parses the answer to <see cref="LegacyDetailsRequest"/>.</summary>
    public static DeviceDetails ParseLegacy(string responseJson, DateTimeOffset readAtUtc)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var items = new List<DeviceDetailItem>();
        var errors = new List<string>();
        void Add(string section, string label, string? value) { if (!string.IsNullOrEmpty(value)) items.Add(new(section, label, value)); }

        JsonElement? Module(string module, string method)
        {
            if (!root.TryGetProperty(module, out var m) || !m.TryGetProperty(method, out var r)) return null;
            if (Long(r, "err_code") is { } ec && ec != 0)
            {
                errors.Add($"{module}.{method}: {(Str(r, "err_msg") ?? "error")} ({ec})");
                return null;
            }
            return r;
        }

        if (Module("system", "get_sysinfo") is { } si)
        {
            Add(S.State, "Relay", Long(si, "relay_state") switch { 1 => "on", 0 => "off", _ => null });
            Add(S.State, "Uptime", Long(si, "on_time") is { } secs && Long(si, "relay_state") == 1 ? Duration(secs) : null);
            Add(S.State, "LED", Long(si, "led_off") switch { 0 => "on", 1 => "off", _ => null });
            Add(S.Network, "Wi-Fi signal", Long(si, "rssi") is { } rssi ? $"{rssi} dBm" : null);
            Add(S.Device, "Model", Str(si, "model"));
            Add(S.Device, "Firmware", Str(si, "sw_ver"));
            Add(S.Device, "Hardware", Str(si, "hw_ver"));
            Add(S.Device, "Updating", Long(si, "updating") is 1 ? "yes" : null);
            if (si.TryGetProperty("next_action", out var na)) Add(S.Schedules, "Next action", NextAction(na));
        }

        if (Module("time", "get_time") is { } t
            && Long(t, "year") is { } y && Long(t, "month") is { } mo && Long(t, "mday") is { } d
            && Long(t, "hour") is { } h && Long(t, "min") is { } mi && Long(t, "sec") is { } s)
            Add(S.Time, "Device time", $"{y:D4}-{mo:D2}-{d:D2} {h:D2}:{mi:D2}:{s:D2}");
        if (Module("time", "get_timezone") is { } tz && Long(tz, "index") is { } idx)
            Add(S.Time, "Time zone index", idx.ToString(CultureInfo.InvariantCulture));

        if (Module("cnCloud", "get_info") is { } cloud)
            Add(S.Network, "Cloud", Long(cloud, "cld_connection") switch
            {
                1 => Long(cloud, "binded") == 1 ? "connected (bound to an account)" : "connected",
                0 => "not connected",
                _ => null,
            });

        if (Module("schedule", "get_rules") is { } sch)
        {
            var rules = Array(sch, "rule_list");
            Add(S.Schedules, "Schedules", Long(sch, "enable") switch
            {
                1 => $"enabled, {rules} rule{(rules == 1 ? "" : "s")}",
                0 => rules > 0 ? $"disabled ({rules} rules)" : "none",
                _ => null,
            });
        }
        if (Module("schedule", "get_next_action") is { } next && !items.Any(i => i.Label == "Next action"))
            Add(S.Schedules, "Next action", NextAction(next));

        if (Module("anti_theft", "get_rules") is { } at)
        {
            var rules = Array(at, "rule_list");
            Add(S.Schedules, "Away mode", Long(at, "enable") == 1 ? $"enabled ({rules} rules)" : "disabled");
        }

        return new DeviceDetails(readAtUtc, items, errors);
    }

    private static string? NextAction(JsonElement na)
    {
        var type = Long(na, "type");
        if (type is null or -1) return "none";
        var action = Long(na, "action") switch { 1 => "turn on", 0 => "turn off", _ => "action" };
        return Long(na, "schd_sec") is { } sec ? $"{action} at {TimeSpan.FromSeconds(sec):hh\\:mm}" : action;
    }

    private static string? DefaultState(JsonElement di)
    {
        if (!di.TryGetProperty("default_states", out var ds) || ds.ValueKind != JsonValueKind.Object) return null;
        return Str(ds, "type") switch
        {
            "last_states" => "last state",
            "custom" => ds.TryGetProperty("state", out var st) && Bool(st, "on") is { } on ? (on ? "on" : "off") : "custom",
            { } other => other,
            null => null,
        };
    }

    private static string Duration(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays} d {t.Hours} h {t.Minutes} min"
             : t.TotalHours >= 1 ? $"{t.Hours} h {t.Minutes} min"
             : $"{t.Minutes} min {t.Seconds} s";
    }

    private static string? YesNo(bool? b, string yes = "yes", string no = "no") => b switch { true => yes, false => no, _ => null };

    private static string? Base64(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw)); }
        catch (FormatException) { return raw; }
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : null;

    private static bool? Bool(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, JsonValueKind.Number => v.GetDouble() != 0, _ => null }
            : null;

    private static int Array(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.GetArrayLength() : 0;
}
