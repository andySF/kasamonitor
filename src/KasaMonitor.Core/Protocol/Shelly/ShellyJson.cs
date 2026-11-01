using System.Globalization;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>Null-tolerant accessors for Shelly JSON (missing or differently typed fields come back as null).</summary>
internal static class ShellyJson
{
    public static JsonElement? Obj(JsonElement? o, string name) =>
        o is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static double? Num(JsonElement? o, string name) =>
        o is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    public static string? Str(JsonElement? o, string name) =>
        o is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static bool? Bool(JsonElement? o, string name) =>
        o is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    /// <summary>Components of a GetStatus/GetConfig answer: "em1:0" -> ("em1", 0, element); keys without ":id" get id -1.</summary>
    public static IEnumerable<(string Type, int Id, JsonElement Value)> Components(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) yield break;
        foreach (var p in root.EnumerateObject())
        {
            var idx = p.Name.IndexOf(':');
            if (idx < 0) { yield return (p.Name, -1, p.Value); continue; }
            if (int.TryParse(p.Name.AsSpan(idx + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                yield return (p.Name[..idx], id, p.Value);
        }
    }

    public static JsonElement? Component(JsonElement? root, string type, int id) => Obj(root, $"{type}:{id}");
}
