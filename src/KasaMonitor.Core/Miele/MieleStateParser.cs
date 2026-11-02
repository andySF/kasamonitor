using System.Text.Json;

namespace KasaMonitor.Core.Miele;

/// <summary>
/// Parses the answer of <c>GET /v1/devices</c> and the data of a devices SSE event: an object keyed by deviceId whose
/// values hold <c>ident</c> and <c>state</c>, or (single-appliance form) one object with <c>ident</c>/<c>state</c>, whose
/// id is then <c>ident.deviceIdentLabel.fabNumber</c>. Everything is optional: the API returns every possible
/// capability, <c>null</c> when unsupported and <c>-32768</c> when there is no current value; both become null.
/// </summary>
public static class MieleStateParser
{
    public static IReadOnlyList<MieleDeviceState> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = new List<MieleDeviceState>();
        if (root.ValueKind != JsonValueKind.Object) return list;
        if (root.TryGetProperty("ident", out _) || root.TryGetProperty("state", out _))
        {
            var ident = root.TryGetProperty("ident", out var i) ? i : default;
            var id = Str(Obj(ident, "deviceIdentLabel"), "fabNumber");
            if (!string.IsNullOrEmpty(id)) list.Add(ParseDevice(id, root));
            return list;
        }
        foreach (var p in root.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(p.Name)) continue;
            if (!p.Value.TryGetProperty("ident", out _) && !p.Value.TryGetProperty("state", out _)) continue;
            list.Add(ParseDevice(p.Name, p.Value));
        }
        return list;
    }

    /// <summary>One appliance: <paramref name="device"/> holds <c>ident</c> and/or <c>state</c>.</summary>
    public static MieleDeviceState ParseDevice(string deviceId, JsonElement device)
    {
        var ident = Obj(device, "ident");
        MieleIdent? id = ident.ValueKind == JsonValueKind.Object
            ? new MieleIdent(Int(Obj(ident, "type"), "value_raw"), NonEmpty(Str(Obj(ident, "type"), "value_localized")),
                NonEmpty(Str(ident, "deviceName")), NonEmpty(Str(Obj(ident, "deviceIdentLabel"), "techType")))
            : null;
        var s = Obj(device, "state");
        var eco = Obj(s, "ecoFeedback");
        var remote = Obj(s, "remoteEnable");
        return new MieleDeviceState(
            deviceId,
            id,
            Value(Obj(s, "status")),
            Value(Obj(s, "programType")),
            Value(Obj(s, "ProgramID")),
            Value(Obj(s, "programPhase")),
            Duration(s, "remainingTime"),
            Duration(s, "elapsedTime"),
            Duration(s, "startTime"),
            TargetTemperature(s),
            Int(Obj(s, "spinningSpeed"), "value_raw"),
            Int(Obj(s, "dryingStep"), "value_raw"),
            Bool(s, "signalDoor"),
            Bool(s, "signalFailure"),
            Bool(s, "signalInfo"),
            Num(Obj(eco, "currentEnergyConsumption"), "value"),
            Num(Obj(eco, "currentWaterConsumption"), "value"),
            Num(eco, "energyForecast"),
            Num(eco, "waterForecast"),
            Bool(remote, "fullRemoteControl"),
            Bool(remote, "smartGrid"),
            Bool(remote, "mobileStart"));
    }

    private static MieleValue? Value(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return null;
        var raw = Int(o, "value_raw");
        var text = NonEmpty(Str(o, "value_localized"));
        return raw is null && text is null ? null : new MieleValue(raw, text);
    }

    /// <summary><c>[hours, minutes]</c> → seconds; anything else → null.</summary>
    private static int? Duration(JsonElement s, string name)
    {
        var a = Obj(s, name);
        if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() < 2) return null;
        if (!a[0].TryGetInt32(out var h) || !a[1].TryGetInt32(out var m)) return null;
        if (h < 0 || m < 0 || h == MieleApi.NoValue || m == MieleApi.NoValue) return null;
        return h * 3600 + m * 60;
    }

    /// <summary>The first <c>targetTemperature</c> with a value: <c>value_raw</c> is °C × 100.</summary>
    private static double? TargetTemperature(JsonElement s)
    {
        var a = Obj(s, "targetTemperature");
        if (a.ValueKind != JsonValueKind.Array) return null;
        foreach (var t in a.EnumerateArray())
            if (Int(t, "value_raw") is { } raw) return raw / 100.0;
        return null;
    }

    private static JsonElement Obj(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : default;

    private static string? Str(JsonElement o, string name) =>
        Obj(o, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static int? Int(JsonElement o, string name)
    {
        var v = Obj(o, name);
        int? n = v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out var i) => i,
            JsonValueKind.Number when v.TryGetDouble(out var d) && d is >= int.MinValue and <= int.MaxValue => (int)Math.Round(d),
            JsonValueKind.String when int.TryParse(v.GetString(), out var p) => p,
            _ => null,
        };
        return n == MieleApi.NoValue ? null : n;
    }

    private static double? Num(JsonElement o, string name)
    {
        var v = Obj(o, name);
        double? n = v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDouble(out var d) => d,
            JsonValueKind.String when double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
            _ => null,
        };
        return n is { } x && (x == MieleApi.NoValue || double.IsNaN(x)) ? null : n;
    }

    private static bool? Bool(JsonElement o, string name) =>
        Obj(o, name).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
}
