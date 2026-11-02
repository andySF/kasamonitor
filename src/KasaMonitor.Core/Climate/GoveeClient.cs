using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KasaMonitor.Core.Climate;

/// <summary>Constants of the Govee Developer API v1 (openapi.api.govee.com, header <c>Govee-API-Key</c>).</summary>
public static class GoveeApi
{
    public const string BaseUrl = "https://openapi.api.govee.com/router/api/v1";
    public const string DevicesUrl = BaseUrl + "/user/devices";
    public const string StateUrl = BaseUrl + "/device/state";
    public const string KeyHeader = "Govee-API-Key";
    /// <summary>Where the key is read from when nothing else is configured (a file mounted read-only).</summary>
    public const string DefaultKeyFile = "/run/secrets/govee-api.key";
    /// <summary>Environment variable naming another key file (the same name as in the sff stack).</summary>
    public const string KeyFileEnvironmentVariable = "GOVEE_API_KEY_FILE";
    /// <summary>Govee's limit per account and day.</summary>
    public const int DailyRequestLimit = 10_000;
    /// <summary>One state request per selected thermometer every 10 minutes (144 per thermometer and day).</summary>
    public static readonly TimeSpan PollInterval = ClimateFreshness.GoveeInterval;
    /// <summary>The account's device list is re-read every 6 hours (and on demand from /settings).</summary>
    public static readonly TimeSpan DeviceListInterval = TimeSpan.FromHours(6);

    public const string InstanceOnline = "online";
    public const string InstanceTemperature = "sensorTemperature";
    public const string InstanceHumidity = "sensorHumidity";
    public const string InstanceBattery = "battery";

    /// <summary>Requests per day for <paramref name="thermometers"/> selected devices (state every 10 min + the list every 6 h).</summary>
    public static int RequestsPerDay(int thermometers) =>
        thermometers * (int)(TimeSpan.FromDays(1) / PollInterval) + (int)(TimeSpan.FromDays(1) / DeviceListInterval);
}

/// <summary>A device of the Govee account (<c>GET /user/devices</c>). <see cref="Device"/> is Govee's device id (MAC-like).</summary>
public sealed record GoveeDevice(string Sku, string Device, string Name, string Type, IReadOnlyList<string> Instances)
{
    /// <summary>It reports a temperature (thermometers, hygrometers).</summary>
    public bool IsThermometer => Instances.Contains(GoveeApi.InstanceTemperature, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The state of one device (<c>POST /device/state</c>): <see cref="Online"/> from the <c>online</c> capability (null when
/// absent), the temperature converted from °F to °C, the humidity in % RH; an offline device answers <c>""</c> (null here).
/// </summary>
public sealed record GoveeState(bool? Online, double? TemperatureC, double? HumidityPercent, int? BatteryPercent);

/// <summary>A Govee error; the message never contains the API key.</summary>
public class GoveeException : Exception
{
    public int? StatusCode { get; }
    public GoveeException(string message, int? statusCode = null, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;
}

/// <summary>HTTP 429 (or code 429 in the body): the account's limit is reached.</summary>
public sealed class GoveeRateLimitException : GoveeException
{
    public GoveeRateLimitException(string message) : base(message, 429) { }
}

public static class GoveeParser
{
    /// <summary>°F → °C, rounded to 0.01.</summary>
    public static double FahrenheitToCelsius(double f) => Math.Round((f - 32) * 5 / 9, 2);

    /// <summary>The <c>data</c> array of <c>GET /user/devices</c>. Throws <see cref="GoveeException"/> on a <c>code</c> other than 200.</summary>
    public static IReadOnlyList<GoveeDevice> ParseDevices(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        CheckCode(root, "devices");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return Array.Empty<GoveeDevice>();
        var result = new List<GoveeDevice>();
        foreach (var d in data.EnumerateArray())
        {
            var sku = Str(d, "sku");
            var device = Str(d, "device");
            if (string.IsNullOrWhiteSpace(sku) || string.IsNullOrWhiteSpace(device)) continue;
            var instances = new List<string>();
            if (d.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                foreach (var c in caps.EnumerateArray())
                    if (Str(c, "instance") is { Length: > 0 } inst) instances.Add(inst);
            result.Add(new GoveeDevice(sku, device, Str(d, "deviceName") ?? sku, Str(d, "type") ?? "", instances));
        }
        return result;
    }

    /// <summary>The <c>payload.capabilities</c> of <c>POST /device/state</c>.</summary>
    public static GoveeState ParseState(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        CheckCode(root, "state");
        bool? online = null;
        double? tempF = null, rh = null, battery = null;
        if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in caps.EnumerateArray())
            {
                if (!c.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object
                    || !state.TryGetProperty("value", out var value)) continue;
                switch (Str(c, "instance"))
                {
                    case GoveeApi.InstanceOnline:
                        online = value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => online };
                        break;
                    case GoveeApi.InstanceTemperature:
                        tempF = Number(value);
                        break;
                    case GoveeApi.InstanceHumidity:
                        rh = Number(value);
                        break;
                    case GoveeApi.InstanceBattery:
                        battery = Number(value);
                        break;
                }
            }
        }
        return new GoveeState(online, tempF is { } f ? FahrenheitToCelsius(f) : null, rh is { } h ? Math.Round(h, 1) : null,
            battery is { } b ? (int)Math.Round(Math.Clamp(b, 0, 100)) : null);
    }

    /// <summary>A number, or a numeric string; <c>""</c> (offline) and anything else are null.</summary>
    private static double? Number(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static void CheckCode(JsonElement root, string what)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new GoveeException($"Govee {what}: răspunsul nu e un obiect JSON");
        if (!root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number) return;
        var c = code.GetInt32();
        if (c == 200) return;
        var msg = Str(root, "message") ?? Str(root, "msg") ?? "";
        if (c == 429) throw new GoveeRateLimitException($"Govee {what}: limita de cereri atinsă {msg}".TrimEnd());
        throw new GoveeException($"Govee {what}: cod {c} {msg}".TrimEnd(), c);
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public interface IGoveeClient
{
    /// <summary><c>GET /user/devices</c>: every device of the account.</summary>
    Task<IReadOnlyList<GoveeDevice>> ListDevicesAsync(string apiKey, CancellationToken ct);

    /// <summary><c>POST /device/state</c> of one device.</summary>
    Task<GoveeState> GetStateAsync(string apiKey, string sku, string device, CancellationToken ct);
}

/// <summary>HTTP client of the Govee Developer API. Read-only: no control command is ever sent. The key is never logged.</summary>
public sealed class GoveeClient : IGoveeClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <param name="handler">Test hook (fake server); production uses the default handler.</param>
    public GoveeClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KasaMonitor");
        _timeout = timeout;
    }

    public async Task<IReadOnlyList<GoveeDevice>> ListDevicesAsync(string apiKey, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GoveeApi.DevicesUrl);
        return GoveeParser.ParseDevices(await SendAsync(req, apiKey, "devices", ct));
    }

    public async Task<GoveeState> GetStateAsync(string apiKey, string sku, string device, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            requestId = Guid.NewGuid().ToString(),
            payload = new { sku, device },
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, GoveeApi.StateUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return GoveeParser.ParseState(await SendAsync(req, apiKey, "state", ct));
    }

    private async Task<string> SendAsync(HttpRequestMessage req, string apiKey, string what, CancellationToken ct)
    {
        req.Headers.TryAddWithoutValidation(GoveeApi.KeyHeader, apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await _http.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            throw new GoveeRateLimitException($"Govee {what}: HTTP 429 (limita de cereri atinsă)");
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new GoveeException($"Govee {what}: HTTP {(int)resp.StatusCode} (cheia API e refuzată)", (int)resp.StatusCode);
        if (!resp.IsSuccessStatusCode)
            throw new GoveeException($"Govee {what}: HTTP {(int)resp.StatusCode}", (int)resp.StatusCode);
        return text;
    }
}
