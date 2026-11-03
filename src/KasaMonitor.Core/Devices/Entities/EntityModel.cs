namespace KasaMonitor.Core.Devices.Entities;

/// <summary>
/// What an entity is (0.11): a reading (<see cref="Sensor"/>, <see cref="Binary"/>, <see cref="Text"/>) or something that can
/// be commanded (<see cref="Switch"/>, <see cref="Number"/>, <see cref="Select"/>, <see cref="Button"/>). A component the app
/// does not accept (MQTT <c>climate</c>, <c>light</c>, <c>cover</c> …) is <see cref="Unsupported"/>: listed with its reason,
/// never dropped silently.
/// </summary>
public enum EntityKind
{
    Sensor,
    Binary,
    Switch,
    Number,
    Select,
    Button,
    Text,
    Unsupported,
}

/// <summary>The type of an entity's value.</summary>
public enum EntityDataType
{
    Number,
    Boolean,
    Text,
    Enum,
    Timestamp,
    Duration,
}

/// <summary>
/// What a value measures, the app's own list mapped from Home Assistant's <c>device_class</c> (sensor and binary sensor)
/// by <see cref="DeviceClasses.FromHa"/>; <see cref="None"/> = generic (the original text stays in
/// <see cref="DeviceEntity.ClassRaw"/>). Binary classes whose HA name is also a sensor class get their own value
/// (<see cref="BatteryLow"/> for binary <c>battery</c>, <see cref="PowerPresent"/> for binary <c>power</c>).
/// </summary>
public enum DeviceClass
{
    None,
    // Sensor classes (numeric).
    Temperature,
    Humidity,
    Moisture,
    Power,
    ApparentPower,
    ReactivePower,
    Energy,
    ReactiveEnergy,
    Voltage,
    Current,
    Frequency,
    PowerFactor,
    Battery,
    SignalStrength,
    CarbonDioxide,
    CarbonMonoxide,
    VolatileOrganicCompounds,
    Pm1,
    Pm25,
    Pm10,
    Pressure,
    Illuminance,
    Duration,
    Volume,
    Water,
    Gas,
    Distance,
    Speed,
    Weight,
    Monetary,
    // Sensor classes (not numeric).
    Timestamp,
    Enum,
    // Binary sensor classes.
    Connectivity,
    Door,
    Window,
    GarageDoor,
    Opening,
    Motion,
    Moving,
    Occupancy,
    Presence,
    Problem,
    Running,
    Plug,
    PowerPresent,
    BatteryLow,
    BatteryCharging,
    Lock,
    Smoke,
    Tamper,
    Vibration,
    Heat,
    Cold,
    Light,
    Safety,
    Sound,
    Update,
}

/// <summary>Home Assistant's <c>state_class</c>: what kind of series the value is (decides chart vs consumption per period).</summary>
public enum StateClass
{
    /// <summary>A current value (temperature, power).</summary>
    Measurement,
    /// <summary>A cumulative amount that can go up and down (net energy).</summary>
    Total,
    /// <summary>A cumulative amount that only increases, with resets (an energy counter).</summary>
    TotalIncreasing,
    /// <summary>An angle in degrees.</summary>
    MeasurementAngle,
}

/// <summary>Home Assistant's <c>entity_category</c>: a main value, a diagnostic one (folded away) or a configuration.</summary>
public enum EntityCategory
{
    Primary,
    Diagnostic,
    Config,
}

/// <summary>
/// How an entity can be commanded (MQTT <c>command_topic</c> with its payloads or limits). Shown only in 0.11 (R8:
/// commands are not enabled); <see cref="Optimistic"/> = no state topic, the state shown would be the last command's.
/// </summary>
public sealed record EntityCommand(
    string? Topic,
    string? PayloadOn = null,
    string? PayloadOff = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    IReadOnlyList<string>? Options = null,
    bool Optimistic = false);

/// <summary>
/// The description of one value of a device (0.11, the common capability model). <see cref="Key"/> is stable text:
/// <c>plug:{guid}/power</c>, <c>climate:{guid}/temperature</c>, <c>heating:{guid}/supply_temp</c>, <c>dev:{id}/{unique_id}</c>;
/// <see cref="EntityPrefs"/> bind to it. <see cref="Unit"/> is canonical (<see cref="DeviceClasses.CanonicalUnit"/>):
/// values are converted on the way in. <see cref="Problem"/> says why it cannot be read (e.g. „șablon neacceptat”).
/// <see cref="StateClassInferred"/>: the device sent no <c>state_class</c>; <c>measurement</c> was deduced from the class
/// (<see cref="DeviceClasses.ImpliesMeasurement"/>).
/// </summary>
public sealed record DeviceEntity(
    string Key,
    string Name,
    EntityKind Kind,
    EntityDataType DataType,
    DeviceClass Class = DeviceClass.None,
    string? ClassRaw = null,
    string? Unit = null,
    StateClass? StateClass = null,
    int? Precision = null,
    bool Writable = false,
    EntityCategory Category = EntityCategory.Primary,
    EntityCommand? Command = null,
    string? Problem = null,
    bool StateClassInferred = false);

/// <summary>
/// The current value of an entity: a number or a text (binary: <see cref="Number"/> 1/0), when it was read, available or not,
/// whether it is late (<see cref="Late"/>: older than its source usually delivers) and, when the last messages could not be
/// read (a template that failed on them), why, how many times and when last (<see cref="ReadError"/>; in memory only).
/// </summary>
public sealed record EntityValue(string EntityKey, double? Number, string? Text, DateTimeOffset? TsUtc, bool Available = true,
    bool Late = false, string? ReadError = null, int ReadErrors = 0, DateTimeOffset? ReadErrorUtc = null)
{
    public static EntityValue Of(string key, double? number, DateTimeOffset? ts, bool available = true) => new(key, number, null, ts, available);
    public static EntityValue Of(string key, bool? on, DateTimeOffset? ts, bool available = true) => new(key, on is { } b ? (b ? 1 : 0) : null, null, ts, available);
    public static EntityValue OfText(string key, string? text, DateTimeOffset? ts, bool available = true) => new(key, null, text, ts, available);

    public bool HasValue => Number is not null || Text is not null;
}

/// <summary>
/// One device as the generated page (<c>/dispozitive/{Key}</c>) sees it: <see cref="Key"/> = the row key of /dispozitive
/// (<c>plug:{id}</c>, <c>climate:{id}</c>, …), the integration, what it is, its entities with their current values, and
/// the integration's own page (<see cref="RichPageUrl"/>) for everything that links several values (totals, cost, cycles).
/// </summary>
public sealed record EntityDevice(
    string Key,
    string Integration,
    string Name,
    string? Manufacturer,
    string? Model,
    string? Firmware,
    string? Room,
    bool Available,
    DateTimeOffset? LastReadUtc,
    IReadOnlyList<DeviceEntity> Entities,
    IReadOnlyDictionary<string, EntityValue> Values,
    string? RichPageUrl = null,
    string? Note = null,
    bool IsArchived = false,
    bool Late = false)
{
    public EntityValue? ValueOf(string entityKey) => Values.TryGetValue(entityKey, out var v) ? v : null;
}

/// <summary>
/// What the user chose for one entity (table <c>EntityPrefs</c>), kept apart from the description, which is rewritten at
/// every discovery message. Null = the default of <see cref="EntityDefaults"/>. <see cref="CommandsEnabled"/> stays
/// false in 0.11 (R8).
/// </summary>
public sealed record EntityPrefs(
    string EntityKey,
    bool? Visible = null,
    bool? InHistory = null,
    bool? InMetrics = null,
    string? NameOverride = null,
    int? PrecisionOverride = null,
    DeviceClass? ClassOverride = null,
    string? UnitOverride = null,
    bool CommandsEnabled = false)
{
    /// <summary>Nothing set: the row can be deleted.</summary>
    public bool IsEmpty => Visible is null && InHistory is null && InMetrics is null && NameOverride is null
                           && PrecisionOverride is null && ClassOverride is null && UnitOverride is null && !CommandsEnabled;
}

/// <summary>One bucket of an entity's history (the device page charts): min / mean / max of the values in it.</summary>
public sealed record EntityBucket(DateTimeOffset StartUtc, double? Min, double? Avg, double? Max);

/// <summary>
/// The projection of one kind of device into <see cref="EntityDevice"/> (0.11, „proiecție, nu migrare”): each existing
/// integration keeps its tables and its writes; a source only reads its in-memory state (and, for the history, the
/// existing tables). <see cref="KeyPrefix"/> is the prefix of the /dispozitive row keys it serves (<c>plug:</c> …); every
/// prefix of the catalog has exactly one source (reflection test).
/// </summary>
public interface IEntitySource
{
    string KeyPrefix { get; }

    /// <summary>The device with its entities and current values; null when the key is unknown or malformed.</summary>
    Task<EntityDevice?> GetAsync(string deviceKey, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// The hourly history of one entity in [from, to), oldest first, through an adapter that already exists (plug power
    /// from <c>LiveMinuteAggregates</c>, climate from <c>ClimateReadings</c>); null when the entity has no adapter.
    /// </summary>
    Task<IReadOnlyList<EntityBucket>?> HistoryAsync(string deviceKey, string entityKey, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
}

/// <summary>What a counter counted (0.11, generic counters): today, yesterday, this month (local days) and the last 30 days.</summary>
public sealed record EntityCounterSummary(double? Today, double? Yesterday, double? Month, IReadOnlyList<(DateOnly Day, double Value)> Days);

/// <summary>A source that knows what its counters counted per period (from its hourly rollups); optional next to <see cref="IEntitySource"/>.</summary>
public interface IEntityCounterSource
{
    Task<EntityCounterSummary?> CounterAsync(string deviceKey, string entityKey, DateTimeOffset now, TimeZoneInfo tz, CancellationToken ct);
}
