using System.Globalization;
using System.Text.Json;

namespace KasaMonitor.Core.Climate;

/// <summary>Open-Meteo forecast API (free, no key): the „current” temperature and humidity at a location.</summary>
public static class OpenMeteoApi
{
    public const string ForecastUrl = "https://api.open-meteo.com/v1/forecast";
    /// <summary>Read every 15 minutes (96 requests a day); the „current” values are 15-minute slots.</summary>
    public static readonly TimeSpan PollInterval = ClimateFreshness.OpenMeteoInterval;
    /// <summary>The one Open-Meteo sensor („Afară”); changing the location keeps its history.</summary>
    public const string OutdoorExternalId = "outdoor";

    public static string CurrentUrl(double latitude, double longitude) =>
        ForecastUrl + "?latitude=" + latitude.ToString("0.####", CultureInfo.InvariantCulture)
        + "&longitude=" + longitude.ToString("0.####", CultureInfo.InvariantCulture)
        + "&current=temperature_2m,relative_humidity_2m&timezone=auto";
}

/// <summary>The „current” block: <see cref="TimeUtc"/> = start of the slot (local time of the answer minus its UTC offset).</summary>
public sealed record OpenMeteoCurrent(DateTimeOffset? TimeUtc, double TemperatureC, double? HumidityPercent);

public sealed class OpenMeteoException : Exception
{
    public OpenMeteoException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class OpenMeteoParser
{
    /// <summary>
    /// Parses <c>current.temperature_2m</c> (°C; a <c>current_units</c> in °F is converted) and
    /// <c>current.relative_humidity_2m</c>. An <c>"error": true</c> answer or a missing temperature throws.
    /// </summary>
    public static OpenMeteoCurrent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new OpenMeteoException("Open-Meteo: răspunsul nu e un obiect JSON");
        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.True)
            throw new OpenMeteoException("Open-Meteo: " + (Str(root, "reason") ?? "eroare"));
        if (!root.TryGetProperty("current", out var current) || current.ValueKind != JsonValueKind.Object)
            throw new OpenMeteoException("Open-Meteo: lipsește blocul current");
        if (Num(current, "temperature_2m") is not { } temp)
            throw new OpenMeteoException("Open-Meteo: lipsește temperature_2m");
        var unit = root.TryGetProperty("current_units", out var units) ? Str(units, "temperature_2m") : null;
        if (unit is not null && unit.Contains('F', StringComparison.OrdinalIgnoreCase)) temp = GoveeParser.FahrenheitToCelsius(temp);

        DateTimeOffset? time = null;
        if (Str(current, "time") is { } t
            && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var local))
        {
            var offset = Num(root, "utc_offset_seconds") ?? 0;
            time = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)).AddSeconds(-offset);
        }
        return new OpenMeteoCurrent(time, temp, Num(current, "relative_humidity_2m"));
    }

    private static double? Num(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public interface IOpenMeteoClient
{
    Task<OpenMeteoCurrent> GetCurrentAsync(double latitude, double longitude, CancellationToken ct);
}

public sealed class OpenMeteoClient : IOpenMeteoClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    /// <param name="handler">Test hook (fake server); production uses the default handler.</param>
    public OpenMeteoClient(TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("KasaMonitor");
        _timeout = timeout;
    }

    public async Task<OpenMeteoCurrent> GetCurrentAsync(double latitude, double longitude, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await _http.GetAsync(OpenMeteoApi.CurrentUrl(latitude, longitude), cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new OpenMeteoException($"Open-Meteo: HTTP {(int)resp.StatusCode}" + (ErrorReason(body) is { } r ? " (" + r + ")" : ""));
        return OpenMeteoParser.Parse(body);
    }

    /// <summary>The <c>reason</c> of an error answer (<c>{"error":true,"reason":…}</c>), if the body is one.</summary>
    private static string? ErrorReason(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("reason", out var r)
                   && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
