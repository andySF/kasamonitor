using System.Globalization;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers;

/// <summary>Keys of the climate settings in the <c>Settings</c> table. The Govee API key is never stored there.</summary>
public static class ClimateSettingKeys
{
    /// <summary>Latitude for Open-Meteo (invariant decimal); empty = Open-Meteo off.</summary>
    public const string ClimateLatitude = "ClimateLatitude";
    /// <summary>Longitude for Open-Meteo (invariant decimal); empty = Open-Meteo off.</summary>
    public const string ClimateLongitude = "ClimateLongitude";
    /// <summary>Govee device ids ticked „acasă” in /settings, comma-separated; only these are read and stored.</summary>
    public const string GoveeHomeDevices = "GoveeHomeDevices";
}

/// <summary>
/// Values from configuration: initial latitude/longitude (the database wins once saved) and where the Govee API key
/// comes from: <c>KasaMonitor__GoveeApiKey</c> (env) or the file <c>KasaMonitor__GoveeApiKeyFile</c> /
/// <c>GOVEE_API_KEY_FILE</c> (default <c>/run/secrets/govee-api.key</c>), read once at startup.
/// </summary>
public sealed record ClimateSettingsDefaults(
    double? Latitude = null,
    double? Longitude = null,
    string GoveeApiKeyFile = GoveeApi.DefaultKeyFile,
    string? GoveeApiKey = null);

/// <summary>
/// The Govee API key, loaded once at startup. Only the climate worker reads <see cref="Value"/>; it is never stored in
/// the database, shown or logged (<see cref="ToString"/> hides it).
/// </summary>
public sealed class GoveeApiKey
{
    public string? Value { get; }
    /// <summary>Where it came from („variabila KasaMonitor__GoveeApiKey” / „fișierul …”), for the UI.</summary>
    public string? Source { get; }
    /// <summary>Why there is none (file missing, empty, unreadable), for the UI.</summary>
    public string? Problem { get; }

    public GoveeApiKey(string? value, string? source, string? problem)
    {
        Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Source = Value is null ? null : source;
        Problem = Value is null ? problem ?? "cheia lipsește" : null;
    }

    public bool IsSet => Value is not null;

    public override string ToString() => IsSet ? "GoveeApiKey(set, " + Source + ")" : "GoveeApiKey(missing)";

    /// <summary>The environment value wins; otherwise the file (trimmed). <paramref name="readFile"/> is a test hook.</summary>
    public static GoveeApiKey Load(ClimateSettingsDefaults d, Func<string, string?>? readFile = null)
    {
        if (!string.IsNullOrWhiteSpace(d.GoveeApiKey))
            return new GoveeApiKey(d.GoveeApiKey, "variabila KasaMonitor__GoveeApiKey", null);
        var path = string.IsNullOrWhiteSpace(d.GoveeApiKeyFile) ? GoveeApi.DefaultKeyFile : d.GoveeApiKeyFile.Trim();
        readFile ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        try
        {
            var text = readFile(path);
            if (text is null) return new GoveeApiKey(null, null, "fișierul " + path + " nu există");
            return new GoveeApiKey(text, "fișierul " + path, "fișierul " + path + " e gol");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new GoveeApiKey(null, null, "fișierul " + path + " nu poate fi citit (" + ex.GetType().Name + ")");
        }
    }
}

/// <summary>Effective climate settings.</summary>
public sealed record ClimateRuntimeSettings(
    double? Latitude,
    double? Longitude,
    IReadOnlySet<string> GoveeHomeDevices,
    bool HasGoveeKey,
    string? GoveeKeySource,
    string? GoveeKeyProblem)
{
    /// <summary>Latitude and longitude set: Open-Meteo is read.</summary>
    public bool HasLocation => Latitude is not null && Longitude is not null;
}

public interface IClimateSettingsService
{
    Task<ClimateRuntimeSettings> GetAsync(CancellationToken ct);
    /// <summary>The Govee API key (only for the Govee requests).</summary>
    string? GoveeApiKey { get; }
    /// <summary>Saves the location (both null = Open-Meteo off). Out-of-range values or only one of them throw <see cref="ArgumentException"/>.</summary>
    Task SaveLocationAsync(double? latitude, double? longitude, CancellationToken ct);
    /// <summary>Ticks / unticks a Govee device as „acasă”.</summary>
    Task SetGoveeHomeAsync(string deviceId, bool home, CancellationToken ct);
}

public sealed class ClimateSettingsService : IClimateSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClimateSettingsDefaults _defaults;
    private readonly GoveeApiKey _key;

    public ClimateSettingsService(IServiceScopeFactory scopeFactory, ClimateSettingsDefaults defaults, GoveeApiKey? key = null)
    {
        _scopeFactory = scopeFactory;
        _defaults = defaults;
        _key = key ?? KasaMonitor.Workers.GoveeApiKey.Load(defaults);
    }

    public string? GoveeApiKey => _key.Value;

    public async Task<ClimateRuntimeSettings> GetAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> db;
        using (var scope = _scopeFactory.CreateScope())
            db = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAllAsync(ct);
        double? lat = _defaults.Latitude, lon = _defaults.Longitude;
        if (db.TryGetValue(ClimateSettingKeys.ClimateLatitude, out var la)) lat = ParseCoordinate(la);
        if (db.TryGetValue(ClimateSettingKeys.ClimateLongitude, out var lo)) lon = ParseCoordinate(lo);
        var home = db.TryGetValue(ClimateSettingKeys.GoveeHomeDevices, out var h) ? ParseList(h) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new ClimateRuntimeSettings(lat, lon, home, _key.IsSet, _key.Source, _key.Problem);
    }

    public async Task SaveLocationAsync(double? latitude, double? longitude, CancellationToken ct)
    {
        if (latitude is null != longitude is null)
            throw new ArgumentException("Completează și latitudinea, și longitudinea (sau lasă-le pe amândouă goale).");
        if (latitude is { } la && (double.IsNaN(la) || la < -90 || la > 90))
            throw new ArgumentException("Latitudinea trebuie să fie între -90 și 90.");
        if (longitude is { } lo && (double.IsNaN(lo) || lo < -180 || lo > 180))
            throw new ArgumentException("Longitudinea trebuie să fie între -180 și 180.");
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync(ClimateSettingKeys.ClimateLatitude, Format(latitude), ct);
        await repo.SetAsync(ClimateSettingKeys.ClimateLongitude, Format(longitude), ct);
    }

    public async Task SetGoveeHomeAsync(string deviceId, bool home, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("Dispozitiv Govee necunoscut.", nameof(deviceId));
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        var set = ParseList(await repo.GetAsync(ClimateSettingKeys.GoveeHomeDevices, ct));
        if (home) set.Add(deviceId.Trim());
        else set.Remove(deviceId.Trim());
        await repo.SetAsync(ClimateSettingKeys.GoveeHomeDevices, string.Join(",", set.OrderBy(x => x, StringComparer.Ordinal)), ct);
    }

    /// <summary>An invariant decimal (a comma is accepted as the decimal separator); empty or invalid = null.</summary>
    public static double? ParseCoordinate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && !double.IsNaN(v) ? v : null;
    }

    private static string Format(double? v) => v is { } x ? Math.Round(x, 5).ToString("0.#####", CultureInfo.InvariantCulture) : "";

    private static HashSet<string> ParseList(string? text) =>
        new((text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
}
