using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>
/// SMART (Tapo) JSON envelope, as in python-kasa <c>protocols/smartprotocol.py</c>:
/// request <c>{"method":"get_device_info","request_time_milis":..., "terminal_uuid":"..."}</c>,
/// answer <c>{"error_code":0,"result":{...}}</c>.
/// </summary>
public static class SmartMessages
{
    private static readonly string TerminalUuid =
        Convert.ToBase64String(MD5.HashData(Guid.NewGuid().ToByteArray()));

    public static string BuildRequest(string method, DateTimeOffset now)
    {
        var obj = new JsonObject
        {
            ["method"] = method,
            ["request_time_milis"] = now.ToUnixTimeMilliseconds(),
            ["terminal_uuid"] = TerminalUuid,
        };
        return obj.ToJsonString();
    }

    /// <summary>Returns a detached copy of <c>result</c>; throws <see cref="KasaProtocolException"/> on error_code != 0.</summary>
    public static JsonElement ParseResult(string responseJson, string method)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var code = root.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;
        if (code != 0)
            throw new KasaProtocolException($"Device returned error_code {code} for {method}.", code, method);
        if (!root.TryGetProperty("result", out var result))
            throw new KasaProtocolException($"Answer to {method} has no result.", operation: method);
        return result.Clone();
    }

    /// <summary>Maps <c>get_device_info</c> (nickname is base64 on Tapo firmware).</summary>
    public static PlugInfo MapDeviceInfo(JsonElement r)
    {
        var mac = Str(r, "mac") ?? throw new KasaProtocolException("get_device_info missing mac", operation: "get_device_info");
        return new PlugInfo(
            Mac: MacFormat.Normalize(mac),
            Alias: DecodeNickname(Str(r, "nickname")) ?? "",
            Model: Str(r, "model") ?? "",
            HwVer: Str(r, "hw_ver") ?? "",
            SwVer: Str(r, "fw_ver") ?? "",
            OemId: Str(r, "oem_id") ?? "",
            RelayOn: r.TryGetProperty("device_on", out var on) && on.ValueKind == JsonValueKind.True,
            RssiDbm: r.TryGetProperty("rssi", out var rssi) && rssi.ValueKind == JsonValueKind.Number ? rssi.GetInt32() : 0,
            OnTime: TimeSpan.FromSeconds(r.TryGetProperty("on_time", out var ot) && ot.ValueKind == JsonValueKind.Number ? ot.GetInt64() : 0));
    }

    /// <summary>
    /// Maps <c>get_energy_usage</c> (current_power in mW, month_energy in Wh, month to date) and, when the device answers it,
    /// <c>get_emeter_data</c> (voltage_mv, current_ma, power_mw) — the same sources python-kasa's
    /// <c>smart/modules/energy.py</c> reads. Without emeter data voltage/current stay null.
    /// </summary>
    public static EmeterReading MapEnergyUsage(JsonElement energyUsage, JsonElement? emeterData = null)
    {
        long? power = Long(energyUsage, "current_power");
        if (power is null && emeterData is { } e1) power = Long(e1, "power_mw");
        if (power is null)
            throw new KasaProtocolException("get_energy_usage missing current_power", operation: "get_energy_usage");
        var month = Long(energyUsage, "month_energy") ?? 0;
        long? voltage = emeterData is { } e2 ? Long(e2, "voltage_mv") : null;
        long? current = emeterData is { } e3 ? Long(e3, "current_ma") : null;
        return new EmeterReading(PowerMw: power.Value, VoltageMv: voltage, CurrentMa: current, TotalWh: month);
    }

    private static long? Long(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            ? (long)el.GetDouble()
            : null;

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static string? DecodeNickname(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw)); }
        catch (FormatException) { return raw; }
    }
}
