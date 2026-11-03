using System.Text.Json.Nodes;

namespace KasaMonitor.Core.Tests.Mqtt;

/// <summary>The MQTT fixtures (fixtures/mqtt/*.jsonl, from documentation, see the README there): topic, payload text, retain.</summary>
public static class MqttFixtures
{
    public sealed record Message(string Topic, string Payload, bool Retain);

    public static IReadOnlyList<Message> Load(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "mqtt", file);
        return File.ReadAllLines(path).Where(l => l.Trim().Length > 0).Select(l =>
        {
            var o = JsonNode.Parse(l)!.AsObject();
            var p = o["payload"];
            var payload = p is JsonValue v && v.TryGetValue<string>(out var s) ? s : p?.ToJsonString() ?? "";
            return new Message(o["topic"]!.GetValue<string>(), payload, o["retain"]?.GetValue<bool>() ?? false);
        }).ToList();
    }

    public static IReadOnlyList<Message> All(params string[] files) => files.SelectMany(Load).ToList();
}
