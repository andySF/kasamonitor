using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>
/// Credential encoding for AES <c>login_device</c>, as in python-kasa <c>transports/aestransport.py</c>
/// (<c>hash_credentials</c>; <c>_sha1</c> there returns the HEX digest):
/// username = base64(hex(sha1(email))) for both versions;
/// login_version 2: <c>password2</c> = base64(hex(sha1(password)));
/// login_version 1: <c>password</c> = base64(password).
/// </summary>
public static class AesLogin
{
    public static JsonObject Params(int loginVersion, TapoCredentials creds)
    {
        var username = B64(Sha1Hex(creds.Username));
        return loginVersion == 2
            ? new JsonObject { ["password2"] = B64(Sha1Hex(creds.Password)), ["username"] = username }
            : new JsonObject { ["password"] = B64(creds.Password), ["username"] = username };
    }

    private static string Sha1Hex(string s) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
}

/// <summary>
/// AES-128-CBC session of the passthrough protocol (python-kasa <c>AesEncyptionSession</c>): key and IV are the first
/// and second 16 bytes of the RSA-decrypted handshake key; the same IV is used for every message; payloads are
/// PKCS7-padded and base64-encoded.
/// </summary>
public sealed class AesPassthroughSession
{
    private readonly byte[] _key;
    private readonly byte[] _iv;

    public AesPassthroughSession(byte[] key, byte[] iv)
    {
        if (key.Length != 16 || iv.Length != 16) throw new ArgumentException("AES key and IV must be 16 bytes.");
        _key = key;
        _iv = iv;
    }

    /// <summary>From the base64 <c>result.key</c> of the handshake answer, RSA/PKCS#1 v1.5-encrypted with our public key.</summary>
    public static AesPassthroughSession FromHandshakeKey(string handshakeKeyBase64, RSA privateKey)
    {
        var keyAndIv = privateKey.Decrypt(Convert.FromBase64String(handshakeKeyBase64), RSAEncryptionPadding.Pkcs1);
        if (keyAndIv.Length < 32) throw new KasaProtocolException($"AES handshake key has {keyAndIv.Length} bytes, expected 32.", operation: "handshake");
        return new AesPassthroughSession(keyAndIv[..16], keyAndIv[16..32]);
    }

    public string Encrypt(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), _iv, PaddingMode.PKCS7));
    }

    public string Decrypt(string base64)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        return Encoding.UTF8.GetString(aes.DecryptCbc(Convert.FromBase64String(base64), _iv, PaddingMode.PKCS7));
    }
}

/// <summary>
/// TP-Link "securePassthrough" transport ("AES" in discovery), python-kasa <c>AesTransport</c>:
/// <list type="number">
/// <item><c>POST http://ip/app</c> <c>{"method":"handshake","params":{"key":"&lt;RSA-1024 public key PEM&gt;"}}</c> →
/// <c>result.key</c> (base64, RSA-encrypted key+IV) and cookie <c>TP_SESSIONID</c>;</item>
/// <item><c>login_device</c> sent through <c>securePassthrough</c> → <c>result.token</c>;</item>
/// <item>requests: <c>POST /app?token=…</c> <c>{"method":"securePassthrough","params":{"request":"&lt;b64 AES&gt;"}}</c>,
/// answer in <c>result.response</c>.</item>
/// </list>
/// The login version (discovery <c>lv</c>) is not stored per plug, so version 2 is tried first and then version 1;
/// the version that worked is remembered per device.
/// </summary>
public sealed class AesTransport : ISmartTransport, IDisposable
{
    private readonly HttpClient _http;
    private readonly int _port;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, DeviceSlot> _slots = new();

    public AesTransport(TimeSpan timeout, HttpMessageHandler? handler = null, int port = 80, Func<DateTimeOffset>? clock = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        { Timeout = timeout };
        _port = port;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Protocol => PlugProtocols.Aes;

    public async Task<string> SendAsync(IPAddress ip, TapoCredentials credentials, string requestJson, CancellationToken ct)
    {
        var slot = _slots.GetOrAdd(ip.ToString(), _ => new DeviceSlot());
        await slot.Lock.WaitAsync(ct);
        try
        {
            var reused = slot.Session is not null && slot.Token is not null
                         && slot.ExpiresAt > _clock() && Equals(slot.Credentials, credentials);
            if (!reused) await ConnectAsync(ip, credentials, slot, ct);

            try
            {
                return await PassthroughAsync(ip, slot, requestJson, useToken: true, ct);
            }
            catch (Exception) when (reused && !ct.IsCancellationRequested)
            {
                await ConnectAsync(ip, credentials, slot, ct);
                return await PassthroughAsync(ip, slot, requestJson, useToken: true, ct);
            }
        }
        catch
        {
            slot.Session = null;
            slot.Token = null;
            throw;
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    private async Task ConnectAsync(IPAddress ip, TapoCredentials credentials, DeviceSlot slot, CancellationToken ct)
    {
        var versions = slot.LoginVersion is { } known ? new[] { known, known == 2 ? 1 : 2 } : new[] { 2, 1 };
        string? lastError = null;
        foreach (var version in versions)
        {
            await HandshakeAsync(ip, slot, ct);
            var login = new JsonObject
            {
                ["method"] = "login_device",
                ["params"] = AesLogin.Params(version, credentials),
                ["request_time_milis"] = _clock().ToUnixTimeMilliseconds(),
            };
            string answer;
            try
            {
                answer = await PassthroughAsync(ip, slot, login.ToJsonString(), useToken: false, ct);
            }
            catch (KasaProtocolException ex) when (ex.ErrCode != 0)
            {
                lastError = $"login_device v{version}: error_code {ex.ErrCode}";
                continue;
            }
            using var doc = JsonDocument.Parse(answer);
            var code = ErrorCode(doc.RootElement);
            if (code == 0 && doc.RootElement.TryGetProperty("result", out var r)
                          && r.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String)
            {
                slot.Token = tok.GetString();
                slot.LoginVersion = version;
                slot.Credentials = credentials;
                return;
            }
            lastError = $"login_device v{version}: error_code {code}";
        }
        throw new TapoAuthenticationException($"AES login with {ip} failed ({lastError}): device did not accept the TP-Link credentials.");
    }

    private async Task HandshakeAsync(IPAddress ip, DeviceSlot slot, CancellationToken ct)
    {
        slot.Session = null;
        slot.Token = null;
        slot.Cookie = null;
        using var rsa = RSA.Create(1024);
        var pem = "-----BEGIN PUBLIC KEY-----\n" + Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()) + "\n-----END PUBLIC KEY-----\n";
        var body = new JsonObject
        {
            ["method"] = "handshake",
            ["params"] = new JsonObject { ["key"] = pem },
        };

        using var resp = await PostAsync(AppUrl(ip, token: null), body.ToJsonString(), cookie: null, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new KasaProtocolException($"AES handshake to {ip} returned HTTP {(int)resp.StatusCode}.", operation: "handshake");
        using var doc = JsonDocument.Parse(text);
        var code = ErrorCode(doc.RootElement);
        if (code != 0)
            throw new KasaProtocolException($"AES handshake to {ip} returned error_code {code}.", code, "handshake");
        var key = doc.RootElement.GetProperty("result").GetProperty("key").GetString()
                  ?? throw new KasaProtocolException("AES handshake answer has no key.", operation: "handshake");

        var (cookie, timeout) = TapoSessionCookies.Parse(resp);
        slot.Session = AesPassthroughSession.FromHandshakeKey(key, rsa);
        slot.Cookie = cookie;
        slot.ExpiresAt = _clock() + TapoSessionCookies.Lifetime(timeout);
    }

    private async Task<string> PassthroughAsync(IPAddress ip, DeviceSlot slot, string requestJson, bool useToken, CancellationToken ct)
    {
        var session = slot.Session ?? throw new InvalidOperationException("No AES session.");
        var body = new JsonObject
        {
            ["method"] = "securePassthrough",
            ["params"] = new JsonObject { ["request"] = session.Encrypt(requestJson) },
        };
        using var resp = await PostAsync(AppUrl(ip, useToken ? slot.Token : null), body.ToJsonString(), slot.Cookie, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new KasaProtocolException($"AES request to {ip} returned HTTP {(int)resp.StatusCode}.", operation: "securePassthrough");
        using var doc = JsonDocument.Parse(text);
        var code = ErrorCode(doc.RootElement);
        if (code != 0)
            throw new KasaProtocolException($"AES securePassthrough to {ip} returned error_code {code}.", code, "securePassthrough");
        var inner = doc.RootElement.GetProperty("result").GetProperty("response").GetString()
                    ?? throw new KasaProtocolException("securePassthrough answer has no response.", operation: "securePassthrough");
        try
        {
            return session.Decrypt(inner);
        }
        catch (CryptographicException ex)
        {
            throw new KasaProtocolException($"AES answer from {ip} could not be decrypted: {ex.Message}", operation: "securePassthrough");
        }
    }

    private Uri AppUrl(IPAddress ip, string? token) =>
        new(token is null ? $"http://{ip}:{_port}/app" : $"http://{ip}:{_port}/app?token={Uri.EscapeDataString(token)}");

    private async Task<HttpResponseMessage> PostAsync(Uri url, string json, string? cookie, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.TryAddWithoutValidation("requestByApp", "true");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (cookie is not null) req.Headers.TryAddWithoutValidation("Cookie", TapoSessionCookies.Header(cookie));
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"AES request to {url.Host} timed out after {_http.Timeout.TotalMilliseconds:0} ms.");
        }
    }

    private static int ErrorCode(JsonElement root) =>
        root.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;

    public void Dispose() => _http.Dispose();

    private sealed class DeviceSlot
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public AesPassthroughSession? Session;
        public string? Cookie;
        public string? Token;
        public int? LoginVersion;
        public TapoCredentials? Credentials;
        public DateTimeOffset ExpiresAt;
    }
}
