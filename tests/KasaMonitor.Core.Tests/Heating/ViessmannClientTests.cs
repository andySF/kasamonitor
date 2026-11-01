using System.Net;
using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Heating;

/// <summary>Fake HTTP endpoint: records the requests (with their bodies) and answers with a scripted function.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _answer;
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public FakeHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) => _answer = answer;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return _answer(request, body);
    }

    public static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class PkceTests
{
    [Fact]
    public void Challenge_OfTheRfc7636Example()
    {
        // RFC 7636, appendix B.
        Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk").Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void Verifier_And_State_AreRandomUrlSafeAndLongEnough()
    {
        var v1 = Pkce.NewVerifier();
        var v2 = Pkce.NewVerifier();
        v1.Should().HaveLength(64).And.MatchRegex("^[A-Za-z0-9_-]+$");
        v1.Should().NotBe(v2);
        Pkce.NewState().Should().MatchRegex("^[A-Za-z0-9_-]{43}$").And.NotBe(Pkce.NewState());
        Pkce.Challenge(v1).Should().HaveLength(43).And.NotContain("=");
    }
}

public class ApiCallBudgetTests
{
    private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TenMinuteWindow_120Calls_ThenWaitForTheOldestToLeave()
    {
        var budget = new ApiCallBudget(() => _now);
        var first = _now;
        for (var i = 0; i < ViessmannApi.TenMinuteCallLimit; i++)
        {
            budget.TryAcquire(out _).Should().BeTrue();
            _now = _now.AddSeconds(1);
        }
        budget.TryAcquire(out var retryAt).Should().BeFalse();
        retryAt.Should().Be(first.AddMinutes(10));

        _now = first.AddMinutes(10).AddSeconds(1);
        budget.TryAcquire(out _).Should().BeTrue();
        budget.CallsLast24h.Should().Be(121);
    }

    [Fact]
    public void DailyWindow_IsSliding24Hours()
    {
        var budget = new ApiCallBudget(() => _now, dailyLimit: 5, tenMinuteLimit: 100);
        var first = _now;
        for (var i = 0; i < 5; i++)
        {
            budget.TryAcquire(out _).Should().BeTrue();
            _now = _now.AddHours(1);
        }
        budget.TryAcquire(out var retryAt).Should().BeFalse();
        retryAt.Should().Be(first.AddHours(24));

        _now = first.AddHours(24).AddSeconds(1);
        budget.CallsLast24h.Should().Be(4);
        budget.TryAcquire(out _).Should().BeTrue();
    }

    [Fact]
    public void After429_NothingUntilTheResetTime()
    {
        var budget = new ApiCallBudget(() => _now);
        var reset = _now.AddHours(3);
        budget.Block(reset).Should().Be(reset);

        budget.TryAcquire(out var retryAt).Should().BeFalse();
        retryAt.Should().Be(reset);
        budget.BlockedUntil.Should().Be(reset);
        _now = reset.AddSeconds(1);
        budget.BlockedUntil.Should().BeNull();
        budget.TryAcquire(out _).Should().BeTrue();
    }

    [Fact]
    public void After429_WithoutResetTime_AtLeastTenMinutes()
    {
        var budget = new ApiCallBudget(() => _now);
        budget.Block(null).Should().Be(_now.AddMinutes(10));
        budget.Block(_now.AddMinutes(-5)).Should().Be(_now.AddMinutes(10), "a reset time in the past is ignored");
    }
}

public class ViessmannClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExchangeCode_PostsTheFormWithTheVerifier()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK,
            "{\"access_token\":\"AT-1\",\"refresh_token\":\"RT-1\",\"token_type\":\"Bearer\",\"expires_in\":3600}"));
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        var token = await client.ExchangeCodeAsync("client-x", "code-1", "verifier-1", "http://host:8910/centrala/callback", CancellationToken.None);

        token.Should().Be(new ViessmannToken("AT-1", "RT-1", Now.AddHours(1)));
        var (req, body) = handler.Requests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.ToString().Should().Be(ViessmannApi.TokenUrl);
        req.Content!.Headers.ContentType!.MediaType.Should().Be("application/x-www-form-urlencoded");
        body.Should().Contain("grant_type=authorization_code").And.Contain("client_id=client-x").And.Contain("code_verifier=verifier-1")
            .And.Contain("code=code-1").And.Contain("redirect_uri=http%3A%2F%2Fhost%3A8910%2Fcentrala%2Fcallback");
    }

    [Fact]
    public async Task Refresh_WithoutANewRefreshToken_KeepsTheOldOne()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"access_token\":\"AT-2\",\"expires_in\":1800}"));
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        var token = await client.RefreshAsync("client-x", "RT-old", CancellationToken.None);

        token.Should().Be(new ViessmannToken("AT-2", "RT-old", Now.AddMinutes(30)));
        handler.Requests.Single().Body.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=RT-old");
    }

    [Fact]
    public async Task RefusedRefresh_IsAnAuthError_WithoutTheToken()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.BadRequest,
            "{\"error\":\"invalid_grant\",\"error_description\":\"refresh token expired\"}"));
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        var act = () => client.RefreshAsync("client-x", "RT-secret-value", CancellationToken.None);

        (await act.Should().ThrowAsync<ViessmannAuthException>())
            .Which.Message.Should().Contain("invalid_grant").And.NotContain("RT-secret-value").And.NotContain("client-x");
    }

    [Theory]
    [InlineData(400, "invalid_grant", true)]
    [InlineData(401, "invalid_token", true)]
    [InlineData(403, "invalid_grant", true)]
    [InlineData(400, "invalid_client", false)]
    [InlineData(429, "invalid_grant", false)]
    [InlineData(503, null, false)]
    [InlineData(408, null, false)]
    public void OnlyARefusedGrantDisconnects(int status, string? error, bool refused) =>
        ViessmannClient.IsGrantRefused(status, error).Should().Be(refused);

    [Fact]
    public async Task TokenEndpoint429_IsTemporary_NotAnAuthError()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.TooManyRequests, "{\"error\":\"rate_limited\"}"));
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        var ex = (await ((Func<Task>)(() => client.RefreshAsync("c", "RT", CancellationToken.None))).Should().ThrowAsync<ViessmannApiException>()).Which;
        ex.StatusCode.Should().Be(429);
    }

    [Fact]
    public async Task Features_UseTheBearerToken_AndReturnTheRawJson()
    {
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, "{\"data\":[]}"));
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        (await client.GetFeaturesJsonAsync("AT-1", "1000001", "1111222233334444", CancellationToken.None)).Should().Be("{\"data\":[]}");

        var req = handler.Requests.Single().Request;
        req.RequestUri!.ToString().Should().Be(ViessmannApi.FeaturesUrl("1000001", "1111222233334444"));
        req.Headers.Authorization!.Scheme.Should().Be("Bearer");
        req.Headers.Authorization.Parameter.Should().Be("AT-1");
    }

    [Fact]
    public async Task RateLimit429_CarriesTheResetTime()
    {
        var reset = Now.AddHours(5);
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.TooManyRequests,
            "{\"viErrorId\":\"x\",\"statusCode\":429,\"errorType\":\"RATE_LIMIT_EXCEEDED\",\"message\":\"API calls rate limit has been exceeded.\"," +
            "\"extendedPayload\":{\"name\":\"ViCare day limit\",\"requestCountLimit\":1450,\"clientId\":\"client-x\",\"userId\":\"user-y\",\"limitReset\":" +
            reset.ToUnixTimeMilliseconds() + "}}"));
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        var act = () => client.GetFeaturesJsonAsync("AT-1", "1", "2", CancellationToken.None);

        var ex = (await act.Should().ThrowAsync<ViessmannRateLimitException>()).Which;
        ex.ResetUtc.Should().Be(reset);
        ex.LimitName.Should().Be("ViCare day limit");
        ex.Message.Should().NotContain("client-x").And.NotContain("user-y");
    }

    [Fact]
    public async Task RateLimit429_WithRetryAfterOnly()
    {
        var handler = new FakeHttpHandler((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") };
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(600));
            return r;
        });
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        var act = () => client.GetInstallationsJsonAsync("AT-1", CancellationToken.None);

        (await act.Should().ThrowAsync<ViessmannRateLimitException>()).Which.ResetUtc.Should().Be(Now.AddMinutes(10));
    }

    [Fact]
    public async Task Api401_IsAnAuthError_OtherErrorsAreApiErrors()
    {
        var code = HttpStatusCode.Unauthorized;
        var handler = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(code, "{\"errorType\":\"DEVICE_COMMUNICATION_ERROR\",\"message\":\"gateway offline\"}"));
        var client = new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => Now);

        await ((Func<Task>)(() => client.GetInstallationsJsonAsync("AT", CancellationToken.None))).Should().ThrowAsync<ViessmannAuthException>();
        code = HttpStatusCode.BadGateway;
        var ex = (await ((Func<Task>)(() => client.GetInstallationsJsonAsync("AT", CancellationToken.None))).Should().ThrowAsync<ViessmannApiException>()).Which;
        ex.StatusCode.Should().Be(502);
        ex.ErrorType.Should().Be("DEVICE_COMMUNICATION_ERROR");
        ex.Message.Should().Contain("gateway offline");
    }
}
