using System.Text.Json;
using System.Text.Json.Nodes;

namespace KasaMonitor.Core.Mqtt;

/// <summary>The <c>device</c> block of a discovery payload (abbreviations expanded).</summary>
public sealed record MqttDeviceInfo(
    IReadOnlyList<string> Identifiers,
    IReadOnlyList<(string Type, string Value)> Connections,
    string? Name,
    string? Manufacturer,
    string? Model,
    string? SwVersion,
    string? HwVersion,
    string? ViaDevice,
    string? SuggestedArea)
{
    /// <summary>
    /// The identity of the physical device: its first identifier, else its first connection (<c>mac:aabbccddee00</c>);
    /// null when the block has neither.
    /// </summary>
    public string? ExternalId => Identifiers.Count > 0 ? Identifiers[0]
        : Connections.Count > 0 ? $"{Connections[0].Type}:{Connections[0].Value}" : null;

    public static MqttDeviceInfo? From(JsonObject? o)
    {
        if (o is null) return null;
        var ids = Strings(o["identifiers"]);
        var cns = new List<(string, string)>();
        if (o["connections"] is JsonArray arr)
            foreach (var c in arr.OfType<JsonArray>())
                if (c.Count >= 2) cns.Add((MqttJson.Text(c[0]) ?? "", MqttJson.Text(c[1]) ?? ""));
        return new MqttDeviceInfo(ids, cns, MqttJson.Text(o["name"]), MqttJson.Text(o["manufacturer"]), MqttJson.Text(o["model"]),
            MqttJson.Text(o["sw_version"]), MqttJson.Text(o["hw_version"]), MqttJson.Text(o["via_device"]), MqttJson.Text(o["suggested_area"]));
    }

    private static IReadOnlyList<string> Strings(JsonNode? n) => n switch
    {
        JsonArray a => a.Select(MqttJson.Text).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList(),
        JsonValue v when MqttJson.Text(v) is { Length: > 0 } s => new[] { s },
        _ => Array.Empty<string>(),
    };
}

/// <summary>
/// One component of a discovery message, abbreviations expanded and <c>~</c> substituted: <see cref="Component"/> =
/// <c>sensor</c>, <c>binary_sensor</c>, <c>switch</c> …; <see cref="Config"/> = its full configuration (in the
/// device-based format: the shared root options merged in, the component's own winning).
/// </summary>
public sealed record MqttDiscoveredComponent(
    string ConfigTopic,
    string Component,
    string? NodeId,
    string ObjectId,
    string? UniqueId,
    JsonObject Config,
    MqttDeviceInfo? Device)
{
    /// <summary>The device this component belongs to: its device's identity, else one device per entity (<c>entity:&lt;unique_id&gt;</c>).</summary>
    public string? DeviceExternalId => Device?.ExternalId ?? (UniqueId is { } u ? "entity:" + u : null);

    /// <summary>The stored form of the configuration (diagnostic and re-parsing after an upgrade).</summary>
    public string Json => Config.ToJsonString();
}

public enum MqttDiscoveryKind
{
    /// <summary>Not a discovery topic (another topic under the prefix: <c>homeassistant/status</c> …).</summary>
    NotDiscovery,
    /// <summary>An empty retained payload: the entity (or the device's entities) was removed by its publisher.</summary>
    Removed,
    Config,
    /// <summary>A discovery topic whose payload cannot be read (not JSON, no component): shown with its reason.</summary>
    Invalid,
}

/// <summary>What one message under the discovery prefix says.</summary>
public sealed record MqttDiscoveryResult(MqttDiscoveryKind Kind, string Topic, IReadOnlyList<MqttDiscoveredComponent> Components, string? Error = null)
{
    /// <summary>For <see cref="MqttDiscoveryKind.Removed"/> and the device-based format: the component type of the topic (<c>device</c>).</summary>
    public string? TopicComponent { get; init; }
}

/// <summary>JSON helpers that follow Home Assistant's string conversions (a JSON <c>true</c> is Python's <c>"True"</c>).</summary>
public static class MqttJson
{
    /// <summary>A scalar as the text Home Assistant compares (payloads, identifiers); null for null / objects.</summary>
    public static string? Text(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        return v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Number => v.ToJsonString(),
            _ => null,
        };
    }

    public static double? Number(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>()
        : n is JsonValue s && s.GetValueKind() == JsonValueKind.String && double.TryParse(s.GetValue<string>(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    public static bool? Bool(JsonNode? n) => n is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => v.GetValue<string>().Trim().ToLowerInvariant() is "true" or "on" or "yes" or "1" ? true
            : v.GetValue<string>().Trim().ToLowerInvariant() is "false" or "off" or "no" or "0" ? false : null,
        _ => null,
    } : null;
}

/// <summary>
/// The parser of MQTT Discovery messages (0.11, pure): both formats — per entity
/// <c>&lt;prefix&gt;/&lt;component&gt;/[&lt;node_id&gt;/]&lt;object_id&gt;/config</c> and per device
/// <c>&lt;prefix&gt;/device/[&lt;node_id&gt;/]&lt;object_id&gt;/config</c> with <c>components</c> — the abbreviations
/// (<see cref="MqttAbbreviations"/>), the <c>~</c> topic base and the empty payload of a removal.
/// </summary>
public static class MqttDiscoveryParser
{
    public const string DefaultPrefix = "homeassistant";
    public const string DeviceComponent = "device";

    public static MqttDiscoveryResult Parse(string topic, string? payload, string prefix = DefaultPrefix)
    {
        var parts = topic.Split('/');
        var pre = prefix.Trim('/').Split('/');
        // <prefix...>/<component>/[<node_id>/]<object_id>/config
        if (parts.Length < pre.Length + 3 || parts.Length > pre.Length + 4 || parts[^1] != "config"
            || !parts.Take(pre.Length).SequenceEqual(pre, StringComparer.Ordinal))
            return new(MqttDiscoveryKind.NotDiscovery, topic, Array.Empty<MqttDiscoveredComponent>());
        var component = parts[pre.Length];
        var nodeId = parts.Length == pre.Length + 4 ? parts[pre.Length + 1] : null;
        var objectId = parts[^2];
        if (string.IsNullOrWhiteSpace(payload))
            return new(MqttDiscoveryKind.Removed, topic, Array.Empty<MqttDiscoveredComponent>()) { TopicComponent = component };

        JsonObject root;
        try
        {
            root = JsonNode.Parse(payload) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            return new(MqttDiscoveryKind.Invalid, topic, Array.Empty<MqttDiscoveredComponent>(), "payload JSON invalid: " + ex.Message) { TopicComponent = component };
        }
        Expand(root, MqttAbbreviations.Entity);
        var device = root["device"] as JsonObject;
        if (device is not null) Expand(device, MqttAbbreviations.Device);
        if (root["origin"] is JsonObject origin) Expand(origin, MqttAbbreviations.Origin);
        var deviceInfo = MqttDeviceInfo.From(device);

        if (component == DeviceComponent)
        {
            if (root["components"] is not JsonObject cmps || cmps.Count == 0)
                return new(MqttDiscoveryKind.Invalid, topic, Array.Empty<MqttDiscoveredComponent>(), "discovery pe dispozitiv fără „components”") { TopicComponent = component };
            var shared = new JsonObject();
            foreach (var (k, v) in root)
                if (k is not ("device" or "origin" or "components")) shared[k] = v?.DeepClone();
            var list = new List<MqttDiscoveredComponent>();
            foreach (var (id, node) in cmps)
            {
                if (node is not JsonObject c) continue;
                var cfg = (JsonObject)c.DeepClone();
                Expand(cfg, MqttAbbreviations.Entity);
                foreach (var (k, v) in shared)
                    if (!cfg.ContainsKey(k)) cfg[k] = v?.DeepClone();
                SubstituteBase(cfg);
                var platform = MqttJson.Text(cfg["platform"]) ?? "";
                list.Add(new MqttDiscoveredComponent(topic, platform, nodeId, id, MqttJson.Text(cfg["unique_id"]), cfg, deviceInfo));
            }
            return new(MqttDiscoveryKind.Config, topic, list) { TopicComponent = component };
        }

        var single = (JsonObject)root.DeepClone();
        SubstituteBase(single);
        single.Remove("components");
        return new(MqttDiscoveryKind.Config, topic,
            new[] { new MqttDiscoveredComponent(topic, component, nodeId, objectId, MqttJson.Text(single["unique_id"]), single, deviceInfo) })
            { TopicComponent = component };
    }

    /// <summary>Renames abbreviated keys in place; the items of <c>availability</c> use the same table (<c>t</c> → <c>topic</c>).</summary>
    public static void Expand(JsonObject o, IReadOnlyDictionary<string, string> table)
    {
        foreach (var key in o.Select(kv => kv.Key).ToList())
        {
            if (!table.TryGetValue(key, out var full) || full == key || o.ContainsKey(full)) continue;
            var value = o[key];
            o.Remove(key);
            o[full] = value;
        }
        if (o["availability"] is JsonArray avail)
            foreach (var item in avail.OfType<JsonObject>()) Expand(item, MqttAbbreviations.Entity);
    }

    /// <summary>
    /// The <c>~</c> topic base, as Home Assistant applies it: in every key ending in <c>topic</c> (and in the
    /// <c>topic</c> of the <c>availability</c> items) a leading <c>~</c> becomes the base prefix, a trailing one the base suffix.
    /// </summary>
    public static void SubstituteBase(JsonObject o)
    {
        if (MqttJson.Text(o["~"]) is not { } b) return;
        o.Remove("~");
        foreach (var key in o.Select(kv => kv.Key).Where(k => k.EndsWith("topic", StringComparison.Ordinal)).ToList())
            if (MqttJson.Text(o[key]) is { Length: > 0 } t) o[key] = Apply(t, b);
        if (o["availability"] is JsonArray avail)
            foreach (var item in avail.OfType<JsonObject>())
                if (MqttJson.Text(item["topic"]) is { Length: > 0 } t) item["topic"] = Apply(t, b);
    }

    private static string Apply(string topic, string b)
    {
        if (topic[0] == '~') topic = b + topic[1..];
        if (topic.Length > 0 && topic[^1] == '~') topic = topic[..^1] + b;
        return topic;
    }
}
