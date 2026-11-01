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
    /// <summary>Live stream (main meter 1 s, plugs 1–10 s) for the channels with StreamEnabled.</summary>
    public const string LiveStreamEnabled = "LiveStreamEnabled";
    /// <summary>Base interval of the live stream in seconds (minimum 1): the main meter's fixed interval, the plugs' fastest.</summary>
    public const string LiveStreamIntervalSeconds = "LiveStreamIntervalSeconds";
    /// <summary>Days of raw live samples to keep (0 = unlimited); minute aggregates are never deleted.</summary>
    public const string LiveRawRetentionDays = "LiveRawRetentionDays";
    /// <summary>Shows the relay switch (Shelly Switch.Set) in the UI; false hides it and refuses commands.</summary>
    public const string AllowRelayControl = "AllowRelayControl";
    /// <summary>Jump of the main meter (W, between two readings) that sends every plug back to 1 s for 60 s.</summary>
    public const string LiveBurstTriggerW = "LiveBurstTriggerW";
    /// <summary>Prometheus remote write of the live samples (<c>kasa_live_*</c>); off by default.</summary>
    public const string RemoteWriteEnabled = "RemoteWriteEnabled";
    /// <summary>Remote write endpoint, e.g. http://prometheus:9090/api/v1/write.</summary>
    public const string RemoteWriteUrl = "RemoteWriteUrl";
    /// <summary>Optional basic-auth user and password of the remote write endpoint; both stored encrypted.</summary>
    public const string RemoteWriteUser = "RemoteWriteUser";
    public const string RemoteWritePassword = "RemoteWritePassword";
    /// <summary>Appliance detection from the main meter (events, signatures, runs, /consumatori); on by default.</summary>
    public const string NilmEnabled = "NilmEnabled";
    /// <summary>Smallest steady-state step of the main meter (W) recorded as an event; default 30 W.</summary>
    public const string MinEventDeltaW = "MinEventDeltaW";
    /// <summary>Days of events grouped by the nightly re-clustering (DBSCAN); default 30.</summary>
    public const string ClusterWindowDays = "ClusterWindowDays";
    /// <summary>Nightly ML.NET classifier over the labelled events (from plugs or the user); on by default.</summary>
    public const string MlEnabled = "MlEnabled";
    /// <summary>Internal: unix second up to which the main meter's samples went through the event detector.</summary>
    public const string NilmLastProcessedTs = "NilmLastProcessedTs";
    /// <summary>Internal: JSON with the last training of the classifier (time, examples, accuracy, confusion matrix).</summary>
    public const string NilmModelInfo = "NilmModelInfo";
}

/// <summary>Basic-auth credentials of the remote write endpoint (decrypted, never shown).</summary>
public sealed record RemoteWriteCredentials(string User, string Password);

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
    string ShellyProbeHosts = "",
    bool LiveStreamEnabled = true,
    int LiveStreamIntervalSeconds = 1,
    int LiveRawRetentionDays = 30,
    bool AllowRelayControl = true,
    int LiveBurstTriggerW = KasaSettingsDefaults.DefaultLiveBurstTriggerW,
    bool RemoteWriteEnabled = false,
    string RemoteWriteUrl = "",
    string? RemoteWriteUser = null,
    string? RemoteWritePassword = null,
    bool NilmEnabled = true,
    int MinEventDeltaW = KasaSettingsDefaults.DefaultMinEventDeltaW,
    int ClusterWindowDays = KasaSettingsDefaults.DefaultClusterWindowDays,
    bool MlEnabled = true)
{
    public const int DefaultLiveBurstTriggerW = 50;
    public const int DefaultMinEventDeltaW = 30;
    public const int DefaultClusterWindowDays = 30;
}

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
    IReadOnlyList<IPAddress>? ShellyProbeAddresses = null,
    bool LiveStreamEnabled = true,
    int LiveStreamIntervalSeconds = 1,
    int LiveRawRetentionDays = 30,
    bool AllowRelayControl = true,
    int LiveBurstTriggerW = KasaSettingsDefaults.DefaultLiveBurstTriggerW,
    bool RemoteWriteEnabled = false,
    string RemoteWriteUrl = "",
    RemoteWriteCredentials? RemoteWriteCredentials = null,
    bool NilmEnabled = true,
    int MinEventDeltaW = KasaSettingsDefaults.DefaultMinEventDeltaW,
    int ClusterWindowDays = KasaSettingsDefaults.DefaultClusterWindowDays,
    bool MlEnabled = true)
{
    public bool HasRemoteWriteAuth => RemoteWriteCredentials is not null;
    public bool HasShellyPassword => ShellyCredentials is not null;
    public IReadOnlyList<IPAddress> ShellyProbeList => ShellyProbeAddresses ?? Array.Empty<IPAddress>();
}

/// <param name="NewTapoPassword">Null or empty keeps the stored password.</param>
/// <param name="ReadingsRetentionMonths">Null keeps the stored value.</param>
/// <param name="ShellyDiscoveryEnabled">Null keeps the stored value.</param>
/// <param name="ShellyProbeHosts">Null keeps the stored value.</param>
/// <param name="NewShellyPassword">Null or empty keeps the stored password.</param>
/// <param name="LiveStreamEnabled">Null keeps the stored value (likewise the other live-stream settings).</param>
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
    bool ClearShellyPassword = false,
    bool? LiveStreamEnabled = null,
    int? LiveStreamIntervalSeconds = null,
    int? LiveRawRetentionDays = null,
    bool? AllowRelayControl = null,
    int? LiveBurstTriggerW = null,
    bool? RemoteWriteEnabled = null,
    string? RemoteWriteUrl = null,
    string? NewRemoteWriteUser = null,
    string? NewRemoteWritePassword = null,
    bool ClearRemoteWriteAuth = false,
    bool? NilmEnabled = null,
    int? MinEventDeltaW = null,
    int? ClusterWindowDays = null,
    bool? MlEnabled = null);

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
    public const int MinLiveIntervalSeconds = 1;
    public const int MaxLiveIntervalSeconds = 60;
    public const int MaxLiveRetentionDays = 36500;
    public const int MinBurstTriggerW = 1;
    public const int MaxBurstTriggerW = 100_000;
    public const int MinEventDeltaWMin = 5;
    public const int MinEventDeltaWMax = 10_000;
    public const int MinClusterWindowDays = 1;
    public const int MaxClusterWindowDays = 365;

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

        var streamOn = ParseBool(db, SettingKeys.LiveStreamEnabled) ?? _defaults.LiveStreamEnabled;
        var streamInterval = Math.Clamp(ParseInt(db, SettingKeys.LiveStreamIntervalSeconds) ?? _defaults.LiveStreamIntervalSeconds,
            MinLiveIntervalSeconds, MaxLiveIntervalSeconds);
        var meterRetention = Math.Clamp(ParseInt(db, SettingKeys.LiveRawRetentionDays) ?? _defaults.LiveRawRetentionDays, 0, MaxLiveRetentionDays);
        var relay = ParseBool(db, SettingKeys.AllowRelayControl) ?? _defaults.AllowRelayControl;
        var burst = Math.Clamp(ParseInt(db, SettingKeys.LiveBurstTriggerW) ?? _defaults.LiveBurstTriggerW, MinBurstTriggerW, MaxBurstTriggerW);
        var rwOn = ParseBool(db, SettingKeys.RemoteWriteEnabled) ?? _defaults.RemoteWriteEnabled;
        var rwUrl = db.TryGetValue(SettingKeys.RemoteWriteUrl, out var ru) ? ru : _defaults.RemoteWriteUrl;
        var rwUser = Secret(db, SettingKeys.RemoteWriteUser, _defaults.RemoteWriteUser);
        var rwPassword = Secret(db, SettingKeys.RemoteWritePassword, _defaults.RemoteWritePassword);
        var rwCreds = !string.IsNullOrEmpty(rwUser) && rwPassword is not null ? new RemoteWriteCredentials(rwUser, rwPassword) : null;
        var nilmOn = ParseBool(db, SettingKeys.NilmEnabled) ?? _defaults.NilmEnabled;
        var minEvent = Math.Clamp(ParseInt(db, SettingKeys.MinEventDeltaW) ?? _defaults.MinEventDeltaW, MinEventDeltaWMin, MinEventDeltaWMax);
        var clusterDays = Math.Clamp(ParseInt(db, SettingKeys.ClusterWindowDays) ?? _defaults.ClusterWindowDays, MinClusterWindowDays, MaxClusterWindowDays);
        var mlOn = ParseBool(db, SettingKeys.MlEnabled) ?? _defaults.MlEnabled;

        return new KasaRuntimeSettings(enabled, interval, broadcastText, addrs, autoAdd,
            user, !string.IsNullOrEmpty(password), creds, retention, tariff,
            shellyEnabled,
            string.IsNullOrEmpty(shellyPassword) ? null : new ShellyCredentials(shellyPassword),
            probeText,
            probeHosts,
            streamOn,
            streamInterval,
            meterRetention,
            relay,
            burst,
            rwOn,
            rwUrl.Trim(),
            rwCreds,
            nilmOn,
            minEvent,
            clusterDays,
            mlOn);
    }

    /// <summary>An encrypted value from the database (empty = cleared), else the configuration value.</summary>
    private string? Secret(IReadOnlyDictionary<string, string> db, string key, string? fallback)
    {
        if (db.TryGetValue(key, out var stored)) return stored.Length == 0 ? null : _protector.Unprotect(stored);
        return string.IsNullOrEmpty(fallback) ? null : fallback;
    }

    /// <summary>An absolute http/https URL, or a Romanian error text.</summary>
    public static string? ValidateRemoteWriteUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? null
            : "Adresa remote write trebuie să fie un URL http(s) complet, ex. http://192.168.1.10:9090/api/v1/write";

    public async Task SaveAsync(KasaSettingsUpdate update, CancellationToken ct)
    {
        var (_, invalid) = ParseBroadcastNetworks(update.BroadcastNetworks);
        if (invalid.Count > 0)
            throw new FormatException($"Adrese broadcast invalide: {string.Join(", ", invalid)}");
        if (update.ShellyProbeHosts is { } probe && ShellySupport.ParseProbeHosts(probe).Invalid is { Count: > 0 } badHosts)
            throw new FormatException($"Adrese Shelly invalide: {string.Join(", ", badHosts)} (IP, interval a.b.c.d-e sau CIDR /{ShellySupport.MinCidrPrefix}–/32, max. {ShellySupport.MaxProbeHosts} adrese)");

        if (update.RemoteWriteEnabled == true && ValidateRemoteWriteUrl(update.RemoteWriteUrl) is { } badUrl)
            throw new FormatException(badUrl);

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
        if (update.LiveStreamEnabled is { } streamOn)
            await repo.SetAsync(SettingKeys.LiveStreamEnabled, streamOn ? "true" : "false", ct);
        if (update.LiveStreamIntervalSeconds is { } streamInterval)
            await repo.SetAsync(SettingKeys.LiveStreamIntervalSeconds,
                Math.Clamp(streamInterval, MinLiveIntervalSeconds, MaxLiveIntervalSeconds).ToString(CultureInfo.InvariantCulture), ct);
        if (update.LiveRawRetentionDays is { } days)
            await repo.SetAsync(SettingKeys.LiveRawRetentionDays,
                Math.Clamp(days, 0, MaxLiveRetentionDays).ToString(CultureInfo.InvariantCulture), ct);
        if (update.AllowRelayControl is { } relay)
            await repo.SetAsync(SettingKeys.AllowRelayControl, relay ? "true" : "false", ct);
        if (update.LiveBurstTriggerW is { } burst)
            await repo.SetAsync(SettingKeys.LiveBurstTriggerW,
                Math.Clamp(burst, MinBurstTriggerW, MaxBurstTriggerW).ToString(CultureInfo.InvariantCulture), ct);
        if (update.RemoteWriteEnabled is { } rwOn)
            await repo.SetAsync(SettingKeys.RemoteWriteEnabled, rwOn ? "true" : "false", ct);
        if (update.RemoteWriteUrl is { } rwUrl)
            await repo.SetAsync(SettingKeys.RemoteWriteUrl, rwUrl.Trim(), ct);
        if (update.ClearRemoteWriteAuth)
        {
            await repo.SetAsync(SettingKeys.RemoteWriteUser, "", ct);
            await repo.SetAsync(SettingKeys.RemoteWritePassword, "", ct);
        }
        else
        {
            if (!string.IsNullOrEmpty(update.NewRemoteWriteUser))
                await repo.SetAsync(SettingKeys.RemoteWriteUser, _protector.Protect(update.NewRemoteWriteUser.Trim()), ct);
            if (!string.IsNullOrEmpty(update.NewRemoteWritePassword))
                await repo.SetAsync(SettingKeys.RemoteWritePassword, _protector.Protect(update.NewRemoteWritePassword), ct);
        }
        if (update.NilmEnabled is { } nilmOn)
            await repo.SetAsync(SettingKeys.NilmEnabled, nilmOn ? "true" : "false", ct);
        if (update.MinEventDeltaW is { } minEvent)
            await repo.SetAsync(SettingKeys.MinEventDeltaW,
                Math.Clamp(minEvent, MinEventDeltaWMin, MinEventDeltaWMax).ToString(CultureInfo.InvariantCulture), ct);
        if (update.ClusterWindowDays is { } clusterDays)
            await repo.SetAsync(SettingKeys.ClusterWindowDays,
                Math.Clamp(clusterDays, MinClusterWindowDays, MaxClusterWindowDays).ToString(CultureInfo.InvariantCulture), ct);
        if (update.MlEnabled is { } mlOn)
            await repo.SetAsync(SettingKeys.MlEnabled, mlOn ? "true" : "false", ct);
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
