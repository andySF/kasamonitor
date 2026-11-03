using System.Globalization;

namespace KasaMonitor.Core.Devices.Entities;

/// <summary>A linear unit conversion to the canonical unit: <c>value × Factor + Offset</c>.</summary>
public readonly record struct UnitConversion(double Factor, double Offset = 0)
{
    public static readonly UnitConversion Identity = new(1);

    public double Apply(double v) => v * Factor + Offset;

    public bool IsIdentity => Factor == 1 && Offset == 0;
}

/// <summary>
/// Single owner of the device classes (0.11): the mapping from Home Assistant's <c>device_class</c> names (snake_case, sensor and
/// binary sensor lists of the HA documentation), the canonical unit of each class with the conversions into it
/// (kW→W, kWh→Wh, °F→°C, mA→A, bar→hPa …), the Romanian names and the binary on/off texts.
/// </summary>
public static class DeviceClasses
{
    private static readonly Dictionary<string, DeviceClass> SensorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["temperature"] = DeviceClass.Temperature,
        ["humidity"] = DeviceClass.Humidity,
        ["moisture"] = DeviceClass.Moisture,
        ["power"] = DeviceClass.Power,
        ["apparent_power"] = DeviceClass.ApparentPower,
        ["reactive_power"] = DeviceClass.ReactivePower,
        ["energy"] = DeviceClass.Energy,
        ["reactive_energy"] = DeviceClass.ReactiveEnergy,
        ["voltage"] = DeviceClass.Voltage,
        ["current"] = DeviceClass.Current,
        ["frequency"] = DeviceClass.Frequency,
        ["power_factor"] = DeviceClass.PowerFactor,
        ["battery"] = DeviceClass.Battery,
        ["signal_strength"] = DeviceClass.SignalStrength,
        ["carbon_dioxide"] = DeviceClass.CarbonDioxide,
        ["carbon_monoxide"] = DeviceClass.CarbonMonoxide,
        ["volatile_organic_compounds"] = DeviceClass.VolatileOrganicCompounds,
        ["pm1"] = DeviceClass.Pm1,
        ["pm25"] = DeviceClass.Pm25,
        ["pm10"] = DeviceClass.Pm10,
        ["pressure"] = DeviceClass.Pressure,
        ["atmospheric_pressure"] = DeviceClass.Pressure,
        ["illuminance"] = DeviceClass.Illuminance,
        ["duration"] = DeviceClass.Duration,
        ["volume"] = DeviceClass.Volume,
        ["water"] = DeviceClass.Water,
        ["gas"] = DeviceClass.Gas,
        ["distance"] = DeviceClass.Distance,
        ["speed"] = DeviceClass.Speed,
        ["weight"] = DeviceClass.Weight,
        ["monetary"] = DeviceClass.Monetary,
        ["timestamp"] = DeviceClass.Timestamp,
        ["enum"] = DeviceClass.Enum,
    };

    private static readonly Dictionary<string, DeviceClass> BinaryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["battery"] = DeviceClass.BatteryLow,
        ["battery_charging"] = DeviceClass.BatteryCharging,
        ["carbon_monoxide"] = DeviceClass.CarbonMonoxide,
        ["cold"] = DeviceClass.Cold,
        ["connectivity"] = DeviceClass.Connectivity,
        ["door"] = DeviceClass.Door,
        ["garage_door"] = DeviceClass.GarageDoor,
        ["gas"] = DeviceClass.Gas,
        ["heat"] = DeviceClass.Heat,
        ["light"] = DeviceClass.Light,
        ["lock"] = DeviceClass.Lock,
        ["moisture"] = DeviceClass.Moisture,
        ["motion"] = DeviceClass.Motion,
        ["moving"] = DeviceClass.Moving,
        ["occupancy"] = DeviceClass.Occupancy,
        ["opening"] = DeviceClass.Opening,
        ["plug"] = DeviceClass.Plug,
        ["power"] = DeviceClass.PowerPresent,
        ["presence"] = DeviceClass.Presence,
        ["problem"] = DeviceClass.Problem,
        ["running"] = DeviceClass.Running,
        ["safety"] = DeviceClass.Safety,
        ["smoke"] = DeviceClass.Smoke,
        ["sound"] = DeviceClass.Sound,
        ["tamper"] = DeviceClass.Tamper,
        ["update"] = DeviceClass.Update,
        ["vibration"] = DeviceClass.Vibration,
        ["window"] = DeviceClass.Window,
    };

    /// <summary>
    /// The class of an HA <c>device_class</c> for a kind of entity (binary sensors have their own list). Unknown or empty:
    /// <see cref="DeviceClass.None"/>, with the original text returned as <c>Raw</c> so nothing is lost.
    /// </summary>
    public static (DeviceClass Class, string? Raw) FromHa(EntityKind kind, string? haClass)
    {
        if (string.IsNullOrWhiteSpace(haClass)) return (DeviceClass.None, null);
        var name = haClass.Trim();
        var table = kind == EntityKind.Binary ? BinaryNames : SensorNames;
        return table.TryGetValue(name, out var c) ? (c, name) : (DeviceClass.None, name);
    }

    /// <summary>The unit values of a class are stored and shown in; null = the class has no fixed unit (the unit passes through).</summary>
    public static string? CanonicalUnit(DeviceClass c) => c switch
    {
        DeviceClass.Temperature => "°C",
        DeviceClass.Humidity or DeviceClass.Moisture or DeviceClass.Battery => "%",
        DeviceClass.Power => "W",
        DeviceClass.ApparentPower => "VA",
        DeviceClass.ReactivePower => "var",
        DeviceClass.Energy => "Wh",
        DeviceClass.ReactiveEnergy => "varh",
        DeviceClass.Voltage => "V",
        DeviceClass.Current => "A",
        DeviceClass.Frequency => "Hz",
        DeviceClass.SignalStrength => "dBm",
        DeviceClass.CarbonDioxide or DeviceClass.CarbonMonoxide => "ppm",
        DeviceClass.Pm1 or DeviceClass.Pm25 or DeviceClass.Pm10 or DeviceClass.VolatileOrganicCompounds => "µg/m³",
        DeviceClass.Pressure => "hPa",
        DeviceClass.Illuminance => "lx",
        DeviceClass.Duration => "s",
        _ => null,
    };

    /// <summary>Units accepted for a class and how they convert into the canonical one (case-sensitive where the case matters: mW/MW).</summary>
    private static readonly Dictionary<DeviceClass, Dictionary<string, UnitConversion>> Conversions = new()
    {
        [DeviceClass.Temperature] = new() { ["°C"] = new(1), ["C"] = new(1), ["°F"] = new(5.0 / 9, -32 * 5.0 / 9), ["F"] = new(5.0 / 9, -32 * 5.0 / 9), ["K"] = new(1, -273.15) },
        [DeviceClass.Humidity] = new() { ["%"] = new(1) },
        [DeviceClass.Moisture] = new() { ["%"] = new(1) },
        [DeviceClass.Battery] = new() { ["%"] = new(1) },
        [DeviceClass.Power] = new() { ["W"] = new(1), ["mW"] = new(0.001), ["kW"] = new(1000), ["MW"] = new(1e6), ["GW"] = new(1e9) },
        [DeviceClass.ApparentPower] = new() { ["VA"] = new(1), ["mVA"] = new(0.001), ["kVA"] = new(1000) },
        [DeviceClass.ReactivePower] = new() { ["var"] = new(1), ["mvar"] = new(0.001), ["kvar"] = new(1000) },
        [DeviceClass.Energy] = new() { ["Wh"] = new(1), ["mWh"] = new(0.001), ["kWh"] = new(1000), ["MWh"] = new(1e6), ["GWh"] = new(1e9), ["J"] = new(1 / 3600.0), ["kJ"] = new(1 / 3.6), ["MJ"] = new(1000 / 3.6), ["GJ"] = new(1e6 / 3.6) },
        [DeviceClass.ReactiveEnergy] = new() { ["varh"] = new(1), ["kvarh"] = new(1000) },
        [DeviceClass.Voltage] = new() { ["V"] = new(1), ["mV"] = new(0.001), ["µV"] = new(1e-6), ["kV"] = new(1000), ["MV"] = new(1e6) },
        [DeviceClass.Current] = new() { ["A"] = new(1), ["mA"] = new(0.001), ["µA"] = new(1e-6) },
        [DeviceClass.Frequency] = new() { ["Hz"] = new(1), ["kHz"] = new(1000), ["MHz"] = new(1e6), ["GHz"] = new(1e9) },
        [DeviceClass.SignalStrength] = new() { ["dBm"] = new(1) },
        [DeviceClass.CarbonDioxide] = new() { ["ppm"] = new(1) },
        [DeviceClass.CarbonMonoxide] = new() { ["ppm"] = new(1) },
        [DeviceClass.Pm1] = new() { ["µg/m³"] = new(1), ["μg/m³"] = new(1) },
        [DeviceClass.Pm25] = new() { ["µg/m³"] = new(1), ["μg/m³"] = new(1) },
        [DeviceClass.Pm10] = new() { ["µg/m³"] = new(1), ["μg/m³"] = new(1) },
        [DeviceClass.VolatileOrganicCompounds] = new() { ["µg/m³"] = new(1), ["μg/m³"] = new(1) },
        [DeviceClass.Pressure] = new() { ["hPa"] = new(1), ["mbar"] = new(1), ["Pa"] = new(0.01), ["kPa"] = new(10), ["cbar"] = new(10), ["bar"] = new(1000), ["mmHg"] = new(1.333224), ["inHg"] = new(33.86389), ["psi"] = new(68.94757) },
        [DeviceClass.Illuminance] = new() { ["lx"] = new(1) },
        [DeviceClass.Duration] = new() { ["s"] = new(1), ["ms"] = new(0.001), ["µs"] = new(1e-6), ["min"] = new(60), ["h"] = new(3600), ["d"] = new(86400) },
    };

    /// <summary>
    /// The canonical form of (class, unit): the canonical unit and the conversion into it. A unit the class does not know
    /// passes through unconverted and the class becomes generic (<see cref="DeviceClass.None"/>): a value is never shown in
    /// a unit it was not converted to. Classes without a canonical unit keep the unit given.
    /// </summary>
    public static (DeviceClass Class, string? Unit, UnitConversion Conversion) Normalize(DeviceClass c, string? unit)
    {
        var u = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        if (CanonicalUnit(c) is not { } canonical || !Conversions.TryGetValue(c, out var table)) return (c, u, UnitConversion.Identity);
        if (u is null)
        {
            // A percentage class without a unit (Z2M battery has %) is taken as canonical; other classes become generic.
            return canonical == "%" ? (c, canonical, UnitConversion.Identity) : (DeviceClass.None, null, UnitConversion.Identity);
        }
        return table.TryGetValue(u, out var conv) ? (c, canonical, conv) : (DeviceClass.None, u, UnitConversion.Identity);
    }

    /// <summary>The decimals to show after a conversion: the original precision less the decimal digits the factor added.</summary>
    public static int? ConvertPrecision(int? precision, UnitConversion conv)
    {
        if (precision is not { } p || conv.Factor <= 0 || conv.Offset != 0) return precision;
        var shift = (int)Math.Round(Math.Log10(conv.Factor));
        return Math.Max(0, p - shift);
    }

    /// <summary>
    /// Round 2: the sensor classes whose only sensible state class is <c>measurement</c> — deduced when a device sends none
    /// (the HA documentation's device-based example has no <c>state_class</c>). Never energy (total or total_increasing cannot
    /// be told apart) and never a number without a class (it may be a code).
    /// </summary>
    public static bool ImpliesMeasurement(DeviceClass c) => c is DeviceClass.Temperature or DeviceClass.Humidity or DeviceClass.Pressure
        or DeviceClass.Illuminance or DeviceClass.CarbonDioxide or DeviceClass.CarbonMonoxide or DeviceClass.Pm1 or DeviceClass.Pm25
        or DeviceClass.Pm10 or DeviceClass.VolatileOrganicCompounds or DeviceClass.Voltage or DeviceClass.Current or DeviceClass.Power
        or DeviceClass.SignalStrength or DeviceClass.Battery;

    /// <summary>The classes exported to /metrics by default (0.11 cardinality rule): the main measured ones.</summary>
    public static bool IsMainMeasurement(DeviceClass c) => c is DeviceClass.Temperature or DeviceClass.Humidity or DeviceClass.Power
        or DeviceClass.Energy or DeviceClass.Voltage or DeviceClass.Current or DeviceClass.Battery or DeviceClass.CarbonDioxide
        or DeviceClass.Pm1 or DeviceClass.Pm25 or DeviceClass.Pm10 or DeviceClass.Pressure or DeviceClass.Illuminance;

    /// <summary>The Romanian name of a class (an entity without a name, the secondary label of a generic device).</summary>
    public static string Name(DeviceClass c) => c switch
    {
        DeviceClass.None => "Valoare",
        DeviceClass.Temperature => "Temperatură",
        DeviceClass.Humidity => "Umiditate",
        DeviceClass.Moisture => "Umezeală",
        DeviceClass.Power => "Putere",
        DeviceClass.ApparentPower => "Putere aparentă",
        DeviceClass.ReactivePower => "Putere reactivă",
        DeviceClass.Energy => "Energie",
        DeviceClass.ReactiveEnergy => "Energie reactivă",
        DeviceClass.Voltage => "Tensiune",
        DeviceClass.Current => "Curent",
        DeviceClass.Frequency => "Frecvență",
        DeviceClass.PowerFactor => "Factor de putere",
        DeviceClass.Battery => "Baterie",
        DeviceClass.SignalStrength => "Semnal",
        DeviceClass.CarbonDioxide => "CO₂",
        DeviceClass.CarbonMonoxide => "CO",
        DeviceClass.VolatileOrganicCompounds => "COV",
        DeviceClass.Pm1 => "PM1",
        DeviceClass.Pm25 => "PM2,5",
        DeviceClass.Pm10 => "PM10",
        DeviceClass.Pressure => "Presiune",
        DeviceClass.Illuminance => "Iluminare",
        DeviceClass.Duration => "Durată",
        DeviceClass.Volume => "Volum",
        DeviceClass.Water => "Apă",
        DeviceClass.Gas => "Gaz",
        DeviceClass.Distance => "Distanță",
        DeviceClass.Speed => "Viteză",
        DeviceClass.Weight => "Greutate",
        DeviceClass.Monetary => "Sumă",
        DeviceClass.Timestamp => "Moment",
        DeviceClass.Enum => "Stare",
        DeviceClass.Connectivity => "Conectivitate",
        DeviceClass.Door => "Ușă",
        DeviceClass.Window => "Fereastră",
        DeviceClass.GarageDoor => "Ușă garaj",
        DeviceClass.Opening => "Deschidere",
        DeviceClass.Motion => "Mișcare",
        DeviceClass.Moving => "În mișcare",
        DeviceClass.Occupancy => "Ocupare",
        DeviceClass.Presence => "Prezență",
        DeviceClass.Problem => "Problemă",
        DeviceClass.Running => "Funcționare",
        DeviceClass.Plug => "Priză",
        DeviceClass.PowerPresent => "Alimentare",
        DeviceClass.BatteryLow => "Baterie",
        DeviceClass.BatteryCharging => "Încărcare",
        DeviceClass.Lock => "Încuietoare",
        DeviceClass.Smoke => "Fum",
        DeviceClass.Tamper => "Deschis forțat",
        DeviceClass.Vibration => "Vibrație",
        DeviceClass.Heat => "Căldură",
        DeviceClass.Cold => "Frig",
        DeviceClass.Light => "Lumină",
        DeviceClass.Safety => "Siguranță",
        DeviceClass.Sound => "Sunet",
        DeviceClass.Update => "Actualizare",
        _ => c.ToString(),
    };

    /// <summary>The Romanian text of a binary state per class (door open/closed, moisture wet/dry, battery low/OK …).</summary>
    public static string BinaryText(DeviceClass c, bool on) => c switch
    {
        DeviceClass.Door or DeviceClass.Window or DeviceClass.GarageDoor or DeviceClass.Opening => on ? "deschis" : "închis",
        DeviceClass.Moisture => on ? "ud" : "uscat",
        DeviceClass.BatteryLow => on ? "descărcată" : "OK",
        DeviceClass.BatteryCharging => on ? "se încarcă" : "nu se încarcă",
        DeviceClass.Connectivity => on ? "conectat" : "deconectat",
        DeviceClass.Motion or DeviceClass.Moving or DeviceClass.Vibration => on ? "detectat" : "liniște",
        DeviceClass.Occupancy or DeviceClass.Presence => on ? "ocupat" : "liber",
        DeviceClass.Problem => on ? "problemă" : "OK",
        DeviceClass.Running => on ? "pornit" : "oprit",
        DeviceClass.Plug or DeviceClass.PowerPresent => on ? "alimentat" : "fără curent",
        DeviceClass.Lock => on ? "descuiat" : "încuiat",
        DeviceClass.Smoke or DeviceClass.Gas or DeviceClass.CarbonMonoxide or DeviceClass.Sound => on ? "detectat" : "normal",
        DeviceClass.Tamper => on ? "deschis forțat" : "OK",
        DeviceClass.Heat => on ? "cald" : "normal",
        DeviceClass.Cold => on ? "rece" : "normal",
        DeviceClass.Light => on ? "lumină" : "întuneric",
        DeviceClass.Safety => on ? "nesigur" : "sigur",
        DeviceClass.Update => on ? "actualizare disponibilă" : "la zi",
        _ => on ? "pornit" : "oprit",
    };

    /// <summary>The binary states that need attention (shown as a warning badge): open, wet, low battery, problem, smoke …</summary>
    public static bool IsAlert(DeviceClass c, bool on) => on && c is DeviceClass.Moisture or DeviceClass.BatteryLow or DeviceClass.Problem
        or DeviceClass.Smoke or DeviceClass.Gas or DeviceClass.CarbonMonoxide or DeviceClass.Tamper or DeviceClass.Safety or DeviceClass.Heat or DeviceClass.Cold;

    /// <summary>The decimals a class is shown with when the entity gives none.</summary>
    public static int DefaultDecimals(DeviceClass c) => c switch
    {
        DeviceClass.Temperature or DeviceClass.Voltage or DeviceClass.ApparentPower or DeviceClass.ReactivePower => 1,
        DeviceClass.Current or DeviceClass.PowerFactor => 2,
        DeviceClass.Frequency => 2,
        DeviceClass.Power => 1,
        _ => 0,
    };

    /// <summary>Parses a value as an invariant number (MQTT payloads, templates); null when it is not one.</summary>
    public static double? ParseNumber(string? s) =>
        s is not null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;
}
