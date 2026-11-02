using System.Globalization;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Heating;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers;

/// <summary>Keys of the heating (Viessmann) settings in the <c>Settings</c> table.</summary>
public static class HeatingSettingKeys
{
    public const string HeatingEnabled = "HeatingEnabled";
    /// <summary>Client ID of the developer-portal client (not a secret in PKCE, but never written into code or logs).</summary>
    public const string ViessmannClientId = "ViessmannClientId";
    /// <summary>Stored encrypted with <see cref="ISecretProtector"/>; empty = cleared (disconnected).</summary>
    public const string ViessmannAccessToken = "ViessmannAccessToken";
    /// <summary>Stored encrypted with <see cref="ISecretProtector"/>; empty = cleared (disconnected).</summary>
    public const string ViessmannRefreshToken = "ViessmannRefreshToken";
    /// <summary>Unix seconds at which the access token expires.</summary>
    public const string ViessmannTokenExpiresUtc = "ViessmannTokenExpiresUtc";
    /// <summary>Why the account is disconnected (refresh refused, …); empty when connected.</summary>
    public const string ViessmannAuthError = "ViessmannAuthError";
    public const string ViessmannPollIntervalSeconds = "ViessmannPollIntervalSeconds";
    /// <summary>Gas price in RON per kWh (invariant decimal).</summary>
    public const string GasPricePerKwh = "GasPricePerKwh";
    /// <summary>Unit assumed for gas values the API sends without a <c>unit</c>: "m3" or "kWh".</summary>
    public const string GasUnit = "GasUnit";
    /// <summary>Calorific factor kWh per m³ (invariant decimal), default 10.55.</summary>
    public const string GasKwhPerM3 = "GasKwhPerM3";
    /// <summary>Days of HeatingReadings to keep; 0 = unlimited (default).</summary>
    public const string HeatingReadingsRetentionDays = "HeatingReadingsRetentionDays";
    /// <summary>IANA zone of the boiler's day (its "currentDay" counters reset at midnight there), default Europe/Berlin.</summary>
    public const string BoilerTimeZone = "BoilerTimeZone";
}

/// <summary>Values from configuration (env <c>KasaMonitor__*</c>) used when the database has no value. Tokens only come from the OAuth flow.</summary>
public sealed record HeatingSettingsDefaults(
    bool HeatingEnabled = true,
    string ViessmannClientId = "",
    int ViessmannPollIntervalSeconds = ViessmannApi.DefaultPollIntervalSeconds,
    decimal GasPricePerKwh = 0.31m,
    string GasUnit = HeatingKinds.UnitM3,
    double GasKwhPerM3 = ViessmannApi.DefaultKwhPerM3,
    int HeatingReadingsRetentionDays = 0,
    string BoilerTimeZone = BoilerClock.DefaultTimeZoneId);

/// <summary>Effective heating settings. The tokens are decrypted here and never shown or logged.</summary>
public sealed record HeatingRuntimeSettings(
    bool HeatingEnabled,
    string ClientId,
    ViessmannToken? Token,
    string? AuthError,
    int PollIntervalSeconds,
    decimal GasPricePerKwh,
    EnergyUnit GasUnit,
    double GasKwhPerM3,
    int ReadingsRetentionDays = 0,
    string BoilerTimeZoneId = BoilerClock.DefaultTimeZoneId)
{
    /// <summary>The zone of the boiler's day (<see cref="BoilerClock"/>); Europe/Berlin when the id is unknown.</summary>
    public TimeZoneInfo BoilerTimeZone => BoilerClock.Resolve(BoilerTimeZoneId);

    public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);
    /// <summary>Connected = a refresh token (or a still valid access token) is stored.</summary>
    public bool IsConnected => Token is { } t && (!string.IsNullOrEmpty(t.RefreshToken) || t.ExpiresAtUtc > DateTimeOffset.UtcNow);
    public int CallsPerDay => ViessmannApi.CallsPerDay(PollIntervalSeconds);
    public string GasUnitText => GasUnit == EnergyUnit.KilowattHour ? HeatingKinds.UnitKwh : HeatingKinds.UnitM3;
}

/// <param name="ClientId">Null keeps the stored value.</param>
/// <param name="BoilerTimeZone">IANA id of the boiler's zone; null keeps the stored value.</param>
public sealed record HeatingSettingsUpdate(
    bool HeatingEnabled,
    string? ClientId,
    int PollIntervalSeconds,
    decimal GasPricePerKwh,
    string GasUnit,
    double GasKwhPerM3,
    int? ReadingsRetentionDays = null,
    string? BoilerTimeZone = null);

public interface IHeatingSettingsService
{
    Task<HeatingRuntimeSettings> GetAsync(CancellationToken ct);
    Task SaveAsync(HeatingSettingsUpdate update, CancellationToken ct);
    /// <summary>Stores new tokens (encrypted) and clears the disconnect reason.</summary>
    Task SaveTokenAsync(ViessmannToken token, CancellationToken ct);
    /// <summary>Clears the tokens and stores the reason (shown with the "reconnect" button).</summary>
    Task DisconnectAsync(string reason, CancellationToken ct);
}

public sealed class HeatingSettingsService : IHeatingSettingsService
{
    public const decimal MaxGasPrice = 1000m;
    public const double MinKwhPerM3 = 1;
    public const double MaxKwhPerM3 = 20;
    public const int MaxReadingsRetentionDays = 36500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly HeatingSettingsDefaults _defaults;
    private readonly ISecretProtector _protector;

    public HeatingSettingsService(IServiceScopeFactory scopeFactory, HeatingSettingsDefaults defaults, ISecretProtector protector)
    {
        _scopeFactory = scopeFactory;
        _defaults = defaults;
        _protector = protector;
    }

    public async Task<HeatingRuntimeSettings> GetAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> db;
        using (var scope = _scopeFactory.CreateScope())
            db = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAllAsync(ct);

        var enabled = db.TryGetValue(HeatingSettingKeys.HeatingEnabled, out var en) && bool.TryParse(en, out var b) ? b : _defaults.HeatingEnabled;
        var clientId = db.TryGetValue(HeatingSettingKeys.ViessmannClientId, out var cid) ? cid : _defaults.ViessmannClientId;
        var interval = ClampInterval(db.TryGetValue(HeatingSettingKeys.ViessmannPollIntervalSeconds, out var iv)
                                     && int.TryParse(iv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : _defaults.ViessmannPollIntervalSeconds);
        var price = db.TryGetValue(HeatingSettingKeys.GasPricePerKwh, out var pv)
                    && decimal.TryParse(pv, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) && p >= 0 ? p : _defaults.GasPricePerKwh;
        var unit = ParseGasUnit(db.TryGetValue(HeatingSettingKeys.GasUnit, out var gu) ? gu : _defaults.GasUnit);
        var factor = db.TryGetValue(HeatingSettingKeys.GasKwhPerM3, out var fv)
                     && double.TryParse(fv, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= MinKwhPerM3 && f <= MaxKwhPerM3
            ? f : _defaults.GasKwhPerM3;

        var retention = Math.Clamp(db.TryGetValue(HeatingSettingKeys.HeatingReadingsRetentionDays, out var rv)
                                   && int.TryParse(rv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : _defaults.HeatingReadingsRetentionDays,
            0, MaxReadingsRetentionDays);

        var boilerZone = db.TryGetValue(HeatingSettingKeys.BoilerTimeZone, out var bz) && BoilerClock.TryResolve(bz, out _) ? bz.Trim()
            : BoilerClock.TryResolve(_defaults.BoilerTimeZone, out _) ? _defaults.BoilerTimeZone.Trim() : BoilerClock.DefaultTimeZoneId;

        var access = Secret(db, HeatingSettingKeys.ViessmannAccessToken);
        var refresh = Secret(db, HeatingSettingKeys.ViessmannRefreshToken);
        var expires = db.TryGetValue(HeatingSettingKeys.ViessmannTokenExpiresUtc, out var ev)
                      && long.TryParse(ev, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? DateTimeOffset.FromUnixTimeSeconds(e) : DateTimeOffset.MinValue;
        var token = !string.IsNullOrEmpty(access) || !string.IsNullOrEmpty(refresh)
            ? new ViessmannToken(access ?? "", refresh, expires)
            : null;
        var authError = db.TryGetValue(HeatingSettingKeys.ViessmannAuthError, out var ae) && ae.Length > 0 ? ae : null;

        return new HeatingRuntimeSettings(enabled, clientId.Trim(), token, authError, interval, price, unit, factor, retention, boilerZone);
    }

    public async Task SaveAsync(HeatingSettingsUpdate u, CancellationToken ct)
    {
        if (u.GasPricePerKwh < 0 || u.GasPricePerKwh > MaxGasPrice)
            throw new FormatException($"Prețul gazului trebuie să fie între 0 și {MaxGasPrice} RON/kWh.");
        if (double.IsNaN(u.GasKwhPerM3) || u.GasKwhPerM3 < MinKwhPerM3 || u.GasKwhPerM3 > MaxKwhPerM3)
            throw new FormatException($"Factorul de conversie trebuie să fie între {MinKwhPerM3} și {MaxKwhPerM3} kWh/m³ (uzual 10,5–10,6).");
        if (u.BoilerTimeZone is { } zone && !BoilerClock.TryResolve(zone, out _))
            throw new FormatException($"Fusul orar „{zone}” nu e cunoscut; folosește un nume IANA, ex. {BoilerClock.DefaultTimeZoneId}.");

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync(HeatingSettingKeys.HeatingEnabled, u.HeatingEnabled ? "true" : "false", ct);
        if (u.ClientId is { } cid)
        {
            var previous = await repo.GetAsync(HeatingSettingKeys.ViessmannClientId, ct) ?? _defaults.ViessmannClientId;
            await repo.SetAsync(HeatingSettingKeys.ViessmannClientId, cid.Trim(), ct);
            // Tokens belong to the client that obtained them: a different client must connect again.
            var hasToken = !string.IsNullOrEmpty(await repo.GetAsync(HeatingSettingKeys.ViessmannRefreshToken, ct))
                           || !string.IsNullOrEmpty(await repo.GetAsync(HeatingSettingKeys.ViessmannAccessToken, ct));
            if (hasToken && previous.Trim().Length > 0 && !string.Equals(previous.Trim(), cid.Trim(), StringComparison.Ordinal))
                await ClearTokensAsync(repo, "Client ID schimbat: conectează din nou contul ViCare.", ct);
        }
        await repo.SetAsync(HeatingSettingKeys.ViessmannPollIntervalSeconds, ClampInterval(u.PollIntervalSeconds).ToString(CultureInfo.InvariantCulture), ct);
        await repo.SetAsync(HeatingSettingKeys.GasPricePerKwh, u.GasPricePerKwh.ToString(CultureInfo.InvariantCulture), ct);
        await repo.SetAsync(HeatingSettingKeys.GasUnit, ParseGasUnit(u.GasUnit) == EnergyUnit.KilowattHour ? HeatingKinds.UnitKwh : HeatingKinds.UnitM3, ct);
        await repo.SetAsync(HeatingSettingKeys.GasKwhPerM3, u.GasKwhPerM3.ToString(CultureInfo.InvariantCulture), ct);
        if (u.ReadingsRetentionDays is { } days)
            await repo.SetAsync(HeatingSettingKeys.HeatingReadingsRetentionDays,
                Math.Clamp(days, 0, MaxReadingsRetentionDays).ToString(CultureInfo.InvariantCulture), ct);
        if (u.BoilerTimeZone is { } boilerZone)
            await repo.SetAsync(HeatingSettingKeys.BoilerTimeZone, boilerZone.Trim(), ct);
    }

    public async Task SaveTokenAsync(ViessmannToken token, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync(HeatingSettingKeys.ViessmannAccessToken, _protector.Protect(token.AccessToken), ct);
        await repo.SetAsync(HeatingSettingKeys.ViessmannRefreshToken,
            string.IsNullOrEmpty(token.RefreshToken) ? "" : _protector.Protect(token.RefreshToken), ct);
        await repo.SetAsync(HeatingSettingKeys.ViessmannTokenExpiresUtc, token.ExpiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ct);
        await repo.SetAsync(HeatingSettingKeys.ViessmannAuthError, "", ct);
    }

    public async Task DisconnectAsync(string reason, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        await ClearTokensAsync(scope.ServiceProvider.GetRequiredService<ISettingsRepository>(), reason, ct);
    }

    public static int ClampInterval(int seconds) =>
        Math.Clamp(seconds, ViessmannApi.MinPollIntervalSeconds, ViessmannApi.MaxPollIntervalSeconds);

    public static EnergyUnit ParseGasUnit(string? text) =>
        HeatingFeatureParser.ParseUnit(text) == EnergyUnit.KilowattHour ? EnergyUnit.KilowattHour : EnergyUnit.CubicMeter;

    private static async Task ClearTokensAsync(ISettingsRepository repo, string reason, CancellationToken ct)
    {
        await repo.SetAsync(HeatingSettingKeys.ViessmannAccessToken, "", ct);
        await repo.SetAsync(HeatingSettingKeys.ViessmannRefreshToken, "", ct);
        await repo.SetAsync(HeatingSettingKeys.ViessmannTokenExpiresUtc, "0", ct);
        await repo.SetAsync(HeatingSettingKeys.ViessmannAuthError, reason, ct);
    }

    private string? Secret(IReadOnlyDictionary<string, string> db, string key) =>
        db.TryGetValue(key, out var stored) && stored.Length > 0 ? _protector.Unprotect(stored) : null;
}
