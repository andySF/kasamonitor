using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace KasaMonitor.Core.Heating;

/// <summary>Tokens of the ViCare account (never logged). <see cref="RefreshToken"/> may be null when the server sends none.</summary>
public sealed record ViessmannToken(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAtUtc);

/// <summary>Base of the Viessmann errors; messages never contain tokens, codes or the client id.</summary>
public class ViessmannException : Exception
{
    public ViessmannException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The token endpoint refused the grant for good (invalid_grant / invalid_token) or the API answered 401.</summary>
public sealed class ViessmannAuthException : ViessmannException
{
    public ViessmannAuthException(string message) : base(message) { }
}

/// <summary>HTTP 429: the call quota is used up until <see cref="ResetUtc"/> (when the API says it).</summary>
public sealed class ViessmannRateLimitException : ViessmannException
{
    public DateTimeOffset? ResetUtc { get; }
    public string? LimitName { get; }

    public ViessmannRateLimitException(DateTimeOffset? resetUtc, string? limitName)
        : base(Describe(resetUtc, limitName))
    {
        ResetUtc = resetUtc;
        LimitName = limitName;
    }

    private static string Describe(DateTimeOffset? resetUtc, string? limitName) =>
        "Viessmann API rate limit exceeded (" + (limitName ?? "unknown limit") + ")"
        + (resetUtc is { } r ? ", reset at " + r.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "");
}

/// <summary>Any other non-success answer (e.g. gateway offline, 5xx).</summary>
public sealed class ViessmannApiException : ViessmannException
{
    public int StatusCode { get; }
    public string? ErrorType { get; }

    public ViessmannApiException(int statusCode, string? errorType, string message)
        : base("Viessmann API HTTP " + statusCode + (errorType is null ? "" : " " + errorType) + ": " + message)
    {
        StatusCode = statusCode;
        ErrorType = errorType;
    }
}

public interface IViessmannClient
{
    /// <summary>Authorization code + PKCE verifier → tokens (POST x-www-form-urlencoded to the token endpoint).</summary>
    Task<ViessmannToken> ExchangeCodeAsync(string clientId, string code, string codeVerifier, string redirectUri, CancellationToken ct);

    /// <summary>Refresh token → new access token (and a new refresh token when the server rotates it).</summary>
    Task<ViessmannToken> RefreshAsync(string clientId, string refreshToken, CancellationToken ct);

    /// <summary>GET /equipment/installations?includeGateways=true (one API call). Returns the raw JSON.</summary>
    Task<string> GetInstallationsJsonAsync(string accessToken, CancellationToken ct);

    /// <summary>The bulk features call of one gateway (one API call). Returns the raw JSON.</summary>
    Task<string> GetFeaturesJsonAsync(string accessToken, string installationId, string gatewaySerial, CancellationToken ct);
}

/// <summary>HTTP client of the Viessmann IAM (OAuth) and IoT API. Read-only: no command is ever sent to the boiler.</summary>
public sealed class ViessmannClient : IViessmannClient
{
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _clock;

    /// <param name="handler">Test hook (fake server); production uses the default handler.</param>
    public ViessmannClient(TimeSpan timeout, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
    {
        _http = handler is null ? new HttpClient { Timeout = timeout } : new HttpClient(handler) { Timeout = timeout };
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<ViessmannToken> ExchangeCodeAsync(string clientId, string code, string codeVerifier, string redirectUri, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier,
            ["code"] = code,
        }, null, ct);

    public Task<ViessmannToken> RefreshAsync(string clientId, string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        }, refreshToken, ct);

    public Task<string> GetInstallationsJsonAsync(string accessToken, CancellationToken ct) =>
        GetAsync(ViessmannApi.InstallationsUrl, accessToken, ct);

    public Task<string> GetFeaturesJsonAsync(string accessToken, string installationId, string gatewaySerial, CancellationToken ct) =>
        GetAsync(ViessmannApi.FeaturesUrl(installationId, gatewaySerial), accessToken, ct);

    private async Task<ViessmannToken> TokenAsync(Dictionary<string, string> form, string? previousRefresh, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ViessmannApi.TokenUrl) { Content = new FormUrlEncodedContent(form) };
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var (error, description) = OAuthError(body);
            var status = (int)resp.StatusCode;
            var text = "token endpoint HTTP " + status + (error is null ? "" : " " + error) + (description is null ? "" : ": " + description);
            if (IsGrantRefused(status, error)) throw new ViessmannAuthException(text);
            // Anything else (429, 5xx, invalid_client, …) is temporary: retried at the next interval, tokens kept.
            throw new ViessmannApiException(status, error, text);
        }
        return ParseToken(body, _clock(), previousRefresh);
    }

    /// <summary>
    /// The token endpoint refused the code or refresh token for good: HTTP 400/401/403 with <c>invalid_grant</c> or
    /// <c>invalid_token</c>. Only this disconnects the account.
    /// </summary>
    public static bool IsGrantRefused(int status, string? error) =>
        status is 400 or 401 or 403 && error is "invalid_grant" or "invalid_token";

    /// <summary>Parses a token answer; a missing <c>refresh_token</c> keeps <paramref name="previousRefresh"/>.</summary>
    public static ViessmannToken ParseToken(string json, DateTimeOffset now, string? previousRefresh = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (string.IsNullOrEmpty(access)) throw new ViessmannAuthException("token answer without access_token");
        var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var secs) ? secs : 3600;
        return new ViessmannToken(access, string.IsNullOrEmpty(refresh) ? previousRefresh : refresh, now.AddSeconds(expiresIn));
    }

    private async Task<string> GetAsync(string url, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (resp.IsSuccessStatusCode) return body;

        var (errorType, message, limitName, resetMs) = ApiError(body);
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            DateTimeOffset? reset = resetMs is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
            if (reset is null && resp.Headers.RetryAfter is { } ra)
                reset = ra.Date ?? (ra.Delta is { } d ? _clock() + d : null);
            throw new ViessmannRateLimitException(reset, limitName);
        }
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new ViessmannAuthException("API HTTP 401" + (errorType is null ? "" : " " + errorType));
        throw new ViessmannApiException((int)resp.StatusCode, errorType, message ?? resp.ReasonPhrase ?? "error");
    }

    /// <summary>
    /// Error body of the IoT API: <c>errorType</c>, <c>message</c> and, for 429, <c>extendedPayload.name</c> and
    /// <c>extendedPayload.limitReset</c> (unix ms). The client and user ids of the payload are deliberately not read.
    /// </summary>
    public static (string? ErrorType, string? Message, string? LimitName, long? LimitResetMs) ApiError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null, null);
            string? limitName = null;
            long? reset = null;
            if (root.TryGetProperty("extendedPayload", out var ext) && ext.ValueKind == JsonValueKind.Object)
            {
                limitName = Str(ext, "name");
                if (ext.TryGetProperty("limitReset", out var lr) && lr.TryGetInt64(out var ms)) reset = ms;
            }
            return (Str(root, "errorType"), Str(root, "message"), limitName, reset);
        }
        catch (JsonException)
        {
            return (null, null, null, null);
        }
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

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
