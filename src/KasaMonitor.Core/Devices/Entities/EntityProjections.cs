using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Devices.Entities;

/// <summary>Entities and their values, built together (a projection adds an entity only with its value).</summary>
public sealed class EntitySet
{
    private readonly List<DeviceEntity> _entities = new();
    private readonly Dictionary<string, EntityValue> _values = new(StringComparer.Ordinal);
    private readonly string _deviceKey;
    private readonly DateTimeOffset? _ts;
    private readonly bool _available;

    public EntitySet(string deviceKey, DateTimeOffset? ts, bool available)
    {
        _deviceKey = deviceKey;
        _ts = ts;
        _available = available;
    }

    public IReadOnlyList<DeviceEntity> Entities => _entities;
    public IReadOnlyDictionary<string, EntityValue> Values => _values;

    public string KeyOf(string slug) => EntityKeys.Of(_deviceKey, slug);

    /// <summary>A number; <paramref name="always"/> false = only when the device reported it.</summary>
    public EntitySet Number(string slug, string name, double? value, DeviceClass cls, string? unit, StateClass? stateClass, int? precision,
        EntityCategory category = EntityCategory.Primary, bool always = false)
    {
        if (value is null && !always) return this;
        var key = KeyOf(slug);
        _entities.Add(new DeviceEntity(key, name, EntityKind.Sensor, EntityDataType.Number, cls, null, unit, stateClass, precision, false, category));
        _values[key] = EntityValue.Of(key, value, _ts, _available);
        return this;
    }

    public EntitySet Binary(string slug, string name, bool? value, DeviceClass cls, EntityCategory category = EntityCategory.Primary, bool always = false)
    {
        if (value is null && !always) return this;
        var key = KeyOf(slug);
        _entities.Add(new DeviceEntity(key, name, EntityKind.Binary, EntityDataType.Boolean, cls, null, null, null, null, false, category));
        _values[key] = EntityValue.Of(key, value, _ts, _available);
        return this;
    }

    public EntitySet Text(string slug, string name, string? value, DeviceClass cls = DeviceClass.None, EntityCategory category = EntityCategory.Primary,
        EntityDataType type = EntityDataType.Text)
    {
        if (string.IsNullOrWhiteSpace(value)) return this;
        var key = KeyOf(slug);
        _entities.Add(new DeviceEntity(key, name, EntityKind.Sensor, type, cls, null, null, null, null, false, category));
        _values[key] = EntityValue.OfText(key, value, _ts, _available);
        return this;
    }

    /// <summary>A relay: shown with its state; the command stays with the integration's own control (R8).</summary>
    public EntitySet Switch(string slug, string name, bool? value)
    {
        if (value is null) return this;
        var key = KeyOf(slug);
        _entities.Add(new DeviceEntity(key, name, EntityKind.Switch, EntityDataType.Boolean, DeviceClass.None, null, null, null, null, true));
        _values[key] = EntityValue.Of(key, value, _ts, _available);
        return this;
    }
}

/// <summary>Single owner of the entity key format: <c>{device key}/{slug}</c>.</summary>
public static class EntityKeys
{
    public const char Separator = '/';

    public static string Of(string deviceKey, string slug) => deviceKey + Separator + slug;

    /// <summary>The device key of an entity key (the text before the last separator), or null.</summary>
    public static string? DeviceOf(string entityKey)
    {
        var i = entityKey.LastIndexOf(Separator);
        return i > 0 ? entityKey[..i] : null;
    }
}

/// <summary>
/// The pure projections of the existing integrations into entities (0.11, „proiecție, nu migrare”): a plug / meter
/// reading, a climate sensor, the boiler snapshot, a Miele appliance state. The domain stays the source of truth;
/// these only describe what is read for the generated page. Every public property of the projected models is either
/// projected or listed in an <c>Excluded*</c> set (reflection test: a new property cannot be forgotten).
/// </summary>
public static class EntityProjections
{
    /// <summary>Properties of <see cref="EmeterReading"/> not projected (none: every value has an entity).</summary>
    public static readonly IReadOnlySet<string> ExcludedEmeter = new HashSet<string>();

    /// <summary>
    /// Properties of <see cref="HeatingSnapshot"/> not projected, and why: the time of the reading (the device's), the
    /// lists shown by /centrala (schedule, messages) and the parser's own diagnostics.
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedHeating = new HashSet<string>
    {
        nameof(HeatingSnapshot.ReadUtc), nameof(HeatingSnapshot.Schedule), nameof(HeatingSnapshot.Messages),
        nameof(HeatingSnapshot.FeatureCount), nameof(HeatingSnapshot.Unmapped),
    };

    /// <summary>Properties of <see cref="MieleStateValues"/> not projected: the raw codes (their texts are projected).</summary>
    public static readonly IReadOnlySet<string> ExcludedMiele = new HashSet<string>
    {
        nameof(MieleStateValues.StatusRaw), nameof(MieleStateValues.ProgramTypeRaw), nameof(MieleStateValues.ProgramIdRaw),
        nameof(MieleStateValues.PhaseRaw),
    };

    /// <summary>
    /// A plug or a meter channel: power, voltage, current, the cumulative counter (Tapo: month-to-date, reset monthly —
    /// exactly <c>total_increasing</c>), the device's day / month counters (diagnostic), the Shelly extras, the relay and
    /// „online”. Optional values appear only when the device reports them.
    /// </summary>
    public static EntitySet Plug(string deviceKey, EmeterReading? r, DateTimeOffset? ts, bool online)
    {
        var s = new EntitySet(deviceKey, ts, online);
        s.Number("power", "Putere", r is null ? null : r.PowerMw / 1000.0, DeviceClass.Power, "W", StateClass.Measurement, 1, always: true)
         .Number("voltage", "Tensiune", r?.VoltageMv / 1000.0, DeviceClass.Voltage, "V", StateClass.Measurement, 1)
         .Number("current", "Curent", r?.CurrentMa / 1000.0, DeviceClass.Current, "A", StateClass.Measurement, 3)
         .Number("energy", "Energie (contorul dispozitivului)", r?.TotalWh, DeviceClass.Energy, "Wh", StateClass.TotalIncreasing, 0)
         .Number("energy_today", "Energie azi (dispozitiv)", r?.TodayWh, DeviceClass.Energy, "Wh", StateClass.TotalIncreasing, 0, EntityCategory.Diagnostic)
         .Number("energy_month", "Energie luna aceasta (dispozitiv)", r?.MonthWh, DeviceClass.Energy, "Wh", StateClass.TotalIncreasing, 0, EntityCategory.Diagnostic)
         .Number("power_factor", "Factor de putere", r?.PowerFactor, DeviceClass.PowerFactor, null, StateClass.Measurement, 2)
         .Number("apparent_power", "Putere aparentă", r?.ApparentPowerVa, DeviceClass.ApparentPower, "VA", StateClass.Measurement, 1)
         .Number("frequency", "Frecvență", r?.FrequencyHz, DeviceClass.Frequency, "Hz", StateClass.Measurement, 2)
         .Number("energy_returned", "Energie returnată", r?.ReturnedWh, DeviceClass.Energy, "Wh", StateClass.TotalIncreasing, 0)
         .Number("device_temperature", "Temperatura dispozitivului", r?.TemperatureC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1, EntityCategory.Diagnostic)
         .Switch("relay", "Releu", r?.RelayOn)
         .Binary("online", "Online", online, DeviceClass.Connectivity, EntityCategory.Diagnostic, always: true);
        return s;
    }

    /// <summary>A climate sensor: temperature, humidity, battery (diagnostic) and „online”.</summary>
    public static EntitySet Climate(string deviceKey, double? temperatureC, double? humidityPercent, int? batteryPercent, DateTimeOffset? ts, bool online)
    {
        var s = new EntitySet(deviceKey, ts, online);
        s.Number("temperature", "Temperatură", temperatureC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1, always: true)
         .Number("humidity", "Umiditate", humidityPercent, DeviceClass.Humidity, "%", StateClass.Measurement, 0)
         .Number("battery", "Baterie", batteryPercent, DeviceClass.Battery, "%", StateClass.Measurement, 0, EntityCategory.Diagnostic)
         .Binary("online", "Online", online, DeviceClass.Connectivity, EntityCategory.Diagnostic, always: true);
        return s;
    }

    /// <summary>The boiler: temperatures, burner, modulation, pump, pressure, counters, modes, error, Wi-Fi and the room sensor.</summary>
    public static EntitySet Heating(string deviceKey, HeatingSnapshot? h, bool online)
    {
        var s = new EntitySet(deviceKey, h?.ReadUtc, online);
        s.Binary("online", "Online", online, DeviceClass.Connectivity, EntityCategory.Diagnostic, always: true);
        if (h is null) return s;
        s.Number("boiler_temp", "Temperatura cazanului", h.BoilerTempC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1)
         .Number("boiler_target", "Ținta cazanului", h.BoilerTargetC, DeviceClass.Temperature, "°C", null, 1)
         .Number("supply_temp", "Tur", h.SupplyTempC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1)
         .Number("return_temp", "Retur", h.ReturnTempC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1)
         .Number("outside_temp", "Afară (senzorul centralei)", h.OutsideTempC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1)
         .Number("room_temp", "Cameră (termostat)", h.RoomTempC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1)
         .Number("dhw_temp", "Apă caldă", h.DhwTempC, DeviceClass.Temperature, "°C", StateClass.Measurement, 1)
         .Number("dhw_target", "Ținta apei calde", h.DhwTargetC, DeviceClass.Temperature, "°C", null, 1)
         .Binary("burner", "Arzător", h.BurnerActive, DeviceClass.Running)
         .Number("modulation", "Modulație", h.ModulationPct, DeviceClass.None, "%", StateClass.Measurement, 0)
         .Number("pump", "Pompă", h.PumpPct, DeviceClass.None, "%", StateClass.Measurement, 0)
         // Water pressure of the circuit, kept in bar (the boiler's unit) as a generic value: the canonical hPa is for air.
         .Number("pressure", "Presiunea apei", h.PressureBar, DeviceClass.None, "bar", StateClass.Measurement, 1)
         .Number("burner_starts", "Porniri arzător", h.BurnerStarts, DeviceClass.None, null, StateClass.TotalIncreasing, 0, EntityCategory.Diagnostic)
         .Number("burner_hours", "Ore arzător", h.BurnerHours, DeviceClass.None, "h", StateClass.TotalIncreasing, 0, EntityCategory.Diagnostic)
         .Text("operating_mode", "Mod de funcționare", h.OperatingMode, DeviceClass.Enum, type: EntityDataType.Enum)
         .Text("active_program", "Program activ", h.ActiveProgram, DeviceClass.Enum, type: EntityDataType.Enum)
         .Text("circuit", "Circuit", h.CircuitName, category: EntityCategory.Diagnostic)
         .Text("frost_protection", "Protecție la îngheț", h.FrostProtection, DeviceClass.Enum, EntityCategory.Diagnostic, EntityDataType.Enum)
         .Text("dhw_mode", "Mod apă caldă", h.DhwMode, DeviceClass.Enum, type: EntityDataType.Enum)
         .Binary("dhw_active", "Apă caldă activă", h.DhwActive, DeviceClass.Running)
         .Text("dhw_status", "Stare apă caldă", h.DhwStatus, DeviceClass.Enum, type: EntityDataType.Enum)
         .Binary("dhw_comfort", "Confort apă caldă", h.DhwComfort, DeviceClass.None, EntityCategory.Config)
         .Binary("dhw_one_time_charge", "Încărcare unică apă caldă", h.DhwOneTimeCharge, DeviceClass.Running, EntityCategory.Config)
         .Binary("dhw_scald_protection", "Protecție anti-opărire", h.DhwScaldProtection, DeviceClass.None, EntityCategory.Config)
         .Binary("malfunction_lock", "Blocare (defect)", h.MalfunctionLock, DeviceClass.Problem)
         .Text("active_error", "Eroare activă", h.ActiveError)
         .Text("status_code", "Cod de stare", h.StatusCode, category: EntityCategory.Diagnostic)
         .Number("wifi", "Semnal Wi-Fi", h.WifiDbm, DeviceClass.SignalStrength, "dBm", StateClass.Measurement, 0, EntityCategory.Diagnostic)
         .Number("room_sensor_battery", "Baterie senzor cameră", h.RoomSensorBatteryPct, DeviceClass.Battery, "%", StateClass.Measurement, 0, EntityCategory.Diagnostic)
         .Number("room_sensor_lqi", "Legătură senzor cameră", h.RoomSensorLqiPct, DeviceClass.None, "%", StateClass.Measurement, 0, EntityCategory.Diagnostic)
         .Text("gas_type", "Tip gaz", h.GasType, category: EntityCategory.Diagnostic)
         .Binary("daylight_saving", "Ora de vară (centrală)", h.DaylightSaving, DeviceClass.None, EntityCategory.Diagnostic);
        foreach (var (program, temp) in h.ProgramTemps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            s.Number("program_temp_" + program, $"Țintă program {program}", temp, DeviceClass.Temperature, "°C", null, 1, EntityCategory.Config);
        Summary(s, "gas_heating", "Gaz încălzire", h.GasHeating);
        Summary(s, "gas_dhw", "Gaz apă caldă", h.GasDhw);
        Summary(s, "heat_heating", "Căldură încălzire", h.HeatHeating);
        Summary(s, "heat_dhw", "Căldură apă caldă", h.HeatDhw);
        Summary(s, "power_heating", "Electricitate încălzire", h.PowerHeating);
        Summary(s, "power_dhw", "Electricitate apă caldă", h.PowerDhw);
        return s;
    }

    /// <summary>Today's and this month's value of a boiler summary: energy (kWh → Wh) or gas (m³), counters that reset.</summary>
    private static void Summary(EntitySet s, string slug, string name, EnergySummary? e)
    {
        if (e is null) return;
        var (cls, unit, factor) = e.Unit switch
        {
            EnergyUnit.KilowattHour => (DeviceClass.Energy, "Wh", 1000.0),
            EnergyUnit.CubicMeter => (DeviceClass.Gas, "m³", 1.0),
            _ => (DeviceClass.None, (string?)null, 1.0),
        };
        var precision = cls == DeviceClass.Gas ? 2 : 0;
        s.Number(slug + "_day", name + " azi", e.CurrentDay * factor, cls, unit, StateClass.TotalIncreasing, precision)
         .Number(slug + "_month", name + " luna aceasta", e.CurrentMonth * factor, cls, unit, StateClass.TotalIncreasing, precision, EntityCategory.Diagnostic);
    }

    /// <summary>A Miele appliance: state, program, phase, times, door, signals, its own consumption and forecasts, remote flags.</summary>
    public static EntitySet Miele(string deviceKey, MieleStateValues? m, DateTimeOffset? ts, bool online)
    {
        var s = new EntitySet(deviceKey, ts, online);
        s.Binary("online", "Online", online, DeviceClass.Connectivity, EntityCategory.Diagnostic, always: true);
        if (m is null) return s;
        s.Text("status", "Stare", m.StatusText, DeviceClass.Enum, type: EntityDataType.Enum)
         .Binary("running", "În funcțiune", m.IsRunning, DeviceClass.Running)
         .Text("program_type", "Tip program", m.ProgramTypeText, DeviceClass.Enum, EntityCategory.Diagnostic, EntityDataType.Enum)
         .Text("program", "Program", m.ProgramIdText, DeviceClass.Enum, type: EntityDataType.Enum)
         .Text("phase", "Fază", m.PhaseText, DeviceClass.Enum, type: EntityDataType.Enum)
         .Number("remaining", "Timp rămas", m.RemainingSec, DeviceClass.Duration, "s", null, 0)
         .Number("elapsed", "Timp scurs", m.ElapsedSec, DeviceClass.Duration, "s", null, 0)
         .Number("start_time", "Pornire întârziată", m.StartTimeSec, DeviceClass.Duration, "s", null, 0, EntityCategory.Diagnostic)
         .Number("target_temp", "Temperatura programului", m.TargetTempC, DeviceClass.Temperature, "°C", null, 0)
         .Number("spin", "Centrifugare", m.SpinRpm, DeviceClass.None, "rpm", null, 0)
         .Number("drying_step", "Treaptă uscare (cod)", m.DryingStepRaw, DeviceClass.None, null, null, 0, EntityCategory.Diagnostic)
         .Binary("door", "Ușa", m.DoorOpen, DeviceClass.Door)
         .Binary("signal_failure", "Defect semnalat", m.SignalFailure, DeviceClass.Problem)
         .Binary("signal_info", "Informație semnalată", m.SignalInfo, DeviceClass.None, EntityCategory.Diagnostic)
         .Number("eco_energy", "Energie program (Miele)", m.EcoEnergyKwh * 1000, DeviceClass.Energy, "Wh", StateClass.TotalIncreasing, 0)
         .Number("eco_water", "Apă program (Miele)", m.EcoWaterL, DeviceClass.Water, "L", StateClass.TotalIncreasing, 0)
         .Number("eco_energy_forecast", "Prognoză energie", m.EcoEnergyForecast * 100, DeviceClass.None, "%", null, 0, EntityCategory.Diagnostic)
         .Number("eco_water_forecast", "Prognoză apă", m.EcoWaterForecast * 100, DeviceClass.None, "%", null, 0, EntityCategory.Diagnostic)
         .Binary("remote_full_control", "Control de la distanță", m.RemoteFullControl, DeviceClass.None, EntityCategory.Config)
         .Binary("remote_smart_grid", "Smart Grid", m.RemoteSmartGrid, DeviceClass.None, EntityCategory.Config)
         .Binary("remote_mobile_start", "Pornire din aplicație", m.RemoteMobileStart, DeviceClass.None, EntityCategory.Config);
        return s;
    }
}
