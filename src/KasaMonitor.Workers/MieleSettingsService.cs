using System.Globalization;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Miele;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers;

/// <summary>Keys of the Miele settings in the <c>Settings</c> table. The client secret is never stored there.</summary>
public static class MieleSettingKeys
{
    /// <summary>Client ID of the Miele developer client (not secret, but never written into code or logs).</summary>
    public const string MieleClientId = "MieleClientId";
    /// <summary>Stored encrypted with <see cref="ISecretProtector"/>; empty = cleared.</summary>
    public const string MieleAccessToken = "MieleAccessToken";
    /// <summary>Stored encrypted with <see cref="ISecretProtector"/>; replaced at every refresh (rotation); empty = cleared.</summary>
    public const string MieleRefreshToken = "MieleRefreshToken";
    /// <summary>Unix seconds at which the access token expires.</summary>
    public const string MieleTokenExpiresUtc = "MieleTokenExpiresUtc";
    /// <summary><c>expires_in</c> of the last token answer (seconds): the refresh starts at 10 % of it before expiry.</summary>
    public const string MieleTokenLifetimeSeconds = "MieleTokenLifetimeSeconds";
    /// <summary>Why the account is disconnected (refresh refused, logged out …); empty when connected.</summary>
    public const string MieleAuthError = "MieleAuthError";
    /// <summary>Unix seconds of the last refused refresh (the next attempt waits at least an hour); empty otherwise.</summary>
    public const string MieleRefreshRefusedUtc = "MieleRefreshRefusedUtc";
}

/// <summary>
/// Values from configuration: the Client ID (used when the database has none) and where the client secret comes
/// from: <c>KasaMonitor__MieleClientSecret</c> (env) or the file <c>KasaMonitor__MieleClientSecretFile</c>
/// (default <c>/run/secrets/miele-client.secret</c>), read once at startup.
/// </summary>
public sealed record MieleSettingsDefaults(
    string MieleClientId = "",
    string MieleClientSecretFile = MieleApi.DefaultClientSecretFile,
    string? MieleClientSecret = null);

/// <summary>
/// The client secret, loaded once at startup from the environment or a file. Only <see cref="MieleAuthService"/>
/// reads <see cref="Value"/>; it is never stored in the database, shown or logged (<see cref="ToString"/> hides it).
/// </summary>
public sealed class MieleClientSecret
{
    public string? Value { get; }
    /// <summary>Where it came from („variabila KasaMonitor__MieleClientSecret” / „fișierul …”), for the UI.</summary>
    public string? Source { get; }
    /// <summary>Why there is none (file missing, empty, unreadable), for the UI.</summary>
    public string? Problem { get; }

    public MieleClientSecret(string? value, string? source, string? problem)
    {
        Value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        Source = Value is null ? null : source;
        Problem = Value is null ? problem ?? "secretul lipsește" : null;
    }

    public bool IsSet => Value is not null;

    public override string ToString() => IsSet ? "MieleClientSecret(set, " + Source + ")" : "MieleClientSecret(missing)";

    /// <summary>The environment value wins; otherwise the file (trimmed). <paramref name="readFile"/> is a test hook.</summary>
    public static MieleClientSecret Load(MieleSettingsDefaults d, Func<string, string?>? readFile = null)
    {
        if (!string.IsNullOrWhiteSpace(d.MieleClientSecret))
            return new MieleClientSecret(d.MieleClientSecret, "variabila KasaMonitor__MieleClientSecret", null);
        var path = string.IsNullOrWhiteSpace(d.MieleClientSecretFile) ? MieleApi.DefaultClientSecretFile : d.MieleClientSecretFile.Trim();
        readFile ??= p => File.Exists(p) ? File.ReadAllText(p) : null;
        try
        {
            var text = readFile(path);
            if (text is null) return new MieleClientSecret(null, null, "fișierul " + path + " nu există");
            return new MieleClientSecret(text, "fișierul " + path, "fișierul " + path + " e gol");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new MieleClientSecret(null, null, "fișierul " + path + " nu poate fi citit (" + ex.GetType().Name + ")");
        }
    }
}

/// <summary>Effective Miele settings. Tokens are decrypted here and never shown or logged.</summary>
public sealed record MieleRuntimeSettings(
    string ClientId,
    bool HasSecret,
    string? SecretSource,
    string? SecretProblem,
    MieleToken? Token,
    string? AuthError,
    DateTimeOffset? RefreshRefusedUtc)
{
    public bool HasClientId => !string.IsNullOrWhiteSpace(ClientId);
    /// <summary>Client ID and secret present: the OAuth flow can start.</summary>
    public bool CanConnect => HasClientId && HasSecret;
    /// <summary>Tokens stored and no refused refresh pending.</summary>
    public bool IsConnected => Token is not null && AuthError is null;
}

public interface IMieleSettingsService
{
    Task<MieleRuntimeSettings> GetAsync(CancellationToken ct);
    /// <summary>The client secret (only for the token requests).</summary>
    string? ClientSecret { get; }
    /// <summary>Saves the Client ID; a different one clears the tokens (they belong to the client that obtained them).</summary>
    Task SaveClientIdAsync(string clientId, CancellationToken ct);
    /// <summary>Stores new tokens (encrypted) and clears the disconnect reason and the refused-refresh time.</summary>
    Task SaveTokenAsync(MieleToken token, CancellationToken ct);
    /// <summary>The refresh was refused: keeps the tokens (one retry per hour), stores the reason and the time.</summary>
    Task MarkRefreshRefusedAsync(string reason, DateTimeOffset atUtc, CancellationToken ct);
    /// <summary>Clears the tokens and stores the reason (shown with the „Conectează” button).</summary>
    Task ClearTokensAsync(string reason, CancellationToken ct);
}

public sealed class MieleSettingsService : IMieleSettingsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MieleSettingsDefaults _defaults;
    private readonly ISecretProtector _protector;
    private readonly MieleClientSecret _secret;

    public MieleSettingsService(IServiceScopeFactory scopeFactory, MieleSettingsDefaults defaults, ISecretProtector protector, MieleClientSecret? secret = null)
    {
        _scopeFactory = scopeFactory;
        _defaults = defaults;
        _protector = protector;
        _secret = secret ?? MieleClientSecret.Load(defaults);
    }

    public string? ClientSecret => _secret.Value;

    public async Task<MieleRuntimeSettings> GetAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, string> db;
        using (var scope = _scopeFactory.CreateScope())
            db = await scope.ServiceProvider.GetRequiredService<ISettingsRepository>().GetAllAsync(ct);

        var clientId = db.TryGetValue(MieleSettingKeys.MieleClientId, out var cid) ? cid : _defaults.MieleClientId;
        var access = Secret(db, MieleSettingKeys.MieleAccessToken);
        var refresh = Secret(db, MieleSettingKeys.MieleRefreshToken);
        var expires = Long(db, MieleSettingKeys.MieleTokenExpiresUtc) is { } e ? DateTimeOffset.FromUnixTimeSeconds(e) : DateTimeOffset.MinValue;
        var lifetime = Long(db, MieleSettingKeys.MieleTokenLifetimeSeconds) ?? 0;
        var token = !string.IsNullOrEmpty(access) || !string.IsNullOrEmpty(refresh)
            ? new MieleToken(access ?? "", refresh, expires, lifetime)
            : null;
        var authError = db.TryGetValue(MieleSettingKeys.MieleAuthError, out var ae) && ae.Length > 0 ? ae : null;
        var refused = Long(db, MieleSettingKeys.MieleRefreshRefusedUtc) is { } r and > 0 ? DateTimeOffset.FromUnixTimeSeconds(r) : (DateTimeOffset?)null;
        return new MieleRuntimeSettings(clientId.Trim(), _secret.IsSet, _secret.Source, _secret.Problem, token, authError, refused);
    }

    public async Task SaveClientIdAsync(string clientId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        var previous = (await repo.GetAsync(MieleSettingKeys.MieleClientId, ct) ?? _defaults.MieleClientId).Trim();
        var next = (clientId ?? "").Trim();
        await repo.SetAsync(MieleSettingKeys.MieleClientId, next, ct);
        var hasToken = !string.IsNullOrEmpty(await repo.GetAsync(MieleSettingKeys.MieleRefreshToken, ct))
                       || !string.IsNullOrEmpty(await repo.GetAsync(MieleSettingKeys.MieleAccessToken, ct));
        if (hasToken && previous.Length > 0 && !string.Equals(previous, next, StringComparison.Ordinal))
            await ClearAsync(repo, "Client ID schimbat: conectează din nou contul Miele.", ct);
    }

    public async Task SaveTokenAsync(MieleToken token, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync(MieleSettingKeys.MieleAccessToken, _protector.Protect(token.AccessToken), ct);
        await repo.SetAsync(MieleSettingKeys.MieleRefreshToken, string.IsNullOrEmpty(token.RefreshToken) ? "" : _protector.Protect(token.RefreshToken), ct);
        await repo.SetAsync(MieleSettingKeys.MieleTokenExpiresUtc, token.ExpiresAtUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ct);
        await repo.SetAsync(MieleSettingKeys.MieleTokenLifetimeSeconds, token.LifetimeSeconds.ToString(CultureInfo.InvariantCulture), ct);
        await repo.SetAsync(MieleSettingKeys.MieleAuthError, "", ct);
        await repo.SetAsync(MieleSettingKeys.MieleRefreshRefusedUtc, "", ct);
    }

    public async Task MarkRefreshRefusedAsync(string reason, DateTimeOffset atUtc, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync(MieleSettingKeys.MieleAuthError, reason, ct);
        await repo.SetAsync(MieleSettingKeys.MieleRefreshRefusedUtc, atUtc.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ct);
    }

    public async Task ClearTokensAsync(string reason, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        await ClearAsync(scope.ServiceProvider.GetRequiredService<ISettingsRepository>(), reason, ct);
    }

    private static async Task ClearAsync(ISettingsRepository repo, string reason, CancellationToken ct)
    {
        await repo.SetAsync(MieleSettingKeys.MieleAccessToken, "", ct);
        await repo.SetAsync(MieleSettingKeys.MieleRefreshToken, "", ct);
        await repo.SetAsync(MieleSettingKeys.MieleTokenExpiresUtc, "0", ct);
        await repo.SetAsync(MieleSettingKeys.MieleTokenLifetimeSeconds, "0", ct);
        await repo.SetAsync(MieleSettingKeys.MieleAuthError, reason, ct);
        await repo.SetAsync(MieleSettingKeys.MieleRefreshRefusedUtc, "", ct);
    }

    private static long? Long(IReadOnlyDictionary<string, string> db, string key) =>
        db.TryGetValue(key, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private string? Secret(IReadOnlyDictionary<string, string> db, string key) =>
        db.TryGetValue(key, out var stored) && stored.Length > 0 ? _protector.Unprotect(stored) : null;
}
