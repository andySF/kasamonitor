using System.Security.Cryptography;
using System.Text;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>
/// HTTP Digest access authentication (RFC 7616), client side, for Shelly Gen2+ devices with <c>auth_en: true</c>
/// (user <c>admin</c>, <c>algorithm=SHA-256</c>, <c>qop=auth</c>). MD5 is accepted too; the "-sess" variants and
/// <c>auth-int</c> are not used by Shelly and are not implemented.
/// </summary>
public static class DigestAuth
{
    public sealed record Challenge(string Realm, string Nonce, string? Qop, string Algorithm, string? Opaque);

    /// <summary>Parses a <c>WWW-Authenticate: Digest ...</c> header value; null when it is not a usable Digest challenge.</summary>
    public static Challenge? ParseChallenge(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var text = header.Trim();
        if (!text.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return null;
        var p = ParseParams(text["Digest".Length..]);
        if (!p.TryGetValue("realm", out var realm) || !p.TryGetValue("nonce", out var nonce)) return null;
        var algorithm = p.TryGetValue("algorithm", out var a) && a.Length > 0 ? a : "MD5";
        if (!IsSupported(algorithm)) return null;
        string? qop = null;
        if (p.TryGetValue("qop", out var q))
            qop = q.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(x => x.Equals("auth", StringComparison.OrdinalIgnoreCase));
        return new Challenge(realm, nonce, qop, algorithm, p.GetValueOrDefault("opaque"));
    }

    public static bool IsSupported(string algorithm) =>
        algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase) || algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// RFC 7616 §3.4.1: response = H(H(user:realm:password) ":" nonce ":" nc ":" cnonce ":" qop ":" H(method:uri)),
    /// or H(HA1:nonce:HA2) without qop (RFC 2069 compatibility). Lowercase hex.
    /// </summary>
    public static string ComputeResponse(string algorithm, string user, string realm, string password,
        string method, string uri, string nonce, string? qop, string nc, string cnonce)
    {
        var ha1 = Hash(algorithm, $"{user}:{realm}:{password}");
        var ha2 = Hash(algorithm, $"{method}:{uri}");
        return qop is null
            ? Hash(algorithm, $"{ha1}:{nonce}:{ha2}")
            : Hash(algorithm, $"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
    }

    /// <summary>Value of the <c>Authorization</c> header (without the header name).</summary>
    public static string BuildAuthorization(Challenge c, string user, string password, string method, string uri,
        int nonceCount, string? cnonce = null)
    {
        cnonce ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var nc = nonceCount.ToString("x8");
        var response = ComputeResponse(c.Algorithm, user, c.Realm, password, method, uri, c.Nonce, c.Qop, nc, cnonce);
        var sb = new StringBuilder("Digest ");
        sb.Append($"username=\"{user}\", realm=\"{c.Realm}\", nonce=\"{c.Nonce}\", uri=\"{uri}\", algorithm={c.Algorithm}, response=\"{response}\"");
        if (c.Qop is not null) sb.Append($", qop={c.Qop}, nc={nc}, cnonce=\"{cnonce}\"");
        if (c.Opaque is not null) sb.Append($", opaque=\"{c.Opaque}\"");
        return sb.ToString();
    }

    /// <summary>Challenge of Shelly RPC over WebSocket: carried as JSON in the <c>message</c> of an RPC error with code 401.</summary>
    public sealed record WsChallenge(string Realm, long Nonce, long Nc, string Algorithm);

    /// <summary>
    /// Parses <c>{"auth_type":"digest","nonce":1625038762,"nc":1,"realm":"shellypro4pm-…","algorithm":"SHA-256"}</c>
    /// (Shelly Gen2+ docs, "Authentication" over WebSocket); null when it is not a usable digest challenge.
    /// </summary>
    public static WsChallenge? ParseWsChallenge(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(message);
            var root = doc.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var realm = ShellyJson.Str(root, "realm");
            var nonce = ShellyJson.Num(root, "nonce");
            var algorithm = ShellyJson.Str(root, "algorithm") ?? "SHA-256";
            if (realm is null || nonce is null || !IsSupported(algorithm)) return null;
            return new WsChallenge(realm, (long)nonce.Value, (long)(ShellyJson.Num(root, "nc") ?? 1), algorithm);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>auth</c> object added to every RPC frame after a WebSocket challenge:
    /// response = H(H(user:realm:password) ":" nonce ":" nc ":" cnonce ":auth:" H("dummy_method:dummy_uri")).
    /// </summary>
    public static Dictionary<string, object> BuildWsAuth(WsChallenge c, string user, string password, long? cnonce = null)
    {
        var cn = cnonce ?? (long)(BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4)) & 0x7FFFFFFF);
        var ha1 = Hash(c.Algorithm, $"{user}:{c.Realm}:{password}");
        var ha2 = Hash(c.Algorithm, "dummy_method:dummy_uri");
        var response = Hash(c.Algorithm, $"{ha1}:{c.Nonce}:{c.Nc}:{cn}:auth:{ha2}");
        return new Dictionary<string, object>
        {
            ["realm"] = c.Realm,
            ["username"] = user,
            ["nonce"] = c.Nonce,
            ["cnonce"] = cn,
            ["response"] = response,
            ["algorithm"] = c.Algorithm,
        };
    }

    private static string Hash(string algorithm, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var digest = algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase) ? SHA256.HashData(bytes) : MD5.HashData(bytes);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    /// <summary>key=value / key="quoted, value" pairs separated by commas.</summary>
    private static Dictionary<string, string> ParseParams(string s)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] == ',' || char.IsWhiteSpace(s[i]))) i++;
            var eq = s.IndexOf('=', i);
            if (eq < 0) break;
            var key = s[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < s.Length && s[i] == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;   // quoted-pair
                    sb.Append(s[i]);
                    i++;
                }
                i++; // closing quote
                value = sb.ToString();
            }
            else
            {
                var end = s.IndexOf(',', i);
                if (end < 0) end = s.Length;
                value = s[i..end].Trim();
                i = end;
            }
            if (key.Length > 0) result[key] = value;
        }
        return result;
    }
}
