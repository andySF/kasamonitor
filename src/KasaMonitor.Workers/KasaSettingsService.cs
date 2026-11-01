using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Net;

namespace KasaMonitor.Workers;

/// <summary>Keys of the runtime settings stored in the <c>Settings</c> table.</summary>
public static class SettingKeys
{
    public const string AutoDiscoveryEnabled = "AutoDiscoveryEnabled";
    public const string AutoDiscoveryIntervalMinutes = "AutoDiscoveryIntervalMinutes";
    public const string BroadcastNetworks = "BroadcastNetworks";
    public const string AutoAddDiscoveredPlugs = "AutoAddDiscoveredPlugs";
    public const string TapoUsername = "TapoUsername";
    /// <summary>Stored encrypted with <see cref="ISecretProtector"/>; an empty value means "cleared" (no env fallback).</summary>
    public const string TapoPassword = "TapoPassword";
    /// <summary>Months of raw 10-second readings (Samples) to keep; 0 = unlimited. Daily history is never deleted.</summary>
    public const string ReadingsRetentionMonths = "ReadingsRetentionMonths";
    /// <summary>Price per kWh saved from /settings (invariant decimal); used for every cost shown in the UI.</summary>
    public const string TariffRonPerKwh = "TariffRonPerKwh";
    /// <summary>mDNS discovery of Shelly Gen2+ devices (and probing of <see cref="ShellyProbeHosts"/>).</summary>
    public const string ShellyDiscoveryEnabled = "ShellyDiscoveryEnabled";
    /// <summary>Admin password of Shelly devices with authentication on; stored encrypted like <see cref="TapoPassword"/>.</summary>
    public const string ShellyPassword = "ShellyPassword";
    /// <summary>Optional IPs / ranges / CIDR blocks probed with Shelly.GetDeviceInfo on every discovery (mDNS fallback).</summary>
    public const string ShellyProbeHosts = "ShellyProbeHosts";
}

/// <summary>Values from configuration (env <c>KasaMonitor__*</c>), used when the database has no value for a key.</summary>
public sealed record KasaSettingsDefaults(
    bool AutoDiscoveryEnabled = true,
    int AutoDiscoveryIntervalMinutes = 5,
    string BroadcastNetworks = "",
    bool AutoAddDiscoveredPlugs = true,
    string? TapoUsername = null,
    string? TapoPassword = null,
    int ReadingsRetentionMonths = 0,
    decimal TariffRonPerKwh = 1.30m,
    bool ShellyDiscoveryEnabled = true,
    string? ShellyPassword = null,
    string ShellyProbeHosts = "");

/// <summary>Effective settings (database first, configuration as fallback).</summary>
public sealed record KasaRuntimeSettings(
    bool AutoDiscoveryEnabled,
    int AutoDiscoveryIntervalMinutes,
    string BroadcastNetworks,
    IReadOnlyList<IPAddress> BroadcastAddresses,
    bool AutoAddDiscoveredPlugs,
    string TapoUsername,
    bool HasTapoPassword,
    TapoCredentials? TapoCredentials,
    int ReadingsRetentionMonths = 0,
    decimal TariffRonPerKwh = 1.30m,
    bool ShellyDiscoveryEnabled = true,
    ShellyCredentials? ShellyCredentials = null,
    string ShellyProbeHosts = "",
    IReadOnlyList<IPAddress>? ShellyProbeAddresses = null)
{
    public bool HasShellyPassword => ShellyCredentials is not null;
    public IReadOnlyList<IPAddress> ShellyProbeList => ShellyProbeAddresses ?? Array.Empty<IPAddress>();
}

/// <param name="NewTapoPassword">Null or empty keeps the stored password.</param>
/// <param name="ReadingsRetentionMonths">Null keeps the stored value.</param>
/// <param name="ShellyDiscoveryEnabled">Null keeps the stored value.</param>
/// <param name="ShellyProbeHosts">Null keeps the stored value.</param>
/// <param name="NewShellyPassword">Null or empty keeps the stored password.</param>
public sealed record KasaSettingsUpdate(
    bool AutoDiscoveryEnabled,
    int AutoDiscoveryIntervalMinutes,
    string BroadcastNetworks,
    bool AutoAddDiscoveredPlugs,
    string TapoUsername,
    string? NewTapoPassword,
    bool ClearTapoPassword,
    int? ReadingsRetentionMonths = null,
    bool? ShellyDiscoveryEnabled = null,
    string? ShellyProbeHosts = null,
    string? NewShellyPassword = null,
    bool ClearShellyPassword = false);

public interface IKasaSettingsService
{
    Task<KasaRuntimeSettings> GetAsync(CancellationToken ct);
    Task SaveAsync(KasaSettingsUpdate update, CancellationToken ct);
}

public sealed class KasaSettingsService : IKasaSettingsService
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;
    public const int MaxRetentionMonths = 1200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KasaSettingsDefaults _defaults;
    private readonly ISecretProtector _protector;

    public KasaSettingsService(IServiceScopeFactory scopeFactory, KasaSettingsDefaults defaults, ISecretProtector protector)
    {
        _scopeFactory = scopeFactory;
        _defaults = defaults;
        _protector = protector;
    }

    public async Task<KasaRuntimeSettings> GetAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> db;
        using (var scope = _scopeFactory.CreateScope())
            db = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAllAsync(ct);

        var enabled = ParseBool(db, SettingKeys.AutoDiscoveryEnabled) ?? _defaults.AutoDiscoveryEnabled;
        var interval = Math.Clamp(
            ParseInt(db, SettingKeys.AutoDiscoveryIntervalMinutes) ?? _defaults.AutoDiscoveryIntervalMinutes,
            MinIntervalMinutes, MaxIntervalMinutes);
        var autoAdd = ParseBool(db, SettingKeys.AutoAddDiscoveredPlugs) ?? _defaults.AutoAddDiscoveredPlugs;

        var broadcastText = db.TryGetValue(SettingKeys.BroadcastNetworks, out var b) && !string.IsNullOrWhiteSpace(b)
            ? b
            : _defaults.BroadcastNetworks;
        var (addrs, _) = ParseBroadcastNetworks(broadcastText);
        if (addrs.Count == 0) addrs = new[] { IPAddress.Broadcast };

        var user = db.TryGetValue(SettingKeys.TapoUsername, out var u) ? u : _defaults.TapoUsername ?? "";
        string? password;
        if (db.TryGetValue(SettingKeys.TapoPassword, out var protectedPw))
            password = protectedPw.Length == 0 ? null : _protector.Unprotect(protectedPw);
        else
            password = string.IsNullOrEmpty(_defaults.TapoPassword) ? null : _defaults.TapoPassword;

        var creds = !string.IsNullOrWhiteSpace(user) && !string.IsNullOrEmpty(password)
            ? new TapoCredentials(user.Trim(), password)
            : null;

        var retention = Math.Clamp(ParseInt(db, SettingKeys.ReadingsRetentionMonths) ?? _defaults.ReadingsRetentionMonths, 0, MaxRetentionMonths);

        var tariff = db.TryGetValue(SettingKeys.TariffRonPerKwh, out var tv)
                     && decimal.TryParse(tv, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsedTariff) && parsedTariff > 0
            ? parsedTariff
            : _defaults.TariffRonPerKwh;

        var shellyEnabled = ParseBool(db, SettingKeys.ShellyDiscoveryEnabled) ?? _defaults.ShellyDiscoveryEnabled;
        string? shellyPassword;
        if (db.TryGetValue(SettingKeys.ShellyPassword, out var protectedShelly))
            shellyPassword = protectedShelly.Length == 0 ? null : _protector.Unprotect(protectedShelly);
        else
            shellyPassword = string.IsNullOrEmpty(_defaults.ShellyPassword) ? null : _defaults.ShellyPassword;
        var probeText = db.TryGetValue(SettingKeys.ShellyProbeHosts, out var ph) ? ph : _defaults.ShellyProbeHosts;
        var (probeHosts, _) = ShellySupport.ParseProbeHosts(probeText);

        return new KasaRuntimeSettings(enabled, interval, broadcastText, addrs, autoAdd,
            user, !string.IsNullOrEmpty(password), creds, retention, tariff,
            shellyEnabled,
            string.IsNullOrEmpty(shellyPassword) ? null : new ShellyCredentials(shellyPassword),
            probeText,
            probeHosts);
    }

    public async Task SaveAsync(KasaSettingsUpdate update, CancellationToken ct)
    {
        var (_, invalid) = ParseBroadcastNetworks(update.BroadcastNetworks);
        if (invalid.Count > 0)
            throw new FormatException($"Adrese broadcast invalide: {string.Join(", ", invalid)}");
        if (update.ShellyProbeHosts is { } probe && ShellySupport.ParseProbeHosts(probe).Invalid is { Count: > 0 } badHosts)
            throw new FormatException($"Adrese Shelly invalide: {string.Join(", ", badHosts)} (IP, interval a.b.c.d-e sau CIDR /{ShellySupport.MinCidrPrefix}–/32, max. {ShellySupport.MaxProbeHosts} adrese)");

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync(SettingKeys.AutoDiscoveryEnabled, update.AutoDiscoveryEnabled ? "true" : "false", ct);
        await repo.SetAsync(SettingKeys.AutoDiscoveryIntervalMinutes,
            Math.Clamp(update.AutoDiscoveryIntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes).ToString(CultureInfo.InvariantCulture), ct);
        await repo.SetAsync(SettingKeys.BroadcastNetworks, update.BroadcastNetworks.Trim(), ct);
        await repo.SetAsync(SettingKeys.AutoAddDiscoveredPlugs, update.AutoAddDiscoveredPlugs ? "true" : "false", ct);
        await repo.SetAsync(SettingKeys.TapoUsername, update.TapoUsername.Trim(), ct);
        if (update.ReadingsRetentionMonths is { } months)
            await repo.SetAsync(SettingKeys.ReadingsRetentionMonths,
                Math.Clamp(months, 0, MaxRetentionMonths).ToString(CultureInfo.InvariantCulture), ct);
        if (update.ClearTapoPassword)
            await repo.SetAsync(SettingKeys.TapoPassword, "", ct);
        else if (!string.IsNullOrEmpty(update.NewTapoPassword))
            await repo.SetAsync(SettingKeys.TapoPassword, _protector.Protect(update.NewTapoPassword), ct);
        if (update.ShellyDiscoveryEnabled is { } shellyOn)
            await repo.SetAsync(SettingKeys.ShellyDiscoveryEnabled, shellyOn ? "true" : "false", ct);
        if (update.ShellyProbeHosts is { } hosts)
            await repo.SetAsync(SettingKeys.ShellyProbeHosts, hosts.Trim(), ct);
        if (update.ClearShellyPassword)
            await repo.SetAsync(SettingKeys.ShellyPassword, "", ct);
        else if (!string.IsNullOrEmpty(update.NewShellyPassword))
            await repo.SetAsync(SettingKeys.ShellyPassword, _protector.Protect(update.NewShellyPassword), ct);
    }

    /// <summary>Comma-separated IPv4 addresses; returns the valid ones and the rejected tokens.</summary>
    public static (IReadOnlyList<IPAddress> Valid, IReadOnlyList<string> Invalid) ParseBroadcastNetworks(string? text)
    {
        var valid = new List<IPAddress>();
        var invalid = new List<string>();
        foreach (var token in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(token, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                valid.Add(ip);
            else
                invalid.Add(token);
        }
        return (valid, invalid);
    }

    private static bool? ParseBool(IReadOnlyDictionary<string, string> db, string key) =>
        db.TryGetValue(key, out var v) && bool.TryParse(v, out var b) ? b : null;

    private static int? ParseInt(IReadOnlyDictionary<string, string> db, string key) =>
        db.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
}
