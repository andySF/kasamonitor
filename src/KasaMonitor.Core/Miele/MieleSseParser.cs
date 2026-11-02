using System.Text;
using System.Text.Json;

namespace KasaMonitor.Core.Miele;

/// <summary>What an SSE event of the Miele stream carries.</summary>
public enum MieleEventKind
{
    /// <summary>Ident + state of one or all appliances (<c>event: devices</c> on the wire, „IDENT” in the docs).</summary>
    Devices,
    /// <summary>The actions an appliance accepts now (<c>event: actions</c>, „ACTION” in the docs); not used in 0.8.5.</summary>
    Actions,
    /// <summary>Heartbeat after ~20 s without another event (<c>event: ping</c>, „PING”).</summary>
    Ping,
    Unknown,
}

/// <summary>One dispatched event: its kind, the raw <c>event:</c> name (may be empty) and the joined <c>data:</c> lines.</summary>
public sealed record MieleSseEvent(MieleEventKind Kind, string Name, string Data);

/// <summary>
/// Incremental parser of a <c>text/event-stream</c> (WHATWG rules): <c>event:</c> and <c>data:</c> fields (one optional
/// space after the colon, several <c>data:</c> lines joined with LF), <c>id:</c>/<c>retry:</c> ignored, lines starting
/// with <c>:</c> are comments, an empty line dispatches. The wire format is not documented by Miele: the docs name the
/// events PING/IDENT/ACTION, pymiele reads lower-case <c>devices</c>/<c>actions</c>/<c>ping</c>; both are accepted (any
/// case), and an event without a name is classified by its data (a JSON object of appliances = devices).
/// </summary>
public sealed class MieleSseParser
{
    private string? _event;
    private readonly StringBuilder _data = new();
    private bool _hasData;

    /// <summary>Feeds one line (CR/LF already stripped or not); returns the event completed by an empty line, else null.</summary>
    public MieleSseEvent? Feed(string line)
    {
        line = line.TrimEnd('\r', '\n');
        if (line.Length == 0) return Dispatch();
        if (line[0] == ':') return null;
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "event":
                _event = value.Trim();
                break;
            case "data":
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
        }
        return null;
    }

    private MieleSseEvent? Dispatch()
    {
        if (_event is null && !_hasData) return null;
        var name = _event ?? "";
        var data = _data.ToString();
        _event = null;
        _data.Clear();
        _hasData = false;
        return new MieleSseEvent(KindOf(name, data), name, data);
    }

    /// <summary>The kind by name (case-insensitive, docs and wire names) or, without a known name, by the data.</summary>
    public static MieleEventKind KindOf(string? name, string data)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "devices" or "device" or "ident":
                return MieleEventKind.Devices;
            case "actions" or "action":
                return MieleEventKind.Actions;
            case "ping" or "heartbeat":
                return MieleEventKind.Ping;
            case "" or "message":
                if (string.IsNullOrWhiteSpace(data)) return MieleEventKind.Ping;
                return LooksLikeDevices(data) ? MieleEventKind.Devices : MieleEventKind.Unknown;
            default:
                return MieleEventKind.Unknown;
        }
    }

    /// <summary>A JSON object with <c>ident</c>/<c>state</c>, or whose values have them (keyed by device id).</summary>
    public static bool LooksLikeDevices(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("ident", out _) || root.TryGetProperty("state", out _)) return true;
            foreach (var p in root.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Object && (p.Value.TryGetProperty("ident", out _) || p.Value.TryGetProperty("state", out _)))
                    return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
