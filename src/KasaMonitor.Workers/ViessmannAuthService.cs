using System.Security.Cryptography;
using System.Text;
using KasaMonitor.Core.Heating;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public enum ViessmannCallbackResult { Connected, InvalidState, Denied, ExchangeFailed, NoClientId }

/// <summary>No usable token: the account must be (re)connected from /centrala.</summary>
public sealed class HeatingDisconnectedException : Exception
{
    public HeatingDisconnectedException(string message) : base(message) { }
}

/// <summary>
/// OAuth 2.0 authorization code flow with PKCE (S256) for the ViCare account. "Conectează ViCare" creates a verifier,
/// its challenge and a random <c>state</c> (kept in memory for 10 minutes, single use) and redirects to the authorize
/// endpoint; <c>/centrala/callback</c> checks the state and exchanges the code (with the verifier) for tokens, which are
/// stored encrypted. The access token is refreshed 5 minutes before it expires; a refused refresh disconnects the
/// account (tokens cleared, reason kept, "reconnect" button); a 5xx/429 from the token endpoint is temporary. The
/// callback must also carry the cookie set by <c>/centrala/connect</c> (hash of the state), so it is tied to the browser
/// that started the flow; at most <see cref="MaxPending"/> requests wait at once. Tokens, codes and verifiers are never logged.
/// </summary>
public sealed class ViessmannAuthService
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    public const int MaxPending = 10;
    /// <summary>Cookie (HttpOnly, SameSite=Lax, Path=/centrala) holding <see cref="StateCookieValue"/> of the pending state.</summary>
    public const string StateCookieName = "vicare_oauth";

    private sealed record Pending(string Verifier, string RedirectUri, DateTimeOffset CreatedUtc);

    private readonly IHeatingSettingsService _settings;
    private readonly IViessmannClient _client;
    private readonly ILogger<ViessmannAuthService> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly DailyCallCounter? _calls;

    /// <param name="calls">Counts every token request that leaves (shown with the API calls of the day).</param>
    public ViessmannAuthService(IHeatingSettingsService settings, IViessmannClient client, ILogger<ViessmannAuthService> log,
        Func<DateTimeOffset>? clock = null, DailyCallCounter? calls = null)
    {
        _calls = calls;
        _settings = settings;
        _client = client;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised after a successful connection (the worker starts the discovery right away).</summary>
    public event Action? Connected;

    /// <summary>The authorize URL for the browser and its state (for the cookie), or null when no Client ID is set.</summary>
    public async Task<(string Url, string State)?> BeginAuthorizeAsync(string redirectUri, CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.HasClientId) return null;
        var verifier = Pkce.NewVerifier();
        var state = Pkce.NewState();
        lock (_sync)
        {
            Prune();
            // Cap: the oldest pending request gives way (a scanner hitting /centrala/connect cannot grow the list).
            while (_pending.Count >= MaxPending)
                _pending.Remove(_pending.MinBy(kv => kv.Value.CreatedUtc).Key);
            _pending[state] = new Pending(verifier, redirectUri, _clock());
        }
        return (AuthorizeUrl(s.ClientId, redirectUri, Pkce.Challenge(verifier), state), state);
    }

    public int PendingCount { get { lock (_sync) return _pending.Count; } }

    /// <summary>Cookie value for a state: hex SHA-256 (the state itself never sits in the cookie).</summary>
    public static string StateCookieValue(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));

    /// <summary>The callback's state matches the cookie of the browser that started the flow (constant-time compare).</summary>
    public static bool CookieMatches(string? state, string? cookie) =>
        !string.IsNullOrEmpty(state) && !string.IsNullOrEmpty(cookie)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(StateCookieValue(state)), Encoding.ASCII.GetBytes(cookie.ToUpperInvariant()));

    public static string AuthorizeUrl(string clientId, string redirectUri, string codeChallenge, string state) =>
        ViessmannApi.AuthorizeUrl
        + "?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&scope=" + Uri.EscapeDataString(ViessmannApi.Scope)
        + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
        + "&code_challenge_method=S256"
        + "&state=" + Uri.EscapeDataString(state);

    /// <summary>
    /// Handles the redirect back from Viessmann. An unknown, expired or already used <paramref name="state"/> is
    /// rejected without any call; otherwise the code is exchanged and the tokens are stored.
    /// </summary>
    public async Task<(ViessmannCallbackResult Result, string? Detail)> CompleteAsync(string? state, string? code, string? error, CancellationToken ct)
    {
        Pending? pending = null;
        lock (_sync)
        {
            Prune();
            if (!string.IsNullOrEmpty(state) && _pending.Remove(state, out var p)) pending = p;
        }
        if (pending is null)
        {
            _log.LogWarning("ViCare callback rejected: unknown or expired state");
            return (ViessmannCallbackResult.InvalidState, null);
        }
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            _log.LogWarning("ViCare authorization refused: {Error}", error ?? "no code");
            return (ViessmannCallbackResult.Denied, error);
        }

        var s = await _settings.GetAsync(ct);
        if (!s.HasClientId) return (ViessmannCallbackResult.NoClientId, null);
        try
        {
            _calls?.Record();
            var token = await _client.ExchangeCodeAsync(s.ClientId, code, pending.Verifier, pending.RedirectUri, ct);
            await _settings.SaveTokenAsync(token, ct);
            _log.LogInformation("ViCare account connected (access token valid until {Expires:u}, refresh token {Refresh})",
                token.ExpiresAtUtc, string.IsNullOrEmpty(token.RefreshToken) ? "missing" : "received");
        }
        catch (Exception ex) when (ex is ViessmannException or HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning("ViCare code exchange failed: {Reason}", ex.Message);
            return (ViessmannCallbackResult.ExchangeFailed, ex.Message);
        }
        try { Connected?.Invoke(); } catch (Exception ex) { _log.LogDebug(ex, "Connected handler failed"); }
        return (ViessmannCallbackResult.Connected, null);
    }

    /// <summary>A valid access token, refreshed when it expires within 5 minutes.</summary>
    /// <exception cref="HeatingDisconnectedException">No token, or the refresh was refused.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (s.Token is not { } token)
            throw new HeatingDisconnectedException(s.AuthError ?? "Contul ViCare nu e conectat.");
        if (!string.IsNullOrEmpty(token.AccessToken) && token.ExpiresAtUtc - ViessmannApi.RefreshBeforeExpiry > _clock())
            return token.AccessToken;
        return await RefreshAsync(s, force: false, ct);
    }

    /// <summary>After an API 401: refresh now, whatever the stored expiry says.</summary>
    public async Task<string> ForceRefreshAsync(CancellationToken ct) => await RefreshAsync(await _settings.GetAsync(ct), force: true, ct);

    private async Task<string> RefreshAsync(HeatingRuntimeSettings s, bool force, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited.
            var current = await _settings.GetAsync(ct);
            if (!force && current.Token is { } fresh && !string.IsNullOrEmpty(fresh.AccessToken)
                && fresh.ExpiresAtUtc - ViessmannApi.RefreshBeforeExpiry > _clock())
                return fresh.AccessToken;
            var refresh = current.Token?.RefreshToken ?? s.Token?.RefreshToken;
            if (string.IsNullOrEmpty(refresh))
            {
                const string reason = "Tokenul ViCare a expirat și nu există refresh token: reconectează contul.";
                await _settings.DisconnectAsync(reason, ct);
                throw new HeatingDisconnectedException(reason);
            }
            if (!current.HasClientId) throw new HeatingDisconnectedException("Lipsește Client ID-ul Viessmann (Setări).");
            try
            {
                _calls?.Record();
                var token = await _client.RefreshAsync(current.ClientId, refresh, ct);
                await _settings.SaveTokenAsync(token, ct);
                _log.LogInformation("ViCare access token refreshed (valid until {Expires:u})", token.ExpiresAtUtc);
                return token.AccessToken;
            }
            catch (ViessmannAuthException ex)
            {
                var reason = "Reîmprospătarea tokenului ViCare a fost refuzată (" + ex.Message + "): reconectează contul.";
                _log.LogWarning("ViCare refresh refused, account disconnected: {Reason}", ex.Message);
                await _settings.DisconnectAsync(reason, ct);
                throw new HeatingDisconnectedException(reason);
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void Prune()
    {
        var cutoff = _clock() - PendingLifetime;
        foreach (var key in _pending.Where(kv => kv.Value.CreatedUtc < cutoff).Select(kv => kv.Key).ToList())
            _pending.Remove(key);
    }
}
