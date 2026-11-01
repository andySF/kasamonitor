using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Tapo;

/// <summary>
/// Vectors from a Python re-implementation of python-kasa aestransport.py (hash_credentials with hex sha1,
/// AesEncyptionSession with PKCS7 + base64): user "user@example.com", password "secret-pass",
/// key = bytes(0..15), iv = bytes(16..31).
/// </summary>
public class AesTransportTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.168.254.130");
    private static readonly TapoCredentials Good = new("user@example.com", "secret-pass");

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void LoginParams_V2_HexSha1Base64()
    {
        var p = AesLogin.Params(2, Good);
        p["username"]!.GetValue<string>().Should().Be("NjNhNzEwNTY5MjYxYTI0YjM3NjYyNzViNzAwMGNlOGQ3YjMyZTJmNw==");
        p["password2"]!.GetValue<string>().Should().Be("ZDg5NGE5NzFkOGUzYmZlNzdmNTExNzU4ZTY0OTljYWE0YTYyN2RmMA==");
        p.ContainsKey("password").Should().BeFalse();
    }

    [Fact]
    public void LoginParams_V1_PlainBase64Password()
    {
        var p = AesLogin.Params(1, Good);
        p["username"]!.GetValue<string>().Should().Be("NjNhNzEwNTY5MjYxYTI0YjM3NjYyNzViNzAwMGNlOGQ3YjMyZTJmNw==");
        p["password"]!.GetValue<string>().Should().Be("c2VjcmV0LXBhc3M=");
        p.ContainsKey("password2").Should().BeFalse();
    }

    [Fact]
    public void Session_EncryptsExactlyLikeReference_AndRoundTrips()
    {
        var s = new AesPassthroughSession(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            Enumerable.Range(16, 16).Select(i => (byte)i).ToArray());
        const string msg = "{\"method\":\"login_device\",\"params\":{\"username\":\"x\"}}";

        var enc = s.Encrypt(msg);

        enc.Should().Be("sbme+DTuHHZx6iqk4H9wbDERjvlFv4XRkrk5eyuNb2M7dXtQetokH3lNkWaGUVLILvSA9j4Ph2WMx734l5C7hw==");
        s.Decrypt(enc).Should().Be(msg);
    }

    [Fact]
    public void Session_FromHandshakeKey_DecryptsRsaPkcs1()
    {
        using var rsa = RSA.Create(1024);
        var keyAndIv = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var handshakeKey = Convert.ToBase64String(rsa.Encrypt(keyAndIv, RSAEncryptionPadding.Pkcs1));

        var s = AesPassthroughSession.FromHandshakeKey(handshakeKey, rsa);

        s.Encrypt("{\"method\":\"login_device\",\"params\":{\"username\":\"x\"}}")
            .Should().Be("sbme+DTuHHZx6iqk4H9wbDERjvlFv4XRkrk5eyuNb2M7dXtQetokH3lNkWaGUVLILvSA9j4Ph2WMx734l5C7hw==");
    }

    /// <summary>Simulates a Tapo plug speaking securePassthrough on /app.</summary>
    private sealed class FakeAesDevice : HttpMessageHandler
    {
        private readonly int _loginVersion;
        private readonly JsonObject _expectedLogin;
        private AesPassthroughSession? _session;
        private string? _cookie;
        private string? _token;

        public int Handshakes;
        public int LoginAttempts;
        public bool ExpireTokenOnce;
        public readonly List<string> Methods = new();
        public string? LastContentType;
        public bool RequestByApp;

        public FakeAesDevice(TapoCredentials creds, int loginVersion)
        {
            _loginVersion = loginVersion;
            _expectedLogin = AesLogin.Params(loginVersion, creds);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            RequestByApp = request.Headers.TryGetValues("requestByApp", out var rba) && rba.Single() == "true";
            if (request.RequestUri!.AbsolutePath != "/app") return new HttpResponseMessage(HttpStatusCode.NotFound);
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var method = doc.RootElement.GetProperty("method").GetString();

            if (method == "handshake")
            {
                Handshakes++;
                var pem = doc.RootElement.GetProperty("params").GetProperty("key").GetString()!;
                using var pub = RSA.Create();
                pub.ImportFromPem(pem);
                var keyAndIv = RandomNumberGenerator.GetBytes(32);
                _session = new AesPassthroughSession(keyAndIv[..16], keyAndIv[16..]);
                _cookie = Guid.NewGuid().ToString("N");
                _token = null;
                var key = Convert.ToBase64String(pub.Encrypt(keyAndIv, RSAEncryptionPadding.Pkcs1));
                var resp = Json(new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["key"] = key } });
                resp.Headers.TryAddWithoutValidation("Set-Cookie", $"TP_SESSIONID={_cookie};TIMEOUT=1440");
                return resp;
            }

            if (method != "securePassthrough" || _session is null) return Json(new JsonObject { ["error_code"] = -1 });
            var cookieOk = request.Headers.TryGetValues("Cookie", out var c) && c.Single() == $"TP_SESSIONID={_cookie}";
            if (!cookieOk) return new HttpResponseMessage(HttpStatusCode.Forbidden);

            var inner = JsonNode.Parse(_session.Decrypt(doc.RootElement.GetProperty("params").GetProperty("request").GetString()!))!;
            var innerMethod = inner["method"]!.GetValue<string>();
            Methods.Add(innerMethod);
            string answer;
            if (innerMethod == "login_device")
            {
                LoginAttempts++;
                var ok = JsonNode.DeepEquals(inner["params"], _expectedLogin);
                _token = ok ? "TOKEN" + Handshakes : null;
                answer = ok ? $"{{\"error_code\":0,\"result\":{{\"token\":\"{_token}\"}}}}" : "{\"error_code\":-1501}";
            }
            else
            {
                var tokenOk = request.RequestUri.Query == $"?token={_token}" && _token is not null;
                if (ExpireTokenOnce) { ExpireTokenOnce = false; tokenOk = false; }
                if (!tokenOk) return Json(new JsonObject { ["error_code"] = 9999 });
                answer = innerMethod switch
                {
                    "get_device_info" => LoadFixture("tapo_device_info_p115.json"),
                    "get_energy_usage" => LoadFixture("tapo_energy_usage_p115.json"),
                    "get_emeter_data" => LoadFixture("tapo_emeter_data_p115.json"),
                    _ => "{\"error_code\":-1}",
                };
            }
            return Json(new JsonObject
            {
                ["error_code"] = 0,
                ["result"] = new JsonObject { ["response"] = _session.Encrypt(answer) },
            });
        }

        private static HttpResponseMessage Json(JsonObject o) =>
            new(HttpStatusCode.OK) { Content = new StringContent(o.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private static (TapoClient Client, FakeAesDevice Device) Create(int loginVersion)
    {
        var device = new FakeAesDevice(Good, loginVersion);
        return (new TapoClient(new ISmartTransport[] { new AesTransport(TimeSpan.FromSeconds(2), device) }), device);
    }

    [Fact]
    public async Task GetDeviceInfo_OverAes_LoginV2()
    {
        var (client, device) = Create(loginVersion: 2);

        var info = await client.GetDeviceInfoAsync(Ip, PlugProtocols.Aes, Good, CancellationToken.None);

        info.Alias.Should().Be("Frigider");
        info.Mac.Should().Be("b01921c4c028");
        device.Methods.Should().Equal("login_device", "get_device_info");
        device.LastContentType.Should().Be("application/json");
        device.RequestByApp.Should().BeTrue();
    }

    [Fact]
    public async Task LoginV1Device_FallsBackAfterV2_AndRemembersVersion()
    {
        var (client, device) = Create(loginVersion: 1);

        var r = await client.GetEnergyUsageAsync(Ip, PlugProtocols.Aes, Good, CancellationToken.None);

        r.PowerMw.Should().Be(109_269);
        r.VoltageMv.Should().Be(231_150);
        r.CurrentMa.Should().Be(754);
        device.LoginAttempts.Should().Be(2);           // v2 rejected, v1 accepted
        device.Methods.Should().Equal("login_device", "login_device", "get_energy_usage", "get_emeter_data");
        device.Handshakes.Should().Be(2);              // fresh handshake per login attempt; session reused afterwards
    }

    [Fact]
    public async Task WrongCredentials_ThrowTapoAuthenticationException()
    {
        var (client, _) = Create(loginVersion: 2);

        var act = () => client.GetDeviceInfoAsync(Ip, PlugProtocols.Aes, new TapoCredentials("user@example.com", "wrong"), CancellationToken.None);

        await act.Should().ThrowAsync<TapoAuthenticationException>();
    }

    [Fact]
    public async Task ExpiredToken_IsRenegotiatedOnce()
    {
        var (client, device) = Create(loginVersion: 2);
        await client.GetDeviceInfoAsync(Ip, PlugProtocols.Aes, Good, CancellationToken.None);
        device.ExpireTokenOnce = true;

        var info = await client.GetDeviceInfoAsync(Ip, PlugProtocols.Aes, Good, CancellationToken.None);

        info.Alias.Should().Be("Frigider");
        device.Handshakes.Should().Be(2);
    }
}
