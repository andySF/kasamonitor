using System.Text.Json.Nodes;
using KasaMonitor.Core.Devices.Entities;

namespace KasaMonitor.Core.Mqtt;

/// <summary>One availability topic of an entity (MQTT <c>availability</c> item or <c>availability_topic</c>).</summary>
public sealed record MqttAvailabilityTopic(string Topic, ValueTemplate? Template, string PayloadAvailable = "online", string PayloadNotAvailable = "offline");

/// <summary><c>availability_mode</c>: all topics available, any of them, or the latest message (the default).</summary>
public enum MqttAvailabilityMode
{
    Latest,
    All,
    Any,
}

/// <summary>
/// What KasaMonitor understands of one discovered entity (0.11): its description in the common model (without the device
/// key, known only once the device is added), the unit conversion into the canonical unit, and where its values and
/// availability come from. <see cref="Problem"/> = why it cannot be read (unsupported component, template outside the
/// subset, no state topic): such an entity is listed, never read from its raw payload.
/// </summary>
public sealed record MqttEntitySpec(
    string UniqueId,
    string Component,
    string ConfigTopic,
    string Name,
    EntityKind Kind,
    EntityDataType DataType,
    DeviceClass Class,
    string? ClassRaw,
    string? Unit,
    UnitConversion Conversion,
    StateClass? StateClass,
    int? Precision,
    EntityCategory Category,
    EntityCommand? Command,
    string? Problem,
    string? StateTopic,
    ValueTemplate? Template,
    string PayloadOn,
    string PayloadOff,
    IReadOnlyList<MqttAvailabilityTopic> Availability,
    MqttAvailabilityMode AvailabilityMode,
    int? ExpireAfterSeconds,
    string? JsonAttributesTopic,
    string DiscoveryJson,
    bool StateClassInferred = false)
{
    /// <summary>The entity's part of its key (<c>dev:{id}/{slug}</c>): the unique id, escaped reversibly (R12).</summary>
    public string Slug => MqttEntityMapper.Slug(UniqueId);

    public bool Writable => Command is not null;

    public DeviceEntity Describe(string deviceKey) =>
        new(EntityKeys.Of(deviceKey, Slug), Name, Kind, DataType, Class, ClassRaw, Unit, StateClass, Precision, Writable, Category, Command, Problem,
            StateClassInferred);
}

/// <summary>
/// Maps a discovered component to an <see cref="MqttEntitySpec"/> (0.11, pure): kind from the component, class from
/// <c>device_class</c> (<see cref="DeviceClasses.FromHa"/>), canonical unit, state class, precision, category, payloads,
/// availability and expiry; the template parsed once (outside the subset → <see cref="MqttEntitySpec.Problem"/>).
/// </summary>
public static class MqttEntityMapper
{
    /// <summary>The longest text kept from a device (names, units, text values; SQL Server keys and indexes need bounds).</summary>
    public const int MaxText = 200;

    /// <summary>
    /// R12 (round 2): the unique id escaped reversibly for the entity key: '%' → %25, '/' → %2F (<c>a/b</c> and <c>a_b</c> no
    /// longer share a key, and the key keeps a single '/').
    /// </summary>
    public static string Slug(string uniqueId) => uniqueId.Replace("%", "%25", StringComparison.Ordinal).Replace("/", "%2F", StringComparison.Ordinal);

    /// <summary>A text from a device, trimmed and cut to <paramref name="max"/> characters.</summary>
    public static string Cut(string text, int max = MaxText)
    {
        var t = text.Trim();
        return t.Length <= max ? t : t[..max];
    }

    /// <summary>R7 (round 2): a topic to subscribe to must be a plain topic, never a filter with '+' or '#'.</summary>
    public static bool HasWildcard(string? topic) => topic is not null && topic.IndexOfAny(['+', '#']) >= 0;

    public static EntityKind KindOf(string component) => component switch
    {
        "sensor" => EntityKind.Sensor,
        "binary_sensor" => EntityKind.Binary,
        "switch" => EntityKind.Switch,
        "number" => EntityKind.Number,
        "select" => EntityKind.Select,
        "button" => EntityKind.Button,
        "text" => EntityKind.Text,
        _ => EntityKind.Unsupported,
    };

    /// <summary>The longest unique id accepted (it is part of the entity key, which must fit an index).</summary>
    public const int MaxUniqueId = 128;

    /// <summary>The spec of a component; null without a <c>unique_id</c> or with one longer than <see cref="MaxUniqueId"/> (cannot be adopted).</summary>
    public static MqttEntitySpec? Map(MqttDiscoveredComponent c)
    {
        if (string.IsNullOrWhiteSpace(c.UniqueId) || c.UniqueId.Length > MaxUniqueId) return null;
        var cfg = c.Config;
        var kind = KindOf(c.Component);
        string? problem = kind == EntityKind.Unsupported ? $"neacceptat ({c.Component})" : null;

        var (cls, raw) = DeviceClasses.FromHa(kind is EntityKind.Binary ? EntityKind.Binary : EntityKind.Sensor, MqttJson.Text(cfg["device_class"]));
        var unit = MqttJson.Text(cfg["unit_of_measurement"]);
        StateClass? stateClass = MqttJson.Text(cfg["state_class"]) switch
        {
            "measurement" => Devices.Entities.StateClass.Measurement,
            "measurement_angle" => Devices.Entities.StateClass.MeasurementAngle,
            "total" => Devices.Entities.StateClass.Total,
            "total_increasing" => Devices.Entities.StateClass.TotalIncreasing,
            _ => null,
        };
        var precision = MqttJson.Number(cfg["suggested_display_precision"]) is { } p ? (int?)Math.Clamp((int)p, 0, 6) : null;
        var category = MqttJson.Text(cfg["entity_category"]) switch
        {
            "diagnostic" => EntityCategory.Diagnostic,
            "config" => EntityCategory.Config,
            _ => EntityCategory.Primary,
        };

        var type = kind switch
        {
            EntityKind.Binary or EntityKind.Switch => EntityDataType.Boolean,
            EntityKind.Number => EntityDataType.Number,
            EntityKind.Select => EntityDataType.Enum,
            EntityKind.Button or EntityKind.Text or EntityKind.Unsupported => EntityDataType.Text,
            _ => cls switch
            {
                DeviceClass.Timestamp => EntityDataType.Timestamp,
                DeviceClass.Enum => EntityDataType.Enum,
                _ when unit is not null || stateClass is not null || cls != DeviceClass.None => EntityDataType.Number,
                _ => EntityDataType.Text,
            },
        };

        var conversion = UnitConversion.Identity;
        var inferred = false;
        if (type == EntityDataType.Number)
        {
            var before = cls;
            (cls, unit, conversion) = DeviceClasses.Normalize(cls, unit);
            if (before != cls && raw is null) raw = before.ToString();
            precision = DeviceClasses.ConvertPrecision(precision, conversion);
            // Round 2: no state_class sent → measurement only for the classes that cannot be anything else, with a
            // recognised unit, not a diagnostic (never energy, never a number without a class).
            if (stateClass is null && kind == EntityKind.Sensor && category != EntityCategory.Diagnostic && DeviceClasses.ImpliesMeasurement(cls)
                && DeviceClasses.CanonicalUnit(cls) is not null && unit == DeviceClasses.CanonicalUnit(cls))
            {
                stateClass = Devices.Entities.StateClass.Measurement;
                inferred = true;
            }
        }

        var payloadOn = MqttJson.Text(cfg["payload_on"]) ?? "ON";
        var payloadOff = MqttJson.Text(cfg["payload_off"]) ?? "OFF";
        if (kind == EntityKind.Switch)
        {
            payloadOn = MqttJson.Text(cfg["state_on"]) ?? payloadOn;
            payloadOff = MqttJson.Text(cfg["state_off"]) ?? payloadOff;
        }

        var stateTopic = MqttJson.Text(cfg["state_topic"]);
        var commandTopic = MqttJson.Text(cfg["command_topic"]);
        EntityCommand? command = kind switch
        {
            EntityKind.Switch when commandTopic is not null => new EntityCommand(commandTopic, MqttJson.Text(cfg["payload_on"]) ?? "ON",
                MqttJson.Text(cfg["payload_off"]) ?? "OFF", Optimistic: stateTopic is null),
            EntityKind.Number when commandTopic is not null => new EntityCommand(commandTopic, Min: MqttJson.Number(cfg["min"]) ?? 1,
                Max: MqttJson.Number(cfg["max"]) ?? 100, Step: MqttJson.Number(cfg["step"]) ?? 1, Optimistic: stateTopic is null),
            EntityKind.Select when commandTopic is not null => new EntityCommand(commandTopic,
                Options: (cfg["options"] as JsonArray)?.Select(MqttJson.Text).Where(o => o is not null).Select(o => o!).ToList(), Optimistic: stateTopic is null),
            EntityKind.Button when commandTopic is not null => new EntityCommand(commandTopic, MqttJson.Text(cfg["payload_press"]) ?? "PRESS"),
            EntityKind.Text when commandTopic is not null => new EntityCommand(commandTopic, Optimistic: stateTopic is null),
            _ => null,
        };

        ValueTemplate? template = null;
        if (problem is null && MqttJson.Text(cfg["value_template"]) is { } tpl)
        {
            if (ValueTemplate.TryParse(tpl, out var t, out var err)) template = t;
            else problem = $"șablon neacceptat: {tpl} ({err})";
        }
        if (problem is null && stateTopic is null && kind is EntityKind.Sensor or EntityKind.Binary)
            problem = "fără state_topic";
        if (problem is null && HasWildcard(stateTopic)) problem = "topic cu wildcard: " + Cut(stateTopic!, 80);
        var attributesTopic = MqttJson.Text(cfg["json_attributes_topic"]);
        if (HasWildcard(attributesTopic)) attributesTopic = null;

        // R12: an explicit „name: null” is the device's main feature (HA: the device's name); an absent name is the class's.
        var explicitNull = cfg.ContainsKey("name") && cfg["name"] is null;
        var name = cfg["name"] is JsonValue ? MqttJson.Text(cfg["name"]) : null;
        if (string.IsNullOrWhiteSpace(name))
            name = explicitNull ? c.Device?.Name ?? (cls != DeviceClass.None ? DeviceClasses.Name(cls) : c.ObjectId)
                : cls != DeviceClass.None ? DeviceClasses.Name(cls) : c.Device?.Name ?? c.ObjectId;

        return new MqttEntitySpec(c.UniqueId!, c.Component, c.ConfigTopic, Cut(name!), kind, type, cls, raw is null ? null : Cut(raw, 64),
            unit is null ? null : Cut(unit, 32), conversion, stateClass, precision,
            category, command, problem, stateTopic, template, payloadOn, payloadOff, Availability(cfg), AvailabilityMode(cfg),
            MqttJson.Number(cfg["expire_after"]) is { } exp && exp > 0 ? (int)Math.Min(exp, int.MaxValue) : null, attributesTopic, c.Json, inferred);
    }

    private static IReadOnlyList<MqttAvailabilityTopic> Availability(JsonObject cfg)
    {
        var list = new List<MqttAvailabilityTopic>();
        // R7: an availability topic with a wildcard is ignored (never subscribed).
        if (MqttJson.Text(cfg["availability_topic"]) is { Length: > 0 } single && !HasWildcard(single))
            list.Add(new MqttAvailabilityTopic(single, Template(MqttJson.Text(cfg["availability_template"])),
                MqttJson.Text(cfg["payload_available"]) ?? "online", MqttJson.Text(cfg["payload_not_available"]) ?? "offline"));
        if (cfg["availability"] is JsonArray items)
            foreach (var item in items.OfType<JsonObject>())
                if (MqttJson.Text(item["topic"]) is { Length: > 0 } topic && !HasWildcard(topic))
                    list.Add(new MqttAvailabilityTopic(topic, Template(MqttJson.Text(item["value_template"])),
                        MqttJson.Text(item["payload_available"]) ?? "online", MqttJson.Text(item["payload_not_available"]) ?? "offline"));
        return list;
    }

    /// <summary>An availability template outside the subset is ignored (the raw payload is compared, as without a template).</summary>
    private static ValueTemplate? Template(string? t) => t is not null && ValueTemplate.TryParse(t, out var v, out _) ? v : null;

    private static MqttAvailabilityMode AvailabilityMode(JsonObject cfg) => MqttJson.Text(cfg["availability_mode"]) switch
    {
        "all" => MqttAvailabilityMode.All,
        "any" => MqttAvailabilityMode.Any,
        _ => MqttAvailabilityMode.Latest,
    };
}

/// <summary>
/// A value read from a state message, already in the canonical unit; <see cref="Error"/> = why the template failed on this
/// payload (counted per entity in memory, never stored, never a <see cref="MqttEntitySpec.Problem"/>: it may be passing).
/// </summary>
public readonly record struct MqttValue(double? Number, string? Text, string? Error = null)
{
    public bool HasValue => Number is not null || Text is not null;
}

/// <summary>Reads a state payload through the entity's template and payloads (0.11, pure).</summary>
public static class MqttValueParser
{
    private static readonly HashSet<string> NoValue = new(StringComparer.OrdinalIgnoreCase) { "", "unknown", "unavailable", "None", "null" };

    /// <summary>R7 (round 2): the longest text value kept.</summary>
    public const int MaxTextValue = 255;

    /// <summary>The value of a state message; <see cref="MqttValue.HasValue"/> false when it says nothing readable.</summary>
    public static MqttValue Parse(MqttEntitySpec spec, string payload)
    {
        if (spec.Problem is not null || spec.Kind == EntityKind.Button) return default;
        string? rendered = payload;
        if (spec.Template is { } t && !t.TryRender(payload, out rendered, out var error)) return new MqttValue(null, null, error ?? "eroare de evaluare");
        if (rendered is null) return default;
        if (spec.DataType == EntityDataType.Boolean)
            return rendered == spec.PayloadOn ? new MqttValue(1, null) : rendered == spec.PayloadOff ? new MqttValue(0, null) : default;
        var text = rendered.Trim();
        if (NoValue.Contains(text)) return default;
        if (spec.DataType == EntityDataType.Number)
            return DeviceClasses.ParseNumber(text) is { } n && double.IsFinite(spec.Conversion.Apply(n)) ? new MqttValue(spec.Conversion.Apply(n), null) : default;
        // R7: a text value is kept to 255 characters (stored on every change).
        return new MqttValue(null, text.Length <= MqttValueParser.MaxTextValue ? text : text[..MaxTextValue]);
    }

    /// <summary>The state of one availability topic for a payload: true / false, null when the payload is neither.</summary>
    public static bool? Availability(MqttAvailabilityTopic a, string payload)
    {
        var rendered = a.Template is { } t ? t.Render(payload) : payload;
        if (rendered is null) return null;
        rendered = rendered.Trim();
        return rendered == a.PayloadAvailable ? true : rendered == a.PayloadNotAvailable ? false : null;
    }
}

/// <summary>How fresh a value without availability nor expiry is (R4, round 2).</summary>
public enum MqttFreshness
{
    /// <summary>Within 2 expected intervals.</summary>
    Fresh,
    /// <summary>Within 6 expected intervals: „Întârziat” (still shown and exported).</summary>
    Late,
    /// <summary>Older: not available (no longer exported as current).</summary>
    Stale,
}

/// <summary>
/// Whether an entity's value can be trusted now (0.11, pure; R3/R4): its availability topics by <c>availability_mode</c>
/// (a topic never heard = not available, as in Home Assistant), then <c>expire_after</c> (a value older than it is no
/// longer valid). Without either (R4, round 2), the value's age against the entity's expected interval — the cadence
/// observed (median of the last gaps between live messages, 30 s … 12 h), else the default of its kind (1 h for a
/// number, 12 h for an on/off or a text, which publish on change) — gives fresh (≤ 2 intervals), late („Întârziat”,
/// ≤ 6 intervals) or stale (not available), the rule of the climate sensors' freshness.
/// </summary>
public static class MqttAvailabilityRule
{
    public const int FreshIntervals = 2;
    public const int LateIntervals = 6;
    public static readonly TimeSpan NumberInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan StateInterval = TimeSpan.FromHours(12);
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxInterval = TimeSpan.FromHours(12);

    /// <summary>The entity has neither availability topics nor <c>expire_after</c>: its age decides (<see cref="Freshness"/>).</summary>
    public static bool AgeDecides(MqttEntitySpec spec) => spec.Availability.Count == 0 && spec.ExpireAfterSeconds is null;

    public static TimeSpan Expected(MqttEntitySpec spec, TimeSpan? observedCadence)
    {
        if (observedCadence is { } c && c > TimeSpan.Zero) return c < MinInterval ? MinInterval : c > MaxInterval ? MaxInterval : c;
        return spec.DataType == KasaMonitor.Core.Devices.Entities.EntityDataType.Number ? NumberInterval : StateInterval;
    }

    public static MqttFreshness Freshness(DateTimeOffset? valueTs, DateTimeOffset now, TimeSpan expected)
    {
        if (valueTs is not { } ts) return MqttFreshness.Stale;
        var age = now - ts;
        return age <= expected * FreshIntervals ? MqttFreshness.Fresh : age <= expected * LateIntervals ? MqttFreshness.Late : MqttFreshness.Stale;
    }

    /// <summary>The median gap between the given arrival times (oldest first); null with fewer than 3 arrivals.</summary>
    public static TimeSpan? Cadence(IReadOnlyList<DateTimeOffset> arrivals)
    {
        if (arrivals.Count < 3) return null;
        var gaps = arrivals.Zip(arrivals.Skip(1), (a, b) => b - a).Where(g => g > TimeSpan.Zero).OrderBy(g => g).ToList();
        return gaps.Count == 0 ? null : gaps[gaps.Count / 2];
    }

    public static bool IsAvailable(MqttEntitySpec spec, IReadOnlyDictionary<string, (bool? State, DateTimeOffset Ts)> topics, DateTimeOffset? valueTs,
        DateTimeOffset now, TimeSpan? observedCadence = null)
    {
        if (spec.Availability.Count > 0)
        {
            var states = spec.Availability.Select(a => topics.TryGetValue(a.Topic, out var s) ? s : ((bool?)null, DateTimeOffset.MinValue)).ToList();
            var ok = spec.AvailabilityMode switch
            {
                MqttAvailabilityMode.All => states.All(s => s.Item1 == true),
                MqttAvailabilityMode.Any => states.Any(s => s.Item1 == true),
                _ => states.Where(s => s.Item1 is not null).OrderByDescending(s => s.Item2).Select(s => s.Item1).FirstOrDefault() == true,
            };
            if (!ok) return false;
        }
        if (spec.ExpireAfterSeconds is { } exp)
            return valueTs is { } ts && now - ts <= TimeSpan.FromSeconds(exp);
        if (spec.Availability.Count > 0) return true;
        return Freshness(valueTs, now, Expected(spec, observedCadence)) != MqttFreshness.Stale;
    }

    /// <summary>„Întârziat”: an entity whose age decides, older than 2 expected intervals but not stale.</summary>
    public static bool IsLate(MqttEntitySpec spec, DateTimeOffset? valueTs, DateTimeOffset now, TimeSpan? observedCadence = null) =>
        AgeDecides(spec) && Freshness(valueTs, now, Expected(spec, observedCadence)) == MqttFreshness.Late;
}
