namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>TP_SESSIONID / TIMEOUT cookies set by Tapo devices on the handshake answer (KLAP and AES).</summary>
internal static class TapoSessionCookies
{
    public const string SessionCookieName = "TP_SESSIONID";

    // python-kasa SESSION_EXPIRE_BUFFER_SECONDS: renegotiate 20 minutes before the device-side timeout.
    private static readonly TimeSpan ExpireBuffer = TimeSpan.FromMinutes(20);

    public static (string? SessionId, TimeSpan Timeout) Parse(HttpResponseMessage resp)
    {
        string? session = null;
        var timeout = TimeSpan.FromHours(24);
        if (resp.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var header in values)
            foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;
                var name = part[..eq];
                var value = part[(eq + 1)..];
                if (name.Equals(SessionCookieName, StringComparison.OrdinalIgnoreCase)) session = value;
                else if (name.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out var secs) && secs > 0)
                    timeout = TimeSpan.FromSeconds(secs);
            }
        }
        return (session, timeout);
    }

    public static string Header(string sessionId) => $"{SessionCookieName}={sessionId}";

    public static TimeSpan Lifetime(TimeSpan timeout) =>
        timeout > ExpireBuffer * 2 ? timeout - ExpireBuffer : TimeSpan.FromMinutes(10);
}
