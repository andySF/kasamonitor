using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Miele;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public enum MieleCallbackResult { Connected, InvalidState, Denied, ExchangeFailed, NotConfigured }

/// <summary>No usable token: the account must be (re)connected from /aparate (or the hourly retry is not due yet).</summary>
public sealed class MieleDisconnectedException : Exception
{
    public MieleDisconnectedException(string message) : base(message) { }
}

/// <summary>
/// OAuth 2.0 authorization code flow of the Miele account (new provider, client secret, no PKCE). „Conectează Miele”
/// creates a random <c>state</c> (in memory 10 minutes, single use, bound to the browser by a cookie with its hash) and
/// redirects to Miele; <c>/miele/callback</c> checks it and exchanges the code (with the client secret) for tokens,
/// stored encrypted. The access token is refreshed when 10 % of its lifetime (<c>expires_in</c>) is left; every refresh
/// returns a new refresh token, which replaces the old one (rotation). Miele limits the tokens per client and user, so
/// nothing here ever re-authorizes on its own: a refused refresh marks the account disconnected (message, „Reconectează”)
/// and is retried at most once an hour; a temporary failure keeps the current access token while it is still valid.
/// „Deconectează” calls <c>POST /logout</c> and clears the tokens. Tokens, codes and the secret are never logged.
/// </summary>
public sealed class MieleAuthService
{
    public static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(10);
    public const int MaxPending = 10;
    /// <summary>Cookie (HttpOnly, SameSite=Lax, Path=/miele) holding the hash of the pending state.</summary>
    public const string StateCookieName = "miele_oauth";

    private sealed record Pending(string RedirectUri, DateTimeOffset CreatedUtc);

    private readonly IMieleSettingsService _settings;
    private readonly IMieleClient _client;
    private readonly ILogger<MieleAuthService> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public MieleAuthService(IMieleSettingsService settings, IMieleClient client, ILogger<MieleAuthService> log, Func<DateTimeOffset>? clock = null)
    {
        _settings = settings;
        _client = client;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>After a successful connection (the worker connects right away).</summary>
    public event Action? Connected;
    /// <summary>After „Deconectează” (the worker drops the stream).</summary>
    public event Action? Disconnected;

    public int PendingCount { get { lock (_sync) return _pending.Count; } }

    /// <summary>The authorize URL and its state (for the cookie), or null without Client ID or secret.</summary>
    public async Task<(string Url, string State)?> BeginAuthorizeAsync(string redirectUri, CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.CanConnect) return null;
        var state = Pkce.NewState();
        lock (_sync)
        {
            Prune();
            while (_pending.Count >= MaxPending)
                _pending.Remove(_pending.MinBy(kv => kv.Value.CreatedUtc).Key);
            _pending[state] = new Pending(redirectUri, _clock());
        }
        return (AuthorizeUrl(s.ClientId, redirectUri, state), state);
    }

    public static string AuthorizeUrl(string clientId, string redirectUri, string state) =>
        MieleApi.AuthorizeUrl
        + "?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(clientId)
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&scope=" + Uri.EscapeDataString(MieleApi.Scope)
        + "&state=" + Uri.EscapeDataString(state);

    /// <summary>The redirect back from Miele: unknown/expired/used state → rejected without any call; else code → tokens.</summary>
    public async Task<(MieleCallbackResult Result, string? Detail)> CompleteAsync(string? state, string? code, string? error, CancellationToken ct)
    {
        Pending? pending = null;
        lock (_sync)
        {
            Prune();
            if (!string.IsNullOrEmpty(state) && _pending.Remove(state, out var p)) pending = p;
        }
        if (pending is null)
        {
            _log.LogWarning("Miele callback rejected: unknown or expired state");
            return (MieleCallbackResult.InvalidState, null);
        }
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            _log.LogWarning("Miele authorization refused: {Error}", error ?? "no code");
            return (MieleCallbackResult.Denied, error);
        }
        var s = await _settings.GetAsync(ct);
        if (!s.CanConnect || _settings.ClientSecret is not { } secret) return (MieleCallbackResult.NotConfigured, null);
        try
        {
            var token = await _client.ExchangeCodeAsync(s.ClientId, secret, code, pending.RedirectUri, ct);
            await _settings.SaveTokenAsync(token, ct);
            _log.LogInformation("Miele account connected (access token valid until {Expires:u}, refresh token {Refresh})",
                token.ExpiresAtUtc, string.IsNullOrEmpty(token.RefreshToken) ? "missing" : "received");
        }
        catch (Exception ex) when (ex is MieleException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            _log.LogWarning("Miele code exchange failed: {Reason}", ex.Message);
            return (MieleCallbackResult.ExchangeFailed, ex.Message);
        }
        try { Connected?.Invoke(); } catch (Exception ex) { _log.LogDebug(ex, "Connected handler failed"); }
        return (MieleCallbackResult.Connected, null);
    }

    /// <summary>A valid access token, refreshed from 10 % of its lifetime before expiry.</summary>
    /// <exception cref="MieleDisconnectedException">Not configured, not connected, or a refused refresh not yet due for its hourly retry.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        var token = Usable(s);
        if (s.AuthError is null && !string.IsNullOrEmpty(token.AccessToken) && _clock() < token.RefreshDueUtc)
            return token.AccessToken;
        return await RefreshAsync(force: s.AuthError is not null, ct);
    }

    /// <summary>After an API 401: refresh now, whatever the stored expiry says (still at most once an hour after a refusal).</summary>
    public async Task<string> ForceRefreshAsync(CancellationToken ct)
    {
        Usable(await _settings.GetAsync(ct));
        return await RefreshAsync(force: true, ct);
    }

    /// <summary>„Deconectează”: <c>POST /logout</c> (best effort), tokens cleared, the worker drops the stream.</summary>
    public async Task LogoutAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (s.Token is { AccessToken.Length: > 0 } t)
        {
            try
            {
                await _client.LogoutAsync(t.AccessToken, ct);
                _log.LogInformation("Miele tokens invalidated (logout)");
            }
            catch (Exception ex) when (ex is MieleException or HttpRequestException or TaskCanceledException)
            {
                _log.LogWarning("Miele logout failed ({Reason}); tokens cleared locally", ex.Message);
            }
        }
        await _settings.ClearTokensAsync("Deconectat de utilizator.", ct);
        try { Disconnected?.Invoke(); } catch (Exception ex) { _log.LogDebug(ex, "Disconnected handler failed"); }
    }

    private MieleToken Usable(MieleRuntimeSettings s)
    {
        if (!s.HasClientId) throw new MieleDisconnectedException("Lipsește Client ID-ul Miele (Setări → Miele).");
        if (!s.HasSecret) throw new MieleDisconnectedException("Lipsește secretul clientului Miele (" + (s.SecretProblem ?? "necunoscut") + ").");
        if (s.Token is not { } token) throw new MieleDisconnectedException(s.AuthError ?? "Contul Miele nu e conectat.");
        if (s.AuthError is not null && s.RefreshRefusedUtc is { } refused && _clock() - refused < MieleApi.RefreshRetryAfterRefusal)
            throw new MieleDisconnectedException(s.AuthError);
        return token;
    }

    private async Task<string> RefreshAsync(bool force, CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed (or failed) while this one waited.
            var current = await _settings.GetAsync(ct);
            var token = Usable(current);
            if (!force && current.AuthError is null && !string.IsNullOrEmpty(token.AccessToken) && _clock() < token.RefreshDueUtc)
                return token.AccessToken;
            if (string.IsNullOrEmpty(token.RefreshToken))
            {
                const string reason = "Tokenul Miele trebuie reînnoit, dar nu există refresh token: reconectează contul.";
                await _settings.MarkRefreshRefusedAsync(reason, _clock(), ct);
                throw new MieleDisconnectedException(reason);
            }
            if (_settings.ClientSecret is not { } secret) throw new MieleDisconnectedException("Lipsește secretul clientului Miele.");
            try
            {
                var fresh = await _client.RefreshAsync(current.ClientId, secret, token.RefreshToken, ct);
                await _settings.SaveTokenAsync(fresh, ct);
                _log.LogInformation("Miele access token refreshed (valid until {Expires:u}, refresh token {Rotation})", fresh.ExpiresAtUtc,
                    fresh.RefreshToken == token.RefreshToken ? "kept" : "rotated");
                return fresh.AccessToken;
            }
            catch (MieleAuthException ex)
            {
                var reason = "Reînnoirea tokenului Miele a fost refuzată (" + ex.Message + "): reconectează contul. Următoarea încercare automată peste o oră.";
                _log.LogWarning("Miele refresh refused, account marked disconnected: {Reason}", ex.Message);
                await _settings.MarkRefreshRefusedAsync(reason, _clock(), ct);
                throw new MieleDisconnectedException(reason);
            }
            catch (Exception ex) when (ex is MieleException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       && !ct.IsCancellationRequested)
            {
                // Temporary (5xx, 503 limit, network): the access token is used while it is still valid.
                if (current.AuthError is null && !string.IsNullOrEmpty(token.AccessToken) && token.ExpiresAtUtc - TimeSpan.FromMinutes(1) > _clock())
                {
                    _log.LogWarning("Miele refresh failed temporarily ({Reason}); current access token used until it expires", ex.Message);
                    return token.AccessToken;
                }
                throw;
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
