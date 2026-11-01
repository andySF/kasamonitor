using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>
/// KLAP over HTTP (python-kasa <c>KlapTransportV2</c> for Tapo SMART devices):
/// POST 16 random bytes to <c>/app/handshake1</c>, the device answers remote_seed(16) + server_hash(32) and sets the
/// <c>TP_SESSIONID</c> cookie; the client checks server_hash against its auth hash, POSTs the handshake2 payload to
/// <c>/app/handshake2</c>, then sends requests to <c>/app/request?seq=N</c> encrypted with <see cref="KlapSession"/>.
/// Sessions are cached per device and serialized (the sequence number must not interleave).
/// </summary>
public sealed class KlapTransport : ISmartTransport, IDisposable
{

    // python-kasa DEFAULT_CREDENTIALS["TAPO"] (base64 in its source), and blank credentials, are tried after the
    // user's credentials, exactly like KlapTransport.perform_handshake1 does (factory-reset / not-yet-bound devices).
    private static readonly TapoCredentials DefaultTapoCredentials = new("test@tp-link.net", "test");
    private static readonly TapoCredentials BlankCredentials = new("", "");

    private readonly HttpClient _http;
    private readonly KlapAuthVersion _version;
    private readonly int _port;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ConcurrentDictionary<string, DeviceSlot> _slots = new();

    public KlapTransport(TimeSpan timeout, HttpMessageHandler? handler = null, int port = 80,
        KlapAuthVersion version = KlapAuthVersion.V2, Func<DateTimeOffset>? clock = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        { Timeout = timeout };
        _version = version;
        _port = port;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public string Protocol => PlugProtocols.Klap;

    public async Task<string> SendAsync(IPAddress ip, TapoCredentials credentials, string requestJson, CancellationToken ct)
    {
        var slot = _slots.GetOrAdd(ip.ToString(), _ => new DeviceSlot());
        await slot.Lock.WaitAsync(ct);
        try
        {
            var authHash = KlapCrypto.AuthHash(_version, credentials);
            var reused = slot.Session is not null
                         && slot.ExpiresAt > _clock()
                         && slot.UserAuthHash is not null
                         && CryptographicOperations.FixedTimeEquals(slot.UserAuthHash, authHash);
            if (!reused) await HandshakeAsync(ip, credentials, slot, ct);

            try
            {
                return await RequestAsync(ip, slot, requestJson, ct);
            }
            catch (Exception) when (reused && !ct.IsCancellationRequested)
            {
                // Cached session may have been dropped by the device (reboot, timeout): one fresh handshake.
                await HandshakeAsync(ip, credentials, slot, ct);
                return await RequestAsync(ip, slot, requestJson, ct);
            }
        }
        catch
        {
            slot.Session = null;
            throw;
        }
        finally
        {
            slot.Lock.Release();
        }
    }

    private Uri Url(IPAddress ip, string pathAndQuery) => new($"http://{ip}:{_port}/app/{pathAndQuery}");

    private async Task HandshakeAsync(IPAddress ip, TapoCredentials credentials, DeviceSlot slot, CancellationToken ct)
    {
        slot.Session = null;
        var localSeed = RandomNumberGenerator.GetBytes(16);

        using var hs1 = await PostAsync(Url(ip, "handshake1"), localSeed, cookie: null, ct);
        var hs1Body = await hs1.Content.ReadAsByteArrayAsync(ct);
        if (hs1.StatusCode != HttpStatusCode.OK)
            throw new KasaProtocolException($"KLAP handshake1 to {ip} returned HTTP {(int)hs1.StatusCode}.", operation: "handshake1");
        if (hs1Body.Length < 48)
            throw new KasaProtocolException($"KLAP handshake1 answer from {ip} has {hs1Body.Length} bytes, expected 48.", operation: "handshake1");

        var remoteSeed = hs1Body[..16];
        var serverHash = hs1Body[16..48];
        var (cookie, timeout) = TapoSessionCookies.Parse(hs1);

        var userHash = KlapCrypto.AuthHash(_version, credentials);
        byte[]? matched = null;
        foreach (var candidate in new[] { userHash, KlapCrypto.AuthHash(_version, DefaultTapoCredentials), KlapCrypto.AuthHash(_version, BlankCredentials) })
        {
            var expected = KlapCrypto.Handshake1ServerHash(_version, localSeed, remoteSeed, candidate);
            if (CryptographicOperations.FixedTimeEquals(expected, serverHash)) { matched = candidate; break; }
        }
        if (matched is null)
            throw new TapoAuthenticationException($"KLAP handshake1 with {ip}: device did not accept the TP-Link credentials.");

        var payload = KlapCrypto.Handshake2Payload(_version, localSeed, remoteSeed, matched);
        using var hs2 = await PostAsync(Url(ip, "handshake2"), payload, cookie, ct);
        if (hs2.StatusCode != HttpStatusCode.OK)
            throw new KasaProtocolException($"KLAP handshake2 to {ip} returned HTTP {(int)hs2.StatusCode}.", operation: "handshake2");

        slot.Session = new KlapSession(localSeed, remoteSeed, matched);
        slot.Cookie = cookie;
        slot.UserAuthHash = userHash;
        slot.ExpiresAt = _clock() + TapoSessionCookies.Lifetime(timeout);
    }

    private async Task<string> RequestAsync(IPAddress ip, DeviceSlot slot, string requestJson, CancellationToken ct)
    {
        var session = slot.Session ?? throw new InvalidOperationException("No KLAP session.");
        var (body, seq) = session.Encrypt(requestJson);
        using var resp = await PostAsync(Url(ip, $"request?seq={seq}"), body, slot.Cookie, ct);
        var respBody = await resp.Content.ReadAsByteArrayAsync(ct);
        if (resp.StatusCode != HttpStatusCode.OK)
            throw new KasaProtocolException($"KLAP request to {ip} returned HTTP {(int)resp.StatusCode}.", operation: "request");
        try
        {
            return session.Decrypt(respBody, seq);
        }
        catch (CryptographicException ex)
        {
            throw new KasaProtocolException($"KLAP answer from {ip} could not be decrypted: {ex.Message}", operation: "request");
        }
    }

    private async Task<HttpResponseMessage> PostAsync(Uri url, byte[] body, string? cookie, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (cookie is not null) req.Headers.TryAddWithoutValidation("Cookie", TapoSessionCookies.Header(cookie));
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"KLAP request to {url.Host} timed out after {_http.Timeout.TotalMilliseconds:0} ms.");
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class DeviceSlot
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public KlapSession? Session;
        public string? Cookie;
        public byte[]? UserAuthHash;
        public DateTimeOffset ExpiresAt;
    }
}
