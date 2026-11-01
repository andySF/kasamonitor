using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Tapo;

public class KlapTransportTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.168.254.146");
    private static readonly TapoCredentials Good = new("user@example.com", "secret-pass");

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    /// <summary>Simulates a Tapo plug's /app/handshake1|2 and /app/request endpoints (KLAP v2).</summary>
    private sealed class FakeKlapDevice : HttpMessageHandler
    {
        private readonly byte[] _authHash;
        private byte[]? _localSeed, _remoteSeed;
        private KlapSession? _session;
        private string? _cookie;

        public int Handshake1Count;
        public int RequestCount;
        public bool DropSessionOnce;
        public readonly List<string> Methods = new();
        public Func<string, string> Answer = _ => "{\"error_code\":0,\"result\":{}}";

        public FakeKlapDevice(TapoCredentials deviceCredentials) =>
            _authHash = KlapCrypto.AuthHash(KlapAuthVersion.V2, deviceCredentials);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(ct);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/app/handshake1")
            {
                Handshake1Count++;
                _localSeed = body;
                _remoteSeed = RandomNumberGenerator.GetBytes(16);
                _cookie = Guid.NewGuid().ToString("N");
                var hash = KlapCrypto.Handshake1ServerHash(KlapAuthVersion.V2, _localSeed, _remoteSeed, _authHash);
                var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_remoteSeed.Concat(hash).ToArray()) };
                resp.Headers.TryAddWithoutValidation("Set-Cookie", $"TP_SESSIONID={_cookie};TIMEOUT=86400");
                return resp;
            }

            var cookieOk = request.Headers.TryGetValues("Cookie", out var c) && c.Single() == $"TP_SESSIONID={_cookie}";
            if (path == "/app/handshake2")
            {
                var expected = KlapCrypto.Handshake2Payload(KlapAuthVersion.V2, _localSeed!, _remoteSeed!, _authHash);
                if (!cookieOk || !expected.SequenceEqual(body)) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                _session = new KlapSession(_localSeed!, _remoteSeed!, _authHash);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (path == "/app/request")
            {
                RequestCount++;
                if (DropSessionOnce) { DropSessionOnce = false; _session = null; }
                if (!cookieOk || _session is null) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                var seq = int.Parse(request.RequestUri.Query.Split('=')[1]);
                var json = _session.DecryptRequest(body, seq);
                var method = JsonDocument.Parse(json).RootElement.GetProperty("method").GetString()!;
                Methods.Add(method);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_session.EncryptResponse(Answer(method), seq)) };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static (TapoClient Client, FakeKlapDevice Device) Create(TapoCredentials deviceCreds)
    {
        var device = new FakeKlapDevice(deviceCreds)
        {
            Answer = m => m switch
            {
                "get_device_info" => LoadFixture("tapo_device_info_p115.json"),
                "get_energy_usage" => LoadFixture("tapo_energy_usage_p115.json"),
                "get_emeter_data" => LoadFixture("tapo_emeter_data_p115.json"),
                _ => "{\"error_code\":-1}",
            }
        };
        var transport = new KlapTransport(TimeSpan.FromSeconds(2), device);
        return (new TapoClient(new ISmartTransport[] { transport }), device);
    }

    [Fact]
    public async Task GetDeviceInfo_OverKlap_MapsP115()
    {
        var (client, device) = Create(Good);

        var info = await client.GetDeviceInfoAsync(Ip, PlugProtocols.Klap, Good, CancellationToken.None);

        info.Mac.Should().Be("b01921c4c028");
        info.Alias.Should().Be("Frigider");   // nickname is base64
        info.Model.Should().Be("P115");
        info.SwVer.Should().StartWith("1.4.3");
        info.RelayOn.Should().BeTrue();
        info.RssiDbm.Should().Be(-48);
        device.Handshake1Count.Should().Be(1);
    }

    [Fact]
    public async Task GetEnergyUsage_CombinesEnergyUsageAndEmeterData()
    {
        var (client, device) = Create(Good);

        var r = await client.GetEnergyUsageAsync(Ip, PlugProtocols.Klap, Good, CancellationToken.None);

        r.PowerMw.Should().Be(109_269);
        r.VoltageMv.Should().Be(231_150);
        r.CurrentMa.Should().Be(754);
        r.TotalWh.Should().Be(458);
        device.Methods.Should().Equal("get_energy_usage", "get_emeter_data");
        device.Handshake1Count.Should().Be(1); // session reused for the second request
    }

    [Fact]
    public async Task GetEnergyUsage_WithoutEmeterData_LeavesVoltageAndCurrentNull_AndStopsAsking()
    {
        var (client, device) = Create(Good);
        device.Answer = m => m == "get_energy_usage" ? LoadFixture("tapo_energy_usage_p115.json") : "{\"error_code\":-1}";

        var r1 = await client.GetEnergyUsageAsync(Ip, PlugProtocols.Klap, Good, CancellationToken.None);
        await client.GetEnergyUsageAsync(Ip, PlugProtocols.Klap, Good, CancellationToken.None);

        r1.VoltageMv.Should().BeNull();
        r1.CurrentMa.Should().BeNull();
        r1.PowerMw.Should().Be(109_269);
        device.Methods.Should().Equal("get_energy_usage", "get_emeter_data", "get_energy_usage");
    }

    [Fact]
    public async Task WrongCredentials_ThrowTapoAuthenticationException()
    {
        var (client, _) = Create(Good);

        var act = () => client.GetDeviceInfoAsync(Ip, PlugProtocols.Klap, new TapoCredentials("user@example.com", "wrong"), CancellationToken.None);

        await act.Should().ThrowAsync<TapoAuthenticationException>();
    }

    [Fact]
    public async Task DroppedSession_IsRenegotiatedOnce()
    {
        var (client, device) = Create(Good);
        await client.GetDeviceInfoAsync(Ip, PlugProtocols.Klap, Good, CancellationToken.None);
        device.DropSessionOnce = true;

        var info = await client.GetDeviceInfoAsync(Ip, PlugProtocols.Klap, Good, CancellationToken.None);

        info.Alias.Should().Be("Frigider");
        device.Handshake1Count.Should().Be(2);
    }

    [Fact]
    public void SmartRequest_HasMethodTimeAndTerminalUuid()
    {
        var json = SmartMessages.BuildRequest("get_device_info", DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("method").GetString().Should().Be("get_device_info");
        doc.RootElement.GetProperty("request_time_milis").GetInt64().Should().Be(1_700_000_000_000);
        doc.RootElement.GetProperty("terminal_uuid").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void SmartResult_ErrorCode_Throws()
    {
        var act = () => SmartMessages.ParseResult("{\"error_code\":-1012}", "get_device_info");
        act.Should().Throw<KasaMonitor.Core.Protocol.KasaProtocolException>().Which.ErrCode.Should().Be(-1012);
    }
}
