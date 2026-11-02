namespace KasaMonitor.Core.Miele;

/// <summary>
/// Miele 3rd Party API (developer.miele.com): the new OAuth2 provider (Keycloak, authorization code with client secret,
/// no PKCE), the REST base and the SSE stream, plus the timings KasaMonitor uses. Single owner of these values (worker,
/// settings card, README read them from here). Read-only in 0.8.5: the scope has no <c>mcs_thirdparty_write</c>.
/// </summary>
public static class MieleApi
{
    public const string AuthorizeUrl = "https://auth.domestic.miele-iot.com/partner/realms/mcs/protocol/openid-connect/auth";
    /// <summary>Code exchange and refresh. The spec documents refresh on the legacy host; the new provider's own token endpoint is used (as pymiele/Home Assistant do).</summary>
    public const string TokenUrl = "https://auth.domestic.miele-iot.com/partner/realms/mcs/protocol/openid-connect/token";
    /// <summary>"Invalidates the current access- and refresh token" (spec <c>POST /logout</c>, Bearer access token, answer 204).</summary>
    public const string LogoutUrl = "https://api.mcs3.miele.com/thirdparty/logout";
    public const string ApiBase = "https://api.mcs3.miele.com/v1";
    /// <summary><c>openid</c> is mandatory for the new provider; <c>mcs_thirdparty_read</c> covers every GET (devices, events).</summary>
    public const string Scope = "openid mcs_thirdparty_read";

    /// <summary>Starts the OAuth flow (redirect to Miele).</summary>
    public const string ConnectPath = "/miele/connect";
    /// <summary>OAuth redirect inside KasaMonitor: <c>{scheme}://{host}/miele/callback</c> of the current request.</summary>
    public const string CallbackPath = "/miele/callback";

    public static string DevicesUrl => ApiBase + "/devices";
    /// <summary>One SSE subscription for every appliance of the account (the docs recommend it to keep connections few).</summary>
    public static string EventsUrl => ApiBase + "/devices/all/events";

    /// <summary>
    /// <c>GET /v1/devices/{id}/lastUsedPrograms</c>: up to five finished runs, newest first (start/end UTC, calculated
    /// water and energy, optionally the 15-min energy profile with <c>calculateEnergyMonitoring=true</c>).
    /// </summary>
    public static string LastUsedProgramsUrl(string deviceId, bool calculateEnergyMonitoring) =>
        ApiBase + "/devices/" + Uri.EscapeDataString(deviceId) + "/lastUsedPrograms" + (calculateEnergyMonitoring ? "?calculateEnergyMonitoring=true" : "");

    /// <summary>The program history is read at connection, then this often (and <see cref="HistoryAfterCycleDelay"/> after a cycle ended).</summary>
    public static readonly TimeSpan HistorySyncInterval = TimeSpan.FromHours(6);
    /// <summary>Wait after an observed cycle ended before asking for the history (so Miele has the run).</summary>
    public static readonly TimeSpan HistoryAfterCycleDelay = TimeSpan.FromMinutes(2);
    /// <summary>After 429/503 without <c>Retry-After</c> (or after a failed history read) the next history call waits this long.</summary>
    public static readonly TimeSpan HistoryBackoff = TimeSpan.FromHours(1);
    /// <summary>An API run and an observed cycle are the same run when their starts are at most this far apart …</summary>
    public static readonly TimeSpan HistoryMatchWindow = TimeSpan.FromMinutes(10);
    /// <summary>… or when they overlap by at least this share of the shorter one.</summary>
    public const double HistoryMatchOverlap = 0.5;

    /// <summary>User-Agent of every request (best practices ask for an own one).</summary>
    public const string UserAgent = "KasaMonitor";

    /// <summary>The access token is refreshed when less than this share of its lifetime (<c>expires_in</c>) is left.</summary>
    public const double RefreshBeforeExpiryFraction = 0.1;
    /// <summary>After the token endpoint refused the refresh token: the next attempt waits at least this long (token quota per client).</summary>
    public static readonly TimeSpan RefreshRetryAfterRefusal = TimeSpan.FromHours(1);

    /// <summary>PING comes every ~20 s of silence; no line for this long = dead connection, reconnect.</summary>
    public static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromMinutes(5);
    /// <summary>After this many SSE failures in a row, <c>GET /devices</c> every <see cref="PollInterval"/> until SSE works again.</summary>
    public const int StreamFailuresBeforePolling = 3;
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    /// <summary>A state snapshot is stored on every change and, without a change, at most this often (sign of life).</summary>
    public static readonly TimeSpan SnapshotHeartbeat = TimeSpan.FromMinutes(5);
    /// <summary>An open cycle is closed when the appliance has not been Running for this long.</summary>
    public static readonly TimeSpan CycleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>"properties without a current value return -32768" (general concept page).</summary>
    public const int NoValue = -32768;

    /// <summary>Default path of the client secret file (Docker secret / read-only bind mount).</summary>
    public const string DefaultClientSecretFile = "/run/secrets/miele-client.secret";

    /// <summary>Reconnect wait after <paramref name="consecutiveFailures"/> failures: 5 s, 10 s, 20 s … at most 5 minutes.</summary>
    public static TimeSpan ReconnectDelay(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return MinReconnectDelay;
        var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 20));
        var delay = TimeSpan.FromSeconds(MinReconnectDelay.TotalSeconds * factor);
        return delay > MaxReconnectDelay ? MaxReconnectDelay : delay;
    }
}
