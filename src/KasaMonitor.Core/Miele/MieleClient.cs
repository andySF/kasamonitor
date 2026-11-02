using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace KasaMonitor.Core.Miele;

/// <summary>
/// Tokens of the Miele account (never logged). <see cref="LifetimeSeconds"/> = <c>expires_in</c> of the answer; the
/// token is refreshed from <see cref="RefreshDueUtc"/> on (10 % of the lifetime before it expires).
/// </summary>
public sealed record MieleToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc, long LifetimeSeconds)
{
    public DateTimeOffset RefreshDueUtc => ExpiresAtUtc - TimeSpan.FromSeconds(Math.Max(0, LifetimeSeconds) * MieleApi.RefreshBeforeExpiryFraction);
}

/// <summary>Base of the Miele errors; messages never contain tokens, codes, the client id or the secret.</summary>
public class MieleException : Exception
{
    public MieleException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The token endpoint refused the code or refresh token for good, or the API answered 401.</summary>
public sealed class MieleAuthException : MieleException
{
    public MieleAuthException(string message) : base(message) { }
}

/// <summary>Any other non-success answer (503 = request/connection limit per IP, 5xx, 404 …): temporary.</summary>
public sealed class MieleApiException : MieleException
{
    public int StatusCode { get; }

    public MieleApiException(int statusCode, string message) : base(message) => StatusCode = statusCode;
}

/// <summary>An open <c>text/event-stream</c> response, read line by line.</summary>
public interface IMieleEventStream : IAsyncDisposable
{
    /// <summary>The next line (without CR/LF), or null at the end of the stream.</summary>
    Task<string?> ReadLineAsync(CancellationToken ct);
}

public interface IMieleClient
{
    /// <summary>Authorization code → tokens (POST x-www-form-urlencoded with the client secret).</summary>
    Task<MieleToken> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct);

    /// <summary>Refresh token → new access token AND new refresh token (rotation: the old one is no longer valid).</summary>
    Task<MieleToken> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct);

    /// <summary>POST /thirdparty/logout with the access token (invalidates access and refresh token).</summary>
    Task LogoutAsync(string accessToken, CancellationToken ct);

    /// <summary>GET /v1/devices: ident + state of every appliance the user granted. Returns the raw JSON.</summary>
    Task<string> GetDevicesJsonAsync(string accessToken, CancellationToken ct);

    /// <summary>GET /v1/devices/all/events (SSE). The caller disposes the stream.</summary>
    Task<IMieleEventStream> OpenEventsAsync(string accessToken, CancellationToken ct);
}

/// <summary>HTTP client of the Miele OAuth provider and 3rd Party API. Read-only: no command is ever sent to an appliance.</summary>
public sealed class MieleClient : IMieleClient
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="timeout">Per request; the SSE stream stays open (its idle timeout belongs to the worker).</param>
    /// <param name="handler">Test hook (fake server); production uses the default handler.</param>
    public MieleClient(TimeSpan timeout, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(MieleApi.UserAgent);
        _timeout = timeout;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<MieleToken> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        }, null, ct);

    public Task<MieleToken> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
        }, refreshToken, ct);

    public async Task LogoutAsync(string accessToken, CancellationToken ct)
    {
        using var cts = Timed(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, MieleApi.LogoutUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var resp = await _http.SendAsync(req, cts.Token);
        if (!resp.IsSuccessStatusCode)
            throw new MieleApiException((int)resp.StatusCode, "logout HTTP " + (int)resp.StatusCode);
    }

    public async Task<string> GetDevicesJsonAsync(string accessToken, CancellationToken ct)
    {
        using var cts = Timed(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, MieleApi.DevicesUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        ThrowIfFailed(resp, body, "devices");
        return body;
    }

    public async Task<IMieleEventStream> OpenEventsAsync(string accessToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, MieleApi.EventsUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        HttpResponseMessage? resp = null;
        try
        {
            using (var cts = Timed(ct))
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                ThrowIfFailed(resp, body, "events");
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var result = new EventStream(req, resp, new StreamReader(stream, Encoding.UTF8));
            resp = null;
            return result;
        }
        finally
        {
            if (resp is not null)
            {
                resp.Dispose();
                req.Dispose();
            }
        }
    }

    private sealed class EventStream : IMieleEventStream
    {
        private readonly HttpRequestMessage _req;
        private readonly HttpResponseMessage _resp;
        private readonly StreamReader _reader;

        public EventStream(HttpRequestMessage req, HttpResponseMessage resp, StreamReader reader)
        {
            _req = req;
            _resp = resp;
            _reader = reader;
        }

        public async Task<string?> ReadLineAsync(CancellationToken ct) => await _reader.ReadLineAsync(ct);

        public ValueTask DisposeAsync()
        {
            _reader.Dispose();
            _resp.Dispose();
            _req.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private CancellationTokenSource Timed(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        return cts;
    }

    private static void ThrowIfFailed(HttpResponseMessage resp, string body, string what)
    {
        if (resp.IsSuccessStatusCode) return;
        var status = (int)resp.StatusCode;
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new MieleAuthException(what + " HTTP 401");
        throw new MieleApiException(status, what + " HTTP " + status + (ApiMessage(body) is { } m ? ": " + m : ""));
    }

    private async Task<MieleToken> TokenAsync(Dictionary<string, string> form, string? previousRefresh, CancellationToken ct)
    {
        using var cts = Timed(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, MieleApi.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, cts.Token);
        var body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            var (error, description) = OAuthError(body);
            var status = (int)resp.StatusCode;
            var text = "token endpoint HTTP " + status + (error is null ? "" : " " + error) + (description is null ? "" : ": " + description);
            if (IsGrantRefused(status, error)) throw new MieleAuthException(text);
            throw new MieleApiException(status, text);
        }
        return ParseToken(body, _clock(), previousRefresh);
    }

    /// <summary>
    /// The token endpoint refused for good (HTTP 400/401/403 with <c>invalid_grant</c>, <c>invalid_token</c>,
    /// <c>invalid_client</c> or <c>unauthorized_client</c>): retrying soon would not help and only spends the token quota.
    /// </summary>
    public static bool IsGrantRefused(int status, string? error) =>
        status is 400 or 401 or 403 && error is "invalid_grant" or "invalid_token" or "invalid_client" or "unauthorized_client";

    /// <summary>
    /// Parses a token answer. The refresh token rotates: the new one replaces <paramref name="previousRefresh"/>; only an
    /// answer without <c>refresh_token</c> keeps the previous one. <c>expires_in</c> missing → 1 hour (conservative).
    /// </summary>
    public static MieleToken ParseToken(string json, DateTimeOffset now, string? previousRefresh = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (string.IsNullOrEmpty(access)) throw new MieleAuthException("token answer without access_token");
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        long expiresIn = 3600;
        if (root.TryGetProperty("expires_in", out var e))
        {
            if (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) && n > 0) expiresIn = n;
            else if (e.ValueKind == JsonValueKind.String && long.TryParse(e.GetString(), out var s) && s > 0) expiresIn = s;
        }
        return new MieleToken(access, string.IsNullOrEmpty(refresh) ? previousRefresh : refresh, now.AddSeconds(expiresIn), expiresIn);
    }

    private static (string? Error, string? Description) OAuthError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            return (Str(root, "error"), Str(root, "error_description"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>The <c>message</c> of an API error body (<c>{"code":503,"message":"unavailable"}</c>), if any.</summary>
    private static string? ApiMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "message") : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
