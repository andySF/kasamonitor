using System.Security.Cryptography;
using System.Text;

namespace KasaMonitor.Core.Heating;

/// <summary>OAuth 2.0 PKCE (RFC 7636) with the S256 method, plus the random <c>state</c> value.</summary>
public static class Pkce
{
    /// <summary>A 64-character code verifier (48 random bytes, base64url), within the 43–128 range of RFC 7636.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    /// <summary>A random <c>state</c> (32 bytes, base64url) that ties the callback to the request that started it.</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary><c>code_challenge</c> = BASE64URL(SHA256(ASCII(verifier))).</summary>
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
