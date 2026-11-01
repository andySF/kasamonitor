using System.Globalization;
using System.Text.Json;

namespace KasaMonitor.Core.Heating;

/// <summary>
/// Maps the bulk features answer (<c>data[]</c> with <c>feature</c>, <c>deviceId</c>, <c>properties{name:{type,value,unit}}</c>,
/// <c>timestamp</c>) onto <see cref="HeatingSnapshot"/>. The mapping follows the real dump of a Vitodens 050-W (B0KA,
/// 2026-11-01) and the PyViCare names for the features other boilers have (outside/return/boiler sensors, day[] series).
/// Device "0" is the boiler, "zigbee-…" a room sensor, "TCU"/missing the gateway. Unknown features are not an error:
/// they stay in the raw dump and are listed in <see cref="HeatingSnapshot.Unmapped"/>.
/// </summary>
public static class HeatingFeatureParser
{
    /// <summary>Reference identity (a HashSet of the used features must not compare JSON values).</summary>
    private sealed class Feature
    {
        public Feature(string name, string? deviceId, JsonElement properties, DateTimeOffset? ts)
        {
            Name = name;
            DeviceId = deviceId;
            Properties = properties;
            Ts = ts;
        }

        public string Name { get; }
        public string? DeviceId { get; }
        public JsonElement Properties { get; }
        public DateTimeOffset? Ts { get; }
    }

    public static HeatingSnapshot Parse(string json, DateTimeOffset readUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var features = new List<Feature>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in data.EnumerateArray())
            {
                if (!f.TryGetProperty("feature", out var n) || n.ValueKind != JsonValueKind.String) continue;
                var deviceId = f.TryGetProperty("deviceId", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                var props = f.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : default;
                features.Add(new Feature(n.GetString()!, deviceId, props, Timestamp(f, "timestamp")));
            }
        }

        var used = new HashSet<Feature>();
        Feature? Main(string name)
        {
            // The boiler's features ("0") first, then gateway-level ones (no deviceId / TCU); never a Zigbee sensor.
            var f = features.FirstOrDefault(x => x.Name == name && x.DeviceId == "0")
                    ?? features.FirstOrDefault(x => x.Name == name && !IsZigbee(x.DeviceId));
            if (f is not null) used.Add(f);
            return f;
        }
        Feature? Zigbee(string name)
        {
            var f = features.FirstOrDefault(x => x.Name == name && IsZigbee(x.DeviceId));
            if (f is not null) used.Add(f);
            return f;
        }

        double? Sensor(string name) => SensorValue(Main(name));
        double? Number(string name, string prop = "value") => Num(Main(name), prop);
        string? Text(string name, string prop = "value") => Str(Main(name), prop);
        bool? Flag(string name, string prop = "active") => Bool(Main(name), prop);

        // Temperatures. commonSupply and circuit 0 supply are the same sensor on B0KA; read both (both "mapped").
        var commonSupply = Sensor("heating.boiler.sensors.temperature.commonSupply");
        var circuitSupply = Sensor("heating.circuits.0.sensors.temperature.supply");
        var dhwCylinder = Sensor("heating.dhw.sensors.temperature.dhwCylinder");
        var dhwStorage = Sensor("heating.dhw.sensors.temperature.hotWaterStorage");
        var dhwOutlet = Sensor("heating.dhw.sensors.temperature.outlet");

        var programTemps = new Dictionary<string, double>();
        foreach (var program in new[] { "normal", "comfort", "reduced" })
            if (Number($"heating.circuits.0.operating.programs.{program}", "temperature") is { } t)
                programTemps[program] = t;

        var circuitName = Text("heating.circuits.0.name", "name");
        var circuitNameOld = Text("heating.circuits.0", "name");

        var messages = new List<HeatingMessage>();
        foreach (var (source, feature) in new[] { ("status", "device.messages.status.raw"), ("service", "device.messages.service.raw"),
                     ("info", "device.messages.info.raw"), ("error", "device.messages.errors.raw") })
            messages.AddRange(Messages(Main(feature), source));
        messages.Sort((a, b) => Nullable.Compare(b.Ts, a.Ts));
        var lockActive = Flag("device.lock.malfunction");
        var activeError = messages.FirstOrDefault(m => m.Source != "info" && !IsStatusPriority(m.Priority))?.Code
                          ?? (lockActive == true ? "blocare" : null);
        var statusCode = messages.FirstOrDefault(m => m.Source == "status" && IsStatusPriority(m.Priority))?.Code;

        var wifi = Number("gateway.wifi", "strength");
        var tcuWifi = Number("tcu.wifi", "strength");

        var burnerActive = Flag("heating.burners.0") ?? Flag("heating.burner");

        var snapshot = new HeatingSnapshot
        {
            ReadUtc = readUtc,
            BoilerTempC = Sensor("heating.boiler.sensors.temperature.main"),
            BoilerTargetC = Number("heating.boiler.temperature"),
            SupplyTempC = commonSupply ?? circuitSupply,
            ReturnTempC = Sensor("heating.sensors.temperature.return"),
            OutsideTempC = Sensor("heating.sensors.temperature.outside"),
            RoomTempC = Sensor("heating.circuits.0.sensors.temperature.room"),
            DhwTempC = dhwCylinder ?? dhwStorage ?? dhwOutlet,
            DhwTargetC = Number("heating.dhw.temperature.main"),
            BurnerActive = burnerActive,
            ModulationPct = Number("heating.burners.0.modulation"),
            PumpPct = Number("heating.boiler.pumps.internal.target"),
            PressureBar = Sensor("heating.sensors.pressure.supply"),
            BurnerStarts = Number("heating.burners.0.statistics", "starts") is { } starts ? (long)Math.Round(starts) : null,
            BurnerHours = Num(Main("heating.burners.0.statistics"), "hours"),
            OperatingMode = Text("heating.circuits.0.operating.modes.active"),
            ActiveProgram = Text("heating.circuits.0.operating.programs.active"),
            ProgramTemps = programTemps,
            CircuitName = circuitName ?? circuitNameOld,
            Schedule = Schedule(Main("heating.circuits.0.heating.schedule")),
            FrostProtection = Text("heating.circuits.0.frostprotection", "status"),
            DhwMode = Text("heating.dhw.operating.modes.active"),
            DhwActive = Flag("heating.dhw"),
            DhwStatus = Str(Main("heating.dhw"), "status"),
            DhwComfort = Flag("heating.dhw.comfort"),
            DhwOneTimeCharge = Flag("heating.dhw.oneTimeCharge"),
            DhwScaldProtection = Flag("heating.dhw.scaldProtection"),
            MalfunctionLock = lockActive,
            Messages = messages,
            ActiveError = activeError,
            StatusCode = statusCode,
            GasHeating = Energy(Main("heating.gas.consumption.summary.heating"), Main("heating.gas.consumption.heating")),
            GasDhw = Energy(Main("heating.gas.consumption.summary.dhw"), Main("heating.gas.consumption.dhw")),
            HeatHeating = Energy(Main("heating.heat.production.summary.heating"), Main("heating.heat.production.heating")),
            HeatDhw = Energy(Main("heating.heat.production.summary.dhw"), Main("heating.heat.production.dhw")),
            PowerHeating = Energy(Main("heating.power.consumption.summary.heating"), Main("heating.power.consumption.heating")),
            PowerDhw = Energy(Main("heating.power.consumption.summary.dhw"), Main("heating.power.consumption.dhw")),
            WifiDbm = wifi ?? tcuWifi,
            RoomSensorBatteryPct = Num(Zigbee("device.power.battery"), "level"),
            RoomSensorLqiPct = Num(Zigbee("device.zigbee.lqi"), "strength"),
            GasType = Text("heating.configuration.gasType"),
            DaylightSaving = Flag("device.time.daylightSaving"),
            FeatureCount = features.Count,
            Unmapped = features.Where(f => !used.Contains(f)).Select(f => new HeatingFeatureRef(f.Name, f.DeviceId)).ToList(),
        };
        return snapshot;
    }

    public static EnergyUnit ParseUnit(string? unit) => unit?.Trim().ToLowerInvariant() switch
    {
        "cubicmeter" or "cubicmeters" or "m3" or "m³" => EnergyUnit.CubicMeter,
        "kilowatthour" or "kilowatthours" or "kwh" => EnergyUnit.KilowattHour,
        _ => EnergyUnit.Unknown,
    };

    private static bool IsZigbee(string? deviceId) => deviceId?.StartsWith("zigbee-", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsStatusPriority(string? priority) => string.Equals(priority, "status", StringComparison.OrdinalIgnoreCase);

    /// <summary>A sensor value only when its <c>status</c> is "connected" (or absent): <c>notConnected</c>/<c>error</c> → null.</summary>
    private static double? SensorValue(Feature? f)
    {
        if (f is null) return null;
        var status = Str(f, "status");
        if (status is not null && !status.Equals("connected", StringComparison.OrdinalIgnoreCase)) return null;
        return Num(f, "value");
    }

    private static bool TryProp(Feature? f, string prop, out JsonElement value)
    {
        value = default;
        return f is not null && f.Properties.ValueKind == JsonValueKind.Object
               && f.Properties.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.Object
               && p.TryGetProperty("value", out value);
    }

    private static double? Num(Feature? f, string prop) =>
        TryProp(f, prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Str(Feature? f, string prop) =>
        TryProp(f, prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(Feature? f, string prop) =>
        TryProp(f, prop, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string? Unit(Feature f, string prop)
    {
        if (f.Properties.ValueKind != JsonValueKind.Object || !f.Properties.TryGetProperty(prop, out var p) || p.ValueKind != JsonValueKind.Object)
            return null;
        if (p.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String && u.GetString() is { Length: > 0 } s) return s;
        return null;
    }

    /// <summary>
    /// A summary feature (<c>currentDay</c>, <c>lastSevenDays</c>, <c>currentMonth</c>, <c>lastMonth</c>, <c>currentYear</c>,
    /// <c>lastYear</c>) and/or a series feature (<c>day[]</c>, <c>week[]</c>, <c>month[]</c>, <c>year[]</c>; index 0 = current).
    /// Missing summary values are derived from the series. The unit comes from the properties' <c>unit</c> (or a
    /// <c>unit</c> property of older answers).
    /// </summary>
    private static EnergySummary? Energy(Feature? summary, Feature? series)
    {
        if (summary is null && series is null) return null;
        IReadOnlyList<double>? SeriesOf(string prop)
        {
            if (!TryProp(series, prop, out var v) || v.ValueKind != JsonValueKind.Array) return null;
            return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => x.GetDouble()).ToList();
        }
        var days = SeriesOf("day");
        var months = SeriesOf("month");
        var years = SeriesOf("year");
        double? At(IReadOnlyList<double>? list, int i) => list is not null && list.Count > i ? list[i] : null;

        string? unit = null;
        if (summary is not null)
            foreach (var p in new[] { "currentDay", "currentMonth", "currentYear", "lastSevenDays", "lastMonth", "lastYear" })
                unit ??= Unit(summary, p);
        if (series is not null)
            unit ??= Unit(series, "day") ?? Unit(series, "month") ?? Unit(series, "year") ?? Str(series, "unit");

        return new EnergySummary(
            Num(summary, "currentDay") ?? At(days, 0),
            Num(summary, "lastSevenDays") ?? (days is { Count: >= 7 } ? days.Take(7).Sum() : null),
            Num(summary, "currentMonth") ?? At(months, 0),
            Num(summary, "lastMonth") ?? At(months, 1),
            Num(summary, "currentYear") ?? At(years, 0),
            Num(summary, "lastYear") ?? At(years, 1),
            ParseUnit(unit),
            summary?.Ts ?? series?.Ts,
            days,
            days is null ? null : TimestampValue(series, "dayValueReadAt") ?? series!.Ts);
    }

    private static IEnumerable<HeatingMessage> Messages(Feature? f, string source)
    {
        if (!TryProp(f, "entries", out var entries) || entries.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var code = e.TryGetProperty("errorCode", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (string.IsNullOrEmpty(code)) continue;
            var priority = e.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            yield return new HeatingMessage(source, code, Timestamp(e, "timestamp"), priority);
        }
    }

    private static IReadOnlyList<HeatingScheduleSlot> Schedule(Feature? f)
    {
        var slots = new List<HeatingScheduleSlot>();
        if (!TryProp(f, "entries", out var entries) || entries.ValueKind != JsonValueKind.Object) return slots;
        foreach (var day in entries.EnumerateObject())
        {
            if (day.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var s in day.Value.EnumerateArray())
            {
                string Get(string n) => s.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                slots.Add(new HeatingScheduleSlot(day.Name, Get("mode"), Get("start"), Get("end")));
            }
        }
        return slots;
    }

    private static DateTimeOffset? TimestampValue(Feature? f, string prop) =>
        TryProp(f, prop, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts : null;

    private static DateTimeOffset? Timestamp(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts) ? ts : null;
}
