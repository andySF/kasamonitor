using KasaMonitor.Core.Devices.Entities;

namespace KasaMonitor.Core.Devices.Generic;

/// <summary>
/// What a generic device is (0.11, audit §3): its main category, a secondary label, the other capabilities shown as badges,
/// and the entities routed to the climate section (temperature + its humidity and battery).
/// </summary>
public sealed record GenericClassification(
    DeviceCategory Category,
    string Label,
    IReadOnlyList<string> Capabilities,
    string? TemperatureKey,
    string? HumidityKey,
    string? BatteryKey);

/// <summary>
/// The automatic classification of a generic device from its entities (pure). Rules in priority order, on the entities
/// that are not diagnostic and can be read: power + energy counter → <see cref="DeviceCategory.Meter"/> (sub-meter
/// semantics: never in the house totals); a switch without power → <see cref="DeviceCategory.Relay"/>; a temperature
/// measurement without power → <see cref="DeviceCategory.ClimateSensor"/>; anything else → <see cref="DeviceCategory.Generic"/>,
/// labelled by its dominant class. The user can override the category among <see cref="AllowedOverrides"/>.
/// </summary>
public static class GenericClassifier
{
    /// <summary>„Priză” (totals, NILM) needs a dedicated adapter (demarcation rule): not offered for a generic device.</summary>
    public static readonly IReadOnlyList<DeviceCategory> AllowedOverrides =
        [DeviceCategory.Meter, DeviceCategory.Relay, DeviceCategory.ClimateSensor, DeviceCategory.Generic];

    public static GenericClassification Classify(IReadOnlyList<DeviceEntity> entities)
    {
        var readable = entities.Where(e => e.Problem is null && e.Kind != EntityKind.Unsupported).ToList();
        var primary = readable.Where(e => e.Category == EntityCategory.Primary).ToList();
        var power = primary.FirstOrDefault(e => e.Class == DeviceClass.Power && e.StateClass == StateClass.Measurement);
        var energy = primary.FirstOrDefault(e => e.Class == DeviceClass.Energy && e.StateClass is StateClass.Total or StateClass.TotalIncreasing);
        var relay = primary.FirstOrDefault(e => e.Kind == EntityKind.Switch);
        var temperature = primary.FirstOrDefault(e => e.Kind == EntityKind.Sensor && e.Class == DeviceClass.Temperature && e.StateClass == StateClass.Measurement);
        var humidity = readable.FirstOrDefault(e => e.Kind == EntityKind.Sensor && e.Class == DeviceClass.Humidity);
        var battery = readable.FirstOrDefault(e => e.Kind == EntityKind.Sensor && e.Class == DeviceClass.Battery);

        DeviceCategory category;
        string label;
        if (power is not null && energy is not null) (category, label) = (DeviceCategory.Meter, "Contor");
        else if (relay is not null && power is null) (category, label) = (DeviceCategory.Relay, "Releu");
        else if (temperature is not null && power is null) (category, label) = (DeviceCategory.ClimateSensor, humidity is not null ? "Senzor temperatură și umiditate" : "Senzor temperatură");
        else (category, label) = (DeviceCategory.Generic, Dominant(primary.Count > 0 ? primary : readable));

        var caps = new List<string>();
        void Cap(bool has, string name, params DeviceCategory[] implied)
        {
            if (has && !implied.Contains(category) && !caps.Contains(name)) caps.Add(name);
        }
        Cap(relay is not null, "releu", DeviceCategory.Relay);
        Cap(power is not null, "putere", DeviceCategory.Meter);
        Cap(energy is not null, "energie", DeviceCategory.Meter);
        Cap(temperature is not null, "temperatură", DeviceCategory.ClimateSensor);
        Cap(humidity is not null, "umiditate", DeviceCategory.ClimateSensor);
        Cap(battery is not null, "baterie");
        foreach (var b in primary.Where(e => e.Kind == EntityKind.Binary && e.Class != DeviceClass.None))
            Cap(true, DeviceClasses.Name(b.Class).ToLowerInvariant());
        return new GenericClassification(category, label, caps, temperature?.Key, temperature is null ? null : humidity?.Key, temperature is null ? null : battery?.Key);
    }

    /// <summary>„Senzor ușă”, „Senzor CO₂” …: the most frequent class among the entities (generic when none).</summary>
    private static string Dominant(IReadOnlyList<DeviceEntity> entities)
    {
        var cls = entities.Where(e => e.Class != DeviceClass.None)
            .GroupBy(e => e.Class).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).Select(g => (DeviceClass?)g.Key).FirstOrDefault();
        if (cls is not { } c) return "Alt dispozitiv";
        var name = DeviceClasses.Name(c);
        // „Ușă” → „Senzor ușă”; an acronym keeps its case („Senzor CO₂”, „Senzor PM10”).
        return "Senzor " + (name.Length > 1 && char.IsLower(name[1]) ? char.ToLowerInvariant(name[0]) + name[1..] : name);
    }
}

/// <summary>What the write rule decides for one value.</summary>
public enum GenericWriteDecision
{
    Write,
    /// <summary>Not stored (not newer, no significant change, heartbeat not due).</summary>
    Skip,
    /// <summary>A significant change within 10 s of the last write: kept and stored when the 10 s pass (the last value wins).</summary>
    Defer,
}

/// <summary>The last stored value of an entity.</summary>
public readonly record struct GenericStored(DateTimeOffset TsUtc, double? Number, string? Text);

/// <summary>
/// When a generic value is stored (0.11, after <c>ClimateWriteRule</c>): the first one; a change at least as large as the
/// threshold (the entity's precision, else the class's: 0.1 °C, 1 % RH, max(1 W, 2 %)); a heartbeat every 30 minutes; and
/// never more than one row per entity in 10 s (a change inside the window is deferred, not lost).
/// </summary>
public static class GenericWriteRule
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(30);
    private const double Epsilon = 1e-9;

    public static double Threshold(DeviceEntity e, double? lastValue)
    {
        if (e.DataType != EntityDataType.Number) return 0;
        if (e.Precision is { } p) return Math.Pow(10, -p);
        return e.Class switch
        {
            DeviceClass.Temperature => 0.1,
            DeviceClass.Humidity or DeviceClass.Moisture or DeviceClass.Battery => 1,
            DeviceClass.Power => Math.Max(1, 0.02 * Math.Abs(lastValue ?? 0)),
            DeviceClass.Energy => 1,
            DeviceClass.Voltage => 0.5,
            _ => 0,
        };
    }

    public static GenericWriteDecision Decide(GenericStored? last, DateTimeOffset tsUtc, double? number, string? text, double threshold)
    {
        if (last is not { } l) return GenericWriteDecision.Write;
        if (tsUtc.ToUnixTimeSeconds() <= l.TsUtc.ToUnixTimeSeconds()) return GenericWriteDecision.Skip;
        var changed = number is null != l.Number is null
                      || number is { } n && l.Number is { } ln && Math.Abs(n - ln) >= Math.Max(threshold, Epsilon) - Epsilon
                      || !string.Equals(text, l.Text, StringComparison.Ordinal);
        if (tsUtc - l.TsUtc < MinInterval) return changed ? GenericWriteDecision.Defer : GenericWriteDecision.Skip;
        if (tsUtc - l.TsUtc >= Heartbeat) return GenericWriteDecision.Write;
        return changed ? GenericWriteDecision.Write : GenericWriteDecision.Skip;
    }
}

/// <summary>One stored generic value (table <c>GenericReadings</c>): a number (binary 1/0) or a text.</summary>
public readonly record struct GenericPoint(DateTimeOffset TsUtc, double? Number, string? Text = null);

/// <summary>
/// One entity over one UTC hour (table <c>GenericHourly</c>, kept forever): min, time-weighted mean, max and last value,
/// the number of stored readings and, for a counter (<c>total</c> / <c>total_increasing</c>), what it counted in the hour.
/// For a binary the mean is the share of the hour it was on.
/// </summary>
public sealed record GenericHour(DateTimeOffset HourUtc, double? Min, double? Avg, double? Max, double? Last, int Count, double? Delta);

/// <summary>The hourly rollup of generic readings (0.11, pure).</summary>
public static class GenericRollup
{
    /// <summary>
    /// The closed hours in [<paramref name="fromHour"/>, <paramref name="toHour"/>) of one entity's readings (oldest first),
    /// with the last reading before the range as the starting value. A value holds until the next reading (written on
    /// change), but not longer than <paramref name="maxCarry"/> (a silent device is a gap, not a flat line). An hour with
    /// neither a reading nor a value carried in has no row. Counters: <c>total_increasing</c> sums the increases and
    /// counts a decrease as a reset (the new value is what was counted since); <c>total</c> sums the differences.
    /// </summary>
    public static IReadOnlyList<GenericHour> Hours(IReadOnlyList<GenericPoint> points, GenericPoint? before, StateClass? stateClass,
        DateTimeOffset fromHour, DateTimeOffset toHour, TimeSpan maxCarry)
    {
        var result = new List<GenericHour>();
        var numeric = points.Where(p => p.Number is not null).OrderBy(p => p.TsUtc).ToList();
        var carry = before is { Number: not null } b ? b : (GenericPoint?)null;
        var counter = stateClass is StateClass.Total or StateClass.TotalIncreasing;
        // R5 (round 2): a counter's basis is the last value before, however old: what was counted during a gap goes to the
        // hour of the first reading after it (as Home Assistant's statistics do), not lost.
        var basis = carry?.Number;
        var i = 0;
        while (i < numeric.Count && numeric[i].TsUtc < fromHour)
        {
            carry = numeric[i++];
            if (counter) CounterStep(ref basis, carry.Value.Number!.Value, stateClass);
        }
        for (var h = fromHour; h < toHour; h = h.AddHours(1))
        {
            var end = h.AddHours(1);
            var inHour = new List<GenericPoint>();
            while (i < numeric.Count && numeric[i].TsUtc < end) inHour.Add(numeric[i++]);
            var start = carry is { } c && h - c.TsUtc < maxCarry ? c : (GenericPoint?)null;
            if (inHour.Count == 0 && start is null)
            {
                carry = null;
                continue;
            }

            // Segments of constant value: [h, first) from the carried value, then each reading until the next, the last until
            // the end of the hour — or until it expires (maxCarry after it was read).
            double weighted = 0, seconds = 0;
            var values = new List<double>();
            void Segment(double v, DateTimeOffset from, DateTimeOffset to, DateTimeOffset readAt)
            {
                var stop = Min(to, readAt + maxCarry);
                if (stop <= from) return;
                var s = (stop - from).TotalSeconds;
                weighted += v * s;
                seconds += s;
                values.Add(v);
            }
            if (start is { } st) Segment(st.Number!.Value, h, inHour.Count > 0 ? inHour[0].TsUtc : end, st.TsUtc);
            for (var k = 0; k < inHour.Count; k++)
                Segment(inHour[k].Number!.Value, inHour[k].TsUtc, k + 1 < inHour.Count ? inHour[k + 1].TsUtc : end, inHour[k].TsUtc);
            foreach (var p in inHour) if (!values.Contains(p.Number!.Value)) values.Add(p.Number!.Value);
            // The carried value expired exactly at the start of the hour and nothing was read in it: no row.
            if (values.Count == 0) continue;

            double? delta = null;
            if (counter)
            {
                double d = 0;
                foreach (var p in inHour) d += CounterStep(ref basis, p.Number!.Value, stateClass);
                delta = d;
            }
            var last = inHour.Count > 0 ? inHour[^1] : start!.Value;
            result.Add(new GenericHour(h, values.Min(), seconds > 0 ? weighted / seconds : last.Number, values.Max(), last.Number, inHour.Count, delta));
            carry = last;
        }
        return result;
    }

    /// <summary>R2 (round 2): a <c>total_increasing</c> value under 90 % of the basis is a reset (Home Assistant's rule).</summary>
    public const double ResetFraction = 0.9;

    /// <summary>
    /// What one counter value adds over the basis. <c>total</c>: the difference (it may go down). <c>total_increasing</c>
    /// (R2, round 2): an increase over the basis counts; a value under 90 % of the basis is a reset (the new value is what
    /// was counted since, and becomes the basis); a smaller dip (rounding, kWh with 3 decimals) is ignored and the basis
    /// stays the highest value, so coming back to it is not counted twice. The first value only sets the basis.
    /// </summary>
    public static double CounterStep(ref double? basis, double value, StateClass? stateClass)
    {
        if (basis is not { } b)
        {
            basis = value;
            return 0;
        }
        if (stateClass != StateClass.TotalIncreasing)
        {
            basis = value;
            return value - b;
        }
        if (value >= b)
        {
            basis = value;
            return value - b;
        }
        if (value < ResetFraction * b)
        {
            basis = value;
            return value;
        }
        return 0;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>One /metrics family of the generic entities: a fixed unit per family (R5), a gauge or a counter.</summary>
public sealed record GenericMetricFamily(string Name, string Unit, bool Counter, string Help);

/// <summary>
/// The /metrics family of a generic entity (0.11, R5): one family per class with its canonical unit
/// (<c>entity_temperature_celsius</c>, <c>entity_power_watts</c>, <c>entity_energy_wh_total</c> …), <c>entity_state</c> for
/// on/off, and <c>entity_value</c> with a <c>unit</c> label only for the classes it does not recognise. Never one family
/// with mixed units: a Prometheus metric „MUST refer to a single unit”.
/// </summary>
public static class GenericMetricFamilies
{
    private static GenericMetricFamily G(string name, string unit, string help) => new(name, unit, false, help);

    public static readonly GenericMetricFamily Temperature = G("entity_temperature_celsius", "°C", "Temperature of a generic device's entity.");
    public static readonly GenericMetricFamily Humidity = G("entity_humidity_percent", "%", "Relative humidity of a generic device's entity.");
    public static readonly GenericMetricFamily Moisture = G("entity_moisture_percent", "%", "Moisture of a generic device's entity.");
    public static readonly GenericMetricFamily Power = G("entity_power_watts", "W", "Power of a generic device's entity (never in the house totals).");
    public static readonly GenericMetricFamily ApparentPower = G("entity_apparent_power_va", "VA", "Apparent power of a generic device's entity.");
    public static readonly GenericMetricFamily ReactivePower = G("entity_reactive_power_var", "var", "Reactive power of a generic device's entity.");
    public static readonly GenericMetricFamily Energy = new("entity_energy_wh_total", "Wh", true, "Energy counted by a generic device's entity (counter; device resets skipped).");
    public static readonly GenericMetricFamily Voltage = G("entity_voltage_volts", "V", "Voltage of a generic device's entity.");
    public static readonly GenericMetricFamily Current = G("entity_current_amperes", "A", "Current of a generic device's entity.");
    public static readonly GenericMetricFamily Frequency = G("entity_frequency_hertz", "Hz", "Frequency of a generic device's entity.");
    public static readonly GenericMetricFamily Battery = G("entity_battery_percent", "%", "Battery of a generic device.");
    public static readonly GenericMetricFamily Signal = G("entity_signal_strength_dbm", "dBm", "Signal strength of a generic device.");
    public static readonly GenericMetricFamily Co2 = G("entity_co2_ppm", "ppm", "CO2 concentration of a generic device's entity.");
    public static readonly GenericMetricFamily Co = G("entity_co_ppm", "ppm", "CO concentration of a generic device's entity.");
    public static readonly GenericMetricFamily Pm1 = G("entity_pm1_ugm3", "µg/m³", "PM1 of a generic device's entity.");
    public static readonly GenericMetricFamily Pm25 = G("entity_pm25_ugm3", "µg/m³", "PM2.5 of a generic device's entity.");
    public static readonly GenericMetricFamily Pm10 = G("entity_pm10_ugm3", "µg/m³", "PM10 of a generic device's entity.");
    public static readonly GenericMetricFamily Voc = G("entity_voc_ugm3", "µg/m³", "Volatile organic compounds of a generic device's entity.");
    public static readonly GenericMetricFamily Pressure = G("entity_pressure_hpa", "hPa", "Pressure of a generic device's entity.");
    public static readonly GenericMetricFamily Illuminance = G("entity_illuminance_lux", "lx", "Illuminance of a generic device's entity.");
    public static readonly GenericMetricFamily Duration = G("entity_duration_seconds", "s", "A duration of a generic device's entity.");
    public static readonly GenericMetricFamily State = G("entity_state", "0/1", "On/off state of a generic device's binary entity or switch.");
    public static readonly GenericMetricFamily Value = G("entity_value", "label unit", "Value of a generic entity whose class has no family of its own (label unit).");
    public static readonly GenericMetricFamily Online = G("entity_online", "0/1", "A generic device answers (availability, expiry, or a value in the last hour).");

    public static readonly IReadOnlyList<GenericMetricFamily> All =
    [
        Temperature, Humidity, Moisture, Power, ApparentPower, ReactivePower, Energy, Voltage, Current, Frequency, Battery, Signal,
        Co2, Co, Pm1, Pm25, Pm10, Voc, Pressure, Illuminance, Duration, State, Value, Online,
    ];

    /// <summary>The family of an entity; null for texts and commands without a state (they have no series).</summary>
    public static GenericMetricFamily? For(DeviceEntity e)
    {
        if (e.Problem is not null || e.Kind is EntityKind.Unsupported or EntityKind.Button) return null;
        if (e.DataType == EntityDataType.Boolean) return State;
        if (e.DataType != EntityDataType.Number) return null;
        return e.Class switch
        {
            DeviceClass.Temperature => Temperature,
            DeviceClass.Humidity => Humidity,
            DeviceClass.Moisture => Moisture,
            DeviceClass.Power => Power,
            DeviceClass.ApparentPower => ApparentPower,
            DeviceClass.ReactivePower => ReactivePower,
            DeviceClass.Energy when e.StateClass is StateClass.Total or StateClass.TotalIncreasing => Energy,
            DeviceClass.Voltage => Voltage,
            DeviceClass.Current => Current,
            DeviceClass.Frequency => Frequency,
            DeviceClass.Battery => Battery,
            DeviceClass.SignalStrength => Signal,
            DeviceClass.CarbonDioxide => Co2,
            DeviceClass.CarbonMonoxide => Co,
            DeviceClass.Pm1 => Pm1,
            DeviceClass.Pm25 => Pm25,
            DeviceClass.Pm10 => Pm10,
            DeviceClass.VolatileOrganicCompounds => Voc,
            DeviceClass.Pressure => Pressure,
            DeviceClass.Illuminance => Illuminance,
            DeviceClass.Duration => Duration,
            _ => Value,
        };
    }
}

/// <summary>One bar of the „Alte dispozitive” history: min, mean, max of the hourly values, what a counter counted, hours covered.</summary>
public sealed record GenericBucketStat(double? Min, double? Avg, double? Max, double? Delta, int Hours)
{
    public static readonly GenericBucketStat Empty = new(null, null, null, null, 0);

    /// <summary>A binary's time on in the bar (its hourly mean is the share of the hour it was on).</summary>
    public double? OnSeconds => Avg is { } a ? a * Hours * 3600 : null;
}

/// <summary>The bars of the generic history from the hourly rollups (0.11, pure): measurement → min / mean / max, counter → Σ, binary → time on.</summary>
public static class GenericHistory
{
    public static IReadOnlyList<GenericBucketStat> Aggregate(IReadOnlyList<GenericHour> hours, IReadOnlyList<(DateTimeOffset StartUtc, DateTimeOffset EndUtc)> windows)
    {
        var result = new List<GenericBucketStat>(windows.Count);
        foreach (var (start, end) in windows)
        {
            var inside = hours.Where(h => h.HourUtc >= start && h.HourUtc < end && h.Avg is not null).ToList();
            if (inside.Count == 0)
            {
                result.Add(GenericBucketStat.Empty);
                continue;
            }
            var deltas = inside.Where(h => h.Delta is not null).Select(h => h.Delta!.Value).ToList();
            result.Add(new GenericBucketStat(inside.Min(h => h.Min ?? h.Avg!.Value), inside.Average(h => h.Avg!.Value), inside.Max(h => h.Max ?? h.Avg!.Value),
                deltas.Count > 0 ? deltas.Sum() : null, inside.Count));
        }
        return result;
    }
}
