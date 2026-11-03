using System.Globalization;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Mqtt;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers.Mqtt;

/// <summary>Keys of the MQTT settings in the <c>Settings</c> table (0.11). The password is never stored there.</summary>
public static class MqttSettingKeys
{
    public const string Enabled = "MqttEnabled";
    public const string Host = "MqttHost";
    public const string Port = "MqttPort";
    public const string User = "MqttUser";
    public const string UseTls = "MqttUseTls";
    public const string DiscoveryPrefix = "MqttDiscoveryPrefix";
    public const string AutoAdd = "MqttAutoAdd";
    /// <summary>Days the raw generic readings are kept (the hourly rollups are kept forever); 0 = never deleted.</summary>
    public const string RawRetentionDays = "GenericRawRetentionDays";
    /// <summary>Safety limit of the generic series on /metrics (a warning in the log and on the MQTT tab above it).</summary>
    public const string MetricsSeriesLimit = "GenericMetricsSeriesLimit";
}

/// <summary>
/// Values from configuration: the broker (used while the database has none) and where the password comes from:
/// <c>KasaMonitor__MqttPassword</c> (env) or the file <c>KasaMonitor__MqttPasswordFile</c> (default
/// <c>/run/secrets/mqtt-kasamonitor.password</c>), read once at start-up — the Miele / Govee model.
/// </summary>
public sealed record MqttSettingsDefaults(
    string Host = "",
    int Port = MqttSettingsService.DefaultPort,
    string User = "",
    string PasswordFile = MqttSettingsService.DefaultPasswordFile,
    string? Password = null);

/// <summary>The broker password, loaded once; only the transport reads <see cref="Value"/> (<see cref="ToString"/> hides it).</summary>
public sealed class MqttPassword
{
    public string? Value { get; }
    /// <summary>Where it came from, for the UI („fișierul …” / „variabila …”).</summary>
    public string? Source { get; }
    /// <summary>Why there is none, for the UI.</summary>
    public string? Problem { get; }

    public MqttPassword(string? value, string? source, string? problem)
    {
        Value = string.IsNullOrEmpty(value) ? null : value.TrimEnd('\r', '\n');
        Source = Value is null ? null : source;
        Problem = Value is null ? problem ?? "parola lipsește" : null;
    }

    public bool IsSet => Value is not null;

    public override string ToString() => IsSet ? "MqttPassword(set, " + Source + ")" : "MqttPassword(missing)";

    /// <summary>The environment value wins; otherwise the file (trailing new line removed). <paramref name="readFile"/> is a test hook.</summary>
    public static MqttPassword Load(MqttSettingsDefaults d, Func<string, string?>? readFile = null)
    {
        if (!string.IsNullOrEmpty(d.Password)) return new MqttPassword(d.Password, "variabila KasaMonitor__MqttPassword", null);
        var path = string.IsNullOrWhiteSpace(d.PasswordFile) ? MqttSettingsService.DefaultPasswordFile : d.PasswordFile.Trim();
        readFile ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        try
        {
            var text = readFile(path);
            return text is null
                ? new MqttPassword(null, null, "fișierul " + path + " nu există")
                : new MqttPassword(text, "fișierul " + path, "fișierul " + path + " e gol");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MqttPassword(null, null, "fișierul " + path + " nu poate fi citit (" + ex.GetType().Name + ")");
        }
    }
}

/// <summary>Effective MQTT settings; the password only as „present / where from / why not”.</summary>
public sealed record MqttRuntimeSettings(
    bool Enabled,
    string Host,
    int Port,
    string User,
    bool HasPassword,
    string? PasswordSource,
    string? PasswordProblem,
    bool UseTls,
    string DiscoveryPrefix,
    bool AutoAdd,
    int RawRetentionDays,
    int MetricsSeriesLimit)
{
    public bool IsConfigured => Host.Length > 0;
    public string Broker => $"{Host}:{Port}";
}

/// <summary>A partial save of the MQTT tab: null = unchanged.</summary>
public sealed record MqttSettingsUpdate(
    bool? Enabled = null,
    string? Host = null,
    int? Port = null,
    string? User = null,
    bool? UseTls = null,
    string? DiscoveryPrefix = null,
    bool? AutoAdd = null,
    int? RawRetentionDays = null,
    int? MetricsSeriesLimit = null);

public interface IMqttSettingsService
{
    Task<MqttRuntimeSettings> GetAsync(CancellationToken ct);
    /// <summary>Validates and saves; throws <see cref="ArgumentException"/> with a Romanian message, before any write.</summary>
    Task SaveAsync(MqttSettingsUpdate update, CancellationToken ct);
    /// <summary>The password (only for the transport).</summary>
    string? Password { get; }
    /// <summary>Raised after a save (the worker reconnects with the new settings).</summary>
    event Action? Changed;
}

public sealed class MqttSettingsService : IMqttSettingsService
{
    public const int DefaultPort = 1883;
    public const string DefaultPasswordFile = "/run/secrets/mqtt-kasamonitor.password";
    public const int DefaultRawRetentionDays = 90;
    public const int DefaultMetricsSeriesLimit = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly MqttSettingsDefaults _defaults;
    private readonly MqttPassword _password;

    public MqttSettingsService(IServiceScopeFactory scopes, MqttSettingsDefaults defaults, MqttPassword? password = null)
    {
        _scopes = scopes;
        _defaults = defaults;
        _password = password ?? MqttPassword.Load(defaults);
    }

    public string? Password => _password.Value;

    public event Action? Changed;

    public async Task<MqttRuntimeSettings> GetAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> db;
        using (var scope = _scopes.CreateScope())
            db = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAllAsync(ct);
        string Text(string key, string fallback) => db.TryGetValue(key, out var v) ? v : fallback;
        int Int(string key, int fallback) => db.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
        bool Bool(string key, bool fallback) => db.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : fallback;
        return new MqttRuntimeSettings(
            Bool(MqttSettingKeys.Enabled, true),
            Text(MqttSettingKeys.Host, _defaults.Host).Trim(),
            Int(MqttSettingKeys.Port, _defaults.Port),
            Text(MqttSettingKeys.User, _defaults.User).Trim(),
            _password.IsSet, _password.Source, _password.Problem,
            Bool(MqttSettingKeys.UseTls, false),
            Text(MqttSettingKeys.DiscoveryPrefix, MqttDiscoveryParser.DefaultPrefix).Trim(),
            Bool(MqttSettingKeys.AutoAdd, false),
            Int(MqttSettingKeys.RawRetentionDays, DefaultRawRetentionDays),
            Int(MqttSettingKeys.MetricsSeriesLimit, DefaultMetricsSeriesLimit));
    }

    /// <summary>The checks of a save, in Romanian; empty when valid (pure, tested).</summary>
    public static IReadOnlyList<string> Validate(MqttSettingsUpdate u)
    {
        var errors = new List<string>();
        if (u.Host is { } h && (h.Trim().Contains(' ') || h.Contains('/') || h.Contains(':')))
            errors.Add("Brokerul: doar numele sau adresa, fără port și fără schemă (portul are câmpul lui).");
        if (u.Port is { } p && (p < 1 || p > 65535)) errors.Add("Portul trebuie să fie între 1 și 65535.");
        if (u.DiscoveryPrefix is { } pre && (pre.Trim().Length == 0 || pre.IndexOfAny(['#', '+', ' ']) >= 0))
            errors.Add("Prefixul de discovery nu poate fi gol și nu poate conține #, + sau spații.");
        if (u.RawRetentionDays is { } r && (r < 0 || r > 3650)) errors.Add("Retenția citirilor brute: între 0 (niciodată șterse) și 3650 de zile.");
        if (u.MetricsSeriesLimit is { } m && (m < 10 || m > 10_000)) errors.Add("Limita de serii /metrics: între 10 și 10.000.");
        return errors;
    }

    public async Task SaveAsync(MqttSettingsUpdate u, CancellationToken ct)
    {
        var errors = Validate(u);
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        using (var scope = _scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
            if (u.Enabled is { } e) await repo.SetAsync(MqttSettingKeys.Enabled, e.ToString(), ct);
            if (u.Host is { } h) await repo.SetAsync(MqttSettingKeys.Host, h.Trim(), ct);
            if (u.Port is { } p) await repo.SetAsync(MqttSettingKeys.Port, p.ToString(CultureInfo.InvariantCulture), ct);
            if (u.User is { } us) await repo.SetAsync(MqttSettingKeys.User, us.Trim(), ct);
            if (u.UseTls is { } tls) await repo.SetAsync(MqttSettingKeys.UseTls, tls.ToString(), ct);
            if (u.DiscoveryPrefix is { } pre) await repo.SetAsync(MqttSettingKeys.DiscoveryPrefix, pre.Trim().Trim('/'), ct);
            if (u.AutoAdd is { } a) await repo.SetAsync(MqttSettingKeys.AutoAdd, a.ToString(), ct);
            if (u.RawRetentionDays is { } r) await repo.SetAsync(MqttSettingKeys.RawRetentionDays, r.ToString(CultureInfo.InvariantCulture), ct);
            if (u.MetricsSeriesLimit is { } m) await repo.SetAsync(MqttSettingKeys.MetricsSeriesLimit, m.ToString(CultureInfo.InvariantCulture), ct);
        }
        try { Changed?.Invoke(); } catch { /* a listener must not break the save */ }
    }
}
