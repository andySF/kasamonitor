using System.Net.WebSockets;
using System.Text.Json;
using FluentAssertions;
using KasaMonitor.Core.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class ShellyRpcSocketTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static long Id(JsonElement frame) => frame.GetProperty("id").GetInt64();

    [Fact]
    public async Task Answers_AreMatchedById_EvenOutOfOrder_AndEveryFrameHasASource()
    {
        await using var server = new FakeShellyWsServer();
        var gate = new TaskCompletionSource();
        var frames = new List<JsonElement>();
        server.Handler = async frame =>
        {
            lock (frames)
            {
                frames.Add(frame);
                if (frames.Count < 2) return;
            }
            // Both requests in: answer the second one first.
            var ordered = frames.OrderByDescending(Id).ToList();
            foreach (var f in ordered)
            {
                var ch = f.GetProperty("params").GetProperty("id").GetInt32();
                await server.SendAsync($$$"""{"id":{{{Id(f)}}},"src":"shellyemg3-d885ac0b743c","dst":"x","result":{"id":{{{ch}}},"act_power":{{{100 + ch}}}.5}}""");
            }
            gate.TrySetResult();
        };
        await using var socket = await ShellyRpcSocket.ConnectAsync(server.Uri, null, Timeout, CancellationToken.None);

        var ch0 = socket.CallAsync("EM1.GetStatus", new Dictionary<string, object> { ["id"] = 0 }, Timeout, CancellationToken.None);
        var ch1 = socket.CallAsync("EM1.GetStatus", new Dictionary<string, object> { ["id"] = 1 }, Timeout, CancellationToken.None);
        var results = await Task.WhenAll(ch0, ch1);

        results[0].GetProperty("act_power").GetDouble().Should().Be(100.5);
        results[1].GetProperty("act_power").GetDouble().Should().Be(101.5);
        frames.Should().OnlyContain(f => f.GetProperty("src").GetString()!.StartsWith("kasamonitor-") && f.GetProperty("method").GetString() == "EM1.GetStatus");
        frames.Select(Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Notifications_AreRaised_AndDoNotDisturbPendingCalls()
    {
        await using var server = new FakeShellyWsServer();
        server.Handler = async frame =>
        {
            await server.SendAsync("""{"src":"shellyemg3-d885ac0b743c","dst":"x","method":"NotifyStatus","params":{"ts":1793560000.12,"switch:0":{"id":0,"output":false}}}""");
            await server.SendAsync($$$"""{"id":{{{Id(frame)}}},"src":"shellyemg3-d885ac0b743c","result":{"ok":true}}""");
        };
        await using var socket = await ShellyRpcSocket.ConnectAsync(server.Uri, null, Timeout, CancellationToken.None);
        var notes = new List<(string Method, JsonElement Params)>();
        socket.Notification += (m, p) => { lock (notes) notes.Add((m, p)); };

        var result = await socket.CallAsync("Shelly.GetStatus", null, Timeout, CancellationToken.None);

        result.GetProperty("ok").GetBoolean().Should().BeTrue();
        notes.Should().ContainSingle().Which.Method.Should().Be("NotifyStatus");
        ShellyStatusParser.Relays(notes[0].Params).Should().Equal((0, false));
    }

    [Fact]
    public async Task NoAnswer_TimesOut_AndALateAnswerIsIgnored()
    {
        await using var server = new FakeShellyWsServer();
        var first = new TaskCompletionSource<JsonElement>();
        server.Handler = async frame =>
        {
            if (frame.GetProperty("method").GetString() == "Slow") { first.TrySetResult(frame); return; }
            await server.SendAsync($$$"""{"id":{{{Id(frame)}}},"result":{"fast":true}}""");
        };
        await using var socket = await ShellyRpcSocket.ConnectAsync(server.Uri, null, Timeout, CancellationToken.None);

        var slow = () => socket.CallAsync("Slow", null, TimeSpan.FromMilliseconds(200), CancellationToken.None);
        await slow.Should().ThrowAsync<TimeoutException>();

        await server.SendAsync($$$"""{"id":{{{Id(await first.Task)}}},"result":{"late":true}}""");   // late answer: no pending call
        var fast = await socket.CallAsync("Fast", null, Timeout, CancellationToken.None);
        fast.TryGetProperty("fast", out _).Should().BeTrue();
    }

    [Fact]
    public async Task RpcError_BecomesAnException_WithTheCode()
    {
        await using var server = new FakeShellyWsServer();
        server.Handler = frame => server.SendAsync($$$"""{"id":{{{Id(frame)}}},"error":{"code":-114,"message":"Method EM9.GetStatus failed: not found"}}""");
        await using var socket = await ShellyRpcSocket.ConnectAsync(server.Uri, null, Timeout, CancellationToken.None);

        var act = () => socket.CallAsync("EM9.GetStatus", null, Timeout, CancellationToken.None);

        (await act.Should().ThrowAsync<ShellyRpcException>()).Which.HttpStatus.Should().Be(-114);
    }

    [Fact]
    public async Task DeviceClosing_FailsPendingCalls_AndCompletes()
    {
        await using var server = new FakeShellyWsServer();
        server.Handler = _ => server.CloseAsync();
        await using var socket = await ShellyRpcSocket.ConnectAsync(server.Uri, null, Timeout, CancellationToken.None);

        var act = () => socket.CallAsync("Shelly.GetStatus", null, Timeout, CancellationToken.None);

        await act.Should().ThrowAsync<WebSocketException>();
        await socket.Completion.WaitAsync(Timeout);
        socket.CloseReason.Should().NotBeNull();
    }

    [Fact]
    public async Task Digest_Challenge401_IsAnsweredWithAnAuthObject_OnEveryLaterFrame()
    {
        const string challenge = """{\"auth_type\": \"digest\", \"nonce\": 1625038762, \"nc\": 1, \"realm\": \"shellyemg3-d885ac0b743c\", \"algorithm\": \"SHA-256\"}""";
        await using var server = new FakeShellyWsServer();
        server.Handler = frame =>
        {
            if (!frame.TryGetProperty("auth", out var auth))
                return server.SendAsync($$$"""{"id":{{{Id(frame)}}},"error":{"code":401,"message":"{{{challenge}}}"}}""");
            var c = new DigestAuth.WsChallenge("shellyemg3-d885ac0b743c", 1625038762, 1, "SHA-256");
            var expected = DigestAuth.BuildWsAuth(c, "admin", "secret", auth.GetProperty("cnonce").GetInt64());
            var ok = auth.GetProperty("response").GetString() == (string)expected["response"]
                     && auth.GetProperty("username").GetString() == "admin"
                     && auth.GetProperty("realm").GetString() == "shellyemg3-d885ac0b743c";
            return server.SendAsync(ok
                ? $$$"""{"id":{{{Id(frame)}}},"result":{"authed":true}}"""
                : $$$"""{"id":{{{Id(frame)}}},"error":{"code":401,"message":"{{{challenge}}}"}}""");
        };
        await using var socket = await ShellyRpcSocket.ConnectAsync(server.Uri, "secret", Timeout, CancellationToken.None);

        (await socket.CallAsync("Shelly.GetStatus", null, Timeout, CancellationToken.None)).GetProperty("authed").GetBoolean().Should().BeTrue();
        (await socket.CallAsync("EM1.GetStatus", new Dictionary<string, object> { ["id"] = 0 }, Timeout, CancellationToken.None))
            .GetProperty("authed").GetBoolean().Should().BeTrue();

        server.Received.Should().HaveCount(3);   // 401, retry with auth, then auth straight away
        server.Received[2].TryGetProperty("auth", out _).Should().BeTrue();
    }

    [Fact]
    public async Task Digest_WithoutPassword_ReportsPasswordMissing()
    {
        await using var server = new FakeShellyWsServer();
        server.Handler = frame => server.SendAsync($$$"""{"id":{{{Id(frame)}}},"error":{"code":401,"message":"{\"auth_type\":\"digest\",\"nonce\":1,\"nc\":1,\"realm\":\"r\",\"algorithm\":\"SHA-256\"}"}}""");
        await using var socket = await ShellyRpcSocket.ConnectAsync(server.Uri, null, Timeout, CancellationToken.None);

        var act = () => socket.CallAsync("Shelly.GetStatus", null, Timeout, CancellationToken.None);

        (await act.Should().ThrowAsync<ShellyAuthenticationException>()).Which.PasswordMissing.Should().BeTrue();
    }

    [Fact]
    public void WsChallenge_Parse_AndResponseFormula()
    {
        var c = DigestAuth.ParseWsChallenge("""{"auth_type":"digest","nonce":1625038762,"nc":1,"realm":"shellypro4pm-f008d1d8b8b8","algorithm":"SHA-256"}""")!;
        c.Should().Be(new DigestAuth.WsChallenge("shellypro4pm-f008d1d8b8b8", 1625038762, 1, "SHA-256"));
        DigestAuth.ParseWsChallenge("not json").Should().BeNull();

        var auth = DigestAuth.BuildWsAuth(c, "admin", "pw", cnonce: 313273957);
        static string H(string s) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        var expected = H($"{H("admin:shellypro4pm-f008d1d8b8b8:pw")}:1625038762:1:313273957:auth:{H("dummy_method:dummy_uri")}");
        auth["response"].Should().Be(expected);
        auth["nonce"].Should().Be(1625038762L);
        auth["cnonce"].Should().Be(313273957L);
    }
}
