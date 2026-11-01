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
