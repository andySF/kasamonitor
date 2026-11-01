using System.Net;
using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class ShellyClientTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.168.254.141");

    /// <summary>A fake device: answers by path; with a password it enforces digest SHA-256 like a Gen2+ device.</summary>
    private sealed class FakeDevice : HttpMessageHandler
    {
        public string? Password { get; init; }
        public string Realm { get; init; } = "shellyemg3-d885ac0b743c";
        public string Nonce { get; set; } = "1793544000";
        public Func<string, HttpResponseMessage> Answer { get; init; } = _ => Json("{}");
        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> AuthHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var pathAndQuery = request.RequestUri!.PathAndQuery;
            if (Password is not null)
            {
                var auth = request.Headers.TryGetValues("Authorization", out var v) ? v.Single() : null;
                if (auth is not null) AuthHeaders.Add(auth);
                if (auth is null || !Valid(auth, pathAndQuery))
                {
                    var r = new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("") };
                    r.Headers.TryAddWithoutValidation("WWW-Authenticate", $"Digest qop=\"auth\", realm=\"{Realm}\", nonce=\"{Nonce}\", algorithm=SHA-256");
                    return Task.FromResult(r);
                }
            }
            return Task.FromResult(Answer(pathAndQuery));
        }

        private bool Valid(string header, string uri)
        {
            string Field(string name)
            {
                var m = System.Text.RegularExpressions.Regex.Match(header, name + "=\"?([^\",]+)\"?");
                return m.Success ? m.Groups[1].Value : "";
            }
            if (Field("nonce") != Nonce || Field("uri") != uri || Field("username") != "admin") return false;
            var expected = DigestAuth.ComputeResponse("SHA-256", "admin", Realm, Password!, "GET", uri, Nonce, "auth", Field("nc"), Field("cnonce"));
            return Field("response") == expected;
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task NoAuth_GetDeviceInfo()
    {
        var device = new FakeDevice { Answer = _ => Json(ShellyFixtures.Text("shelly_emg3_deviceinfo.json")) };
        using var client = new ShellyClient(handler: device);

        var info = await client.GetDeviceInfoAsync(Ip, CancellationToken.None);

        info.Model.Should().Be("S3EM-002CXCEU");
        device.Requests.Single().RequestUri!.ToString().Should().Be("http://192.168.254.141/rpc/Shelly.GetDeviceInfo");
    }

    [Fact]
    public async Task Digest_AnswersTheChallenge_ThenReusesTheNonce()
    {
        var device = new FakeDevice { Password = "test-pass", Answer = _ => Json(ShellyFixtures.Text("shelly_emg3_status.json")) };
        using var client = new ShellyClient(handler: device);

        var status = await client.GetStatusAsync(Ip, "test-pass", CancellationToken.None);
        status.TryGetProperty("em1:0", out _).Should().BeTrue();
        device.Requests.Should().HaveCount(2);           // 401, then the authenticated request

        await client.GetStatusAsync(Ip, "test-pass", CancellationToken.None);
        device.Requests.Should().HaveCount(3);           // cached challenge: one request
        device.AuthHeaders.Last().Should().Contain("nc=00000002");

        device.Nonce = "new-nonce";                      // device rotated its nonce: one 401, then success
        await client.GetStatusAsync(Ip, "test-pass", CancellationToken.None);
        device.Requests.Should().HaveCount(5);
    }

    [Fact]
    public async Task Digest_WithoutPassword_IsPasswordMissing()
    {
        var device = new FakeDevice { Password = "test-pass" };
        using var client = new ShellyClient(handler: device);

        var act = () => client.GetStatusAsync(Ip, null, CancellationToken.None);

        (await act.Should().ThrowAsync<ShellyAuthenticationException>()).Which.PasswordMissing.Should().BeTrue();
    }

    [Fact]
    public async Task Digest_WrongPassword_IsRejected()
    {
        var device = new FakeDevice { Password = "test-pass" };
        using var client = new ShellyClient(handler: device);

        var act = () => client.GetStatusAsync(Ip, "wrong", CancellationToken.None);

        (await act.Should().ThrowAsync<ShellyAuthenticationException>()).Which.PasswordMissing.Should().BeFalse();
    }

    [Fact]
    public async Task RpcError_Throws()
    {
        var device = new FakeDevice { Answer = _ => Json("""{"code":-114,"message":"Method EM1Data.GetRecords failed: not found"}""", HttpStatusCode.NotFound) };
        using var client = new ShellyClient(handler: device);

        var act = () => client.GetEm1RecordsAsync(Ip, null, 0, CancellationToken.None);

        (await act.Should().ThrowAsync<ShellyRpcException>()).Which.HttpStatus.Should().Be(404);
    }

    [Fact]
    public async Task GetEm1Data_FollowsNextRecordTs()
    {
        var device = new FakeDevice
        {
            Answer = path => path.Contains("ts=1000&")
                ? Json("""{"keys":["total_act_energy"],"data":[{"ts":1000,"period":60,"values":[[1.5],[2.5]]}],"next_record_ts":1120}""")
                : Json("""{"keys":["total_act_energy"],"data":[{"ts":1120,"period":60,"values":[[3.0]]}]}"""),
        };
        using var client = new ShellyClient(handler: device);

        var rows = await client.GetEm1DataAsync(Ip, null, 1, 1000, 2000, CancellationToken.None);

        rows.Select(r => r.Ts).Should().Equal(1000, 1060, 1120);
        rows.Sum(r => r.ActiveWh).Should().Be(7.0);
        device.Requests.Select(r => r.RequestUri!.PathAndQuery).Should().Equal(
            "/rpc/EM1Data.GetData?id=1&ts=1000&end_ts=2000",
            "/rpc/EM1Data.GetData?id=1&ts=1120&end_ts=2000");
    }
    /// <summary>Device that answers EM1Data.GetData with <paramref name="perPage"/> one-minute rows (1 Wh each) per page.</summary>
    private static FakeDevice PagedDevice(int perPage, long recordsFrom, long recordsTo) => new()
    {
        Answer = path =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + path).Query);
            var ts = long.Parse(q["ts"]!);
            var end = Math.Min(long.Parse(q["end_ts"]!), recordsTo);
            var first = Math.Max(ts, recordsFrom);
            var values = new List<string>();
            var t = first;
            for (; t <= end && values.Count < perPage; t += 60) values.Add("[1.0,0.0]");
            var next = t <= end ? $",\"next_record_ts\":{t}" : "";
            return Json($"{{\"keys\":[\"total_act_energy\",\"total_act_ret_energy\"],\"data\":[{{\"ts\":{first},\"period\":60,\"values\":[{string.Join(",", values)}]}}]{next}}}");
        },
    };

    [Fact]
    public async Task GetEm1Data_FullDay_In28RowPages_IsComplete()
    {
        const long from = 1_793_484_000;            // a local midnight; 1440 one-minute records follow
        const long to = from + 86_399;
        var device = PagedDevice(28, from, to);
        using var client = new ShellyClient(handler: device);

        var rows = await client.GetEm1DataAsync(Ip, null, 0, from, to, CancellationToken.None);

        rows.Should().HaveCount(1440);
        rows.Sum(r => r.ActiveWh).Should().Be(1440);
        device.Requests.Should().HaveCount(52);      // ceil(1440 / 28)
        ShellyClient.MaxPagesFor(from, to).Should().Be(200);
    }

    [Fact]
    public async Task GetEm1Data_PageLimitReachedWithRecordsPending_Throws()
    {
        const long from = 1_793_484_000;
        const long to = from + 86_399;
        var device = PagedDevice(1, from, to);       // 1440 pages needed, cap 200
        using var client = new ShellyClient(handler: device);

        var act = () => client.GetEm1DataAsync(Ip, null, 0, from, to, CancellationToken.None);

        await act.Should().ThrowAsync<ShellyIncompleteDataException>();
        device.Requests.Should().HaveCount(200);
    }
}
