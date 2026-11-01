using System.Globalization;
using System.Net;
using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>Scripted Viessmann client: counts the calls, can throw, records the code exchange.</summary>
internal sealed class FakeViessmannClient : IViessmannClient
{
    public string InstallationsJson { get; set; } = FakeShellyClient.Fixture("vicare_installations.json");
    public Func<string> FeaturesJson { get; set; } = () => FakeShellyClient.Fixture("vicare_b0ka_features.json");
    public Queue<Exception> FeatureErrors { get; } = new();
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    public int InstallationCalls, FeatureCalls, ExchangeCalls, RefreshCalls;
    public string? LastVerifier, LastRedirectUri, LastFeatureToken;

    public Task<ViessmannToken> ExchangeCodeAsync(string clientId, string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        ExchangeCalls++;
        LastVerifier = codeVerifier;
        LastRedirectUri = redirectUri;
        return Task.FromResult(new ViessmannToken("AT-from-code", "RT-from-code", Clock().AddHours(1)));
    }

    public Task<ViessmannToken> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
    {
        RefreshCalls++;
        return Task.FromResult(new ViessmannToken("AT-refreshed-" + RefreshCalls, refreshToken, Clock().AddHours(1)));
    }

    public Task<string> GetInstallationsJsonAsync(string accessToken, CancellationToken ct)
    {
        InstallationCalls++;
        return Task.FromResult(InstallationsJson);
    }

    public Task<string> GetFeaturesJsonAsync(string accessToken, string installationId, string gatewaySerial, CancellationToken ct)
    {
        FeatureCalls++;
        LastFeatureToken = accessToken;
        if (FeatureErrors.TryDequeue(out var ex)) throw ex;
        return Task.FromResult(FeaturesJson());
    }
}

/// <summary>Records log lines (level + formatted text).</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Text)> Messages { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (Messages) Messages.Add((logLevel, formatter(state, exception)));
    }
}

internal sealed class FakeTokenHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _answer;
    public List<string?> Bodies { get; } = new();
    public FakeTokenHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> answer) => _answer = answer;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Bodies.Add(body);
        return _answer(request, body);
    }
}

public sealed class HeatingTests : IDisposable
{
    // The zone of the QNAP (TZ=Europe/Bucharest), DST included.
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    private readonly TestDbFactory _db = new();
    private readonly TestScopeFactory _scopes;
    private readonly HeatingSettingsService _settings;
    private DateTimeOffset _now = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);

    public HeatingTests()
    {
        _scopes = new TestScopeFactory(_db.CreateContext);
        _settings = new HeatingSettingsService(_scopes, new HeatingSettingsDefaults(), new FakeSecretProtector());
    }

    public void Dispose() => _db.Dispose();

    private async Task ConnectAsync(DateTimeOffset? expires = null)
    {
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "client-test", 120, 0.31m, "m3", 10.55), CancellationToken.None);
        await _settings.SaveTokenAsync(new ViessmannToken("AT-stored", "RT-stored", expires ?? _now.AddHours(1)), CancellationToken.None);
    }

    private (HeatingPollWorker Worker, HeatingState State, FakeViessmannClient Client, ViessmannAuthService Auth) CreateWorker(
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        var client = new FakeViessmannClient { Clock = () => _now };
        var state = new HeatingState(new ApiCallBudget(() => _now), new DailyCallCounter(() => _now, Zone));
        var auth = new ViessmannAuthService(_settings, client, NullLogger<ViessmannAuthService>.Instance, () => _now, state.Calls);
        var worker = new HeatingPollWorker(_scopes, _settings, auth, client, state, NullLogger<HeatingPollWorker>.Instance, () => _now, Zone, wait);
        return (worker, state, client, auth);
    }

    private async Task<Dictionary<string, string>> RawSettingsAsync()
    {
        using var scope = _scopes.CreateScope();
        return new Dictionary<string, string>(await ((ISettingsRepository)scope.ServiceProvider.GetService(typeof(ISettingsRepository))!).GetAllAsync(CancellationToken.None));
    }

    private HeatingRepository Repo() => new(_db.CreateContext());

    /// <summary>A small features answer with today's gas per purpose (m³) and the feature timestamp.</summary>
    private static string GasJson(double heatingDay, double dhwDay, DateTimeOffset ts)
    {
        string F(string purpose, double day) => "{\"feature\":\"heating.gas.consumption.summary." + purpose + "\",\"deviceId\":\"0\",\"timestamp\":\""
            + ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "\",\"properties\":{\"currentDay\":{\"type\":\"number\",\"value\":"
            + day.ToString(CultureInfo.InvariantCulture) + ",\"unit\":\"cubicMeter\"},\"currentMonth\":{\"type\":\"number\",\"value\":5,\"unit\":\"cubicMeter\"}}}";
        return "{\"data\":[" + F("heating", heatingDay) + "," + F("dhw", dhwDay)
            + ",{\"feature\":\"heating.burners.0\",\"deviceId\":\"0\",\"properties\":{\"active\":{\"type\":\"boolean\",\"value\":true}}}]}";
    }

    // ---- settings ----

    [Fact]
    public async Task Settings_TokensAreStoredEncrypted_IntervalAtLeast60()
    {
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "client-test", 10, 0.31m, "m3", 10.55), CancellationToken.None);
        await _settings.SaveTokenAsync(new ViessmannToken("AT-plain", "RT-plain", _now.AddHours(1)), CancellationToken.None);

        var raw = await RawSettingsAsync();
        raw[HeatingSettingKeys.ViessmannAccessToken].Should().StartWith("enc:").And.NotContain("AT-plain");
        raw[HeatingSettingKeys.ViessmannRefreshToken].Should().StartWith("enc:").And.NotContain("RT-plain");

        var s = await _settings.GetAsync(CancellationToken.None);
        s.PollIntervalSeconds.Should().Be(60);
        s.CallsPerDay.Should().Be(1440);
        s.Token.Should().Be(new ViessmannToken("AT-plain", "RT-plain", _now.AddHours(1)));
        s.IsConnected.Should().BeTrue();
        s.GasKwhPerM3.Should().Be(10.55);
        s.GasUnit.Should().Be(EnergyUnit.CubicMeter);
    }

    [Fact]
    public async Task Settings_Defaults_And_InvalidFactorRefused()
    {
        var s = await _settings.GetAsync(CancellationToken.None);
        s.PollIntervalSeconds.Should().Be(120);
        s.HasClientId.Should().BeFalse();
        s.Token.Should().BeNull();

        var act = () => _settings.SaveAsync(new HeatingSettingsUpdate(true, "c", 120, 0.31m, "m3", 0.5), CancellationToken.None);
        await act.Should().ThrowAsync<FormatException>();
    }

    [Fact]
    public async Task Settings_NewClientId_DisconnectsTheAccount()
    {
        await ConnectAsync();
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "another-client", 120, 0.31m, "m3", 10.55), CancellationToken.None);

        var s = await _settings.GetAsync(CancellationToken.None);
        s.Token.Should().BeNull();
        s.AuthError.Should().Contain("Client ID");
    }

    // ---- OAuth ----

    [Fact]
    public async Task Authorize_BuildsThePkceUrl_AndTheCallbackExchangesTheCodeWithTheVerifier()
    {
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "client-test", 120, 0.31m, "m3", 10.55), CancellationToken.None);
        var (_, _, client, auth) = CreateWorker();
        var connected = 0;
        auth.Connected += () => connected++;

        var start = await auth.BeginAuthorizeAsync("http://192.168.1.10:8910/centrala/callback", CancellationToken.None);
        var url = start!.Value.Url;

        url.Should().StartWith(ViessmannApi.AuthorizeUrl + "?");
        var q = new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        q["response_type"].Should().Be("code");
        q["client_id"].Should().Be("client-test");
        q["redirect_uri"].Should().Be("http://192.168.1.10:8910/centrala/callback");
        q["scope"].Should().Be("IoT User offline_access");
        q["code_challenge_method"].Should().Be("S256");
        q["state"].Should().Be(start.Value.State);

        var (result, _) = await auth.CompleteAsync(q["state"], "the-code", null, CancellationToken.None);

        result.Should().Be(ViessmannCallbackResult.Connected);
        client.ExchangeCalls.Should().Be(1);
        Pkce.Challenge(client.LastVerifier!).Should().Be(q["code_challenge"]);
        client.LastRedirectUri.Should().Be("http://192.168.1.10:8910/centrala/callback");
        (await _settings.GetAsync(CancellationToken.None)).Token!.AccessToken.Should().Be("AT-from-code");
        connected.Should().Be(1);
    }

    [Fact]
    public async Task Callback_WithAWrongReusedOrExpiredState_IsRejectedWithoutAnyCall()
    {
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "client-test", 120, 0.31m, "m3", 10.55), CancellationToken.None);
        var (_, _, client, auth) = CreateWorker();
        var state = (await auth.BeginAuthorizeAsync("http://h/centrala/callback", CancellationToken.None))!.Value.State;

        (await auth.CompleteAsync("forged-state", "code", null, CancellationToken.None)).Result.Should().Be(ViessmannCallbackResult.InvalidState);
        (await auth.CompleteAsync(null, "code", null, CancellationToken.None)).Result.Should().Be(ViessmannCallbackResult.InvalidState);
        client.ExchangeCalls.Should().Be(0);

        (await auth.CompleteAsync(state, "code", null, CancellationToken.None)).Result.Should().Be(ViessmannCallbackResult.Connected);
        (await auth.CompleteAsync(state, "code", null, CancellationToken.None)).Result.Should().Be(ViessmannCallbackResult.InvalidState, "a state is single use");

        var state2 = (await auth.BeginAuthorizeAsync("http://h/centrala/callback", CancellationToken.None))!.Value.State;
        _now = _now.AddMinutes(11);
        (await auth.CompleteAsync(state2, "code", null, CancellationToken.None)).Result
            .Should().Be(ViessmannCallbackResult.InvalidState, "a state expires after 10 minutes");
        client.ExchangeCalls.Should().Be(1);
    }

    [Fact]
    public async Task Callback_WithAnError_IsDenied()
    {
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "client-test", 120, 0.31m, "m3", 10.55), CancellationToken.None);
        var (_, _, client, auth) = CreateWorker();
        var state = (await auth.BeginAuthorizeAsync("http://h/centrala/callback", CancellationToken.None))!.Value.State;

        var (result, detail) = await auth.CompleteAsync(state, null, "access_denied", CancellationToken.None);

        result.Should().Be(ViessmannCallbackResult.Denied);
        detail.Should().Be("access_denied");
        client.ExchangeCalls.Should().Be(0);
    }

    [Fact]
    public async Task AccessToken_IsRefreshedFiveMinutesBeforeExpiry_ThroughTheTokenEndpoint()
    {
        await ConnectAsync(expires: _now.AddMinutes(10));
        var handler = new FakeTokenHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"access_token\":\"AT-new\",\"refresh_token\":\"RT-new\",\"expires_in\":3600}", Encoding.UTF8, "application/json"),
        });
        var auth = new ViessmannAuthService(_settings, new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => _now),
            NullLogger<ViessmannAuthService>.Instance, () => _now);

        (await auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("AT-stored");
        handler.Bodies.Should().BeEmpty("10 minutes left: no refresh yet");

        _now = _now.AddMinutes(6);   // 4 minutes left
        (await auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("AT-new");
        handler.Bodies.Should().ContainSingle().Which.Should().Contain("grant_type=refresh_token").And.Contain("refresh_token=RT-stored")
            .And.Contain("client_id=client-test");

        var s = await _settings.GetAsync(CancellationToken.None);
        s.Token.Should().Be(new ViessmannToken("AT-new", "RT-new", _now.AddHours(1)));
        (await auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("AT-new");
        handler.Bodies.Should().HaveCount(1);
    }

    [Fact]
    public async Task RefusedRefresh_DisconnectsTheAccount()
    {
        await ConnectAsync(expires: _now.AddMinutes(1));
        var handler = new FakeTokenHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json"),
        });
        var auth = new ViessmannAuthService(_settings, new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => _now),
            NullLogger<ViessmannAuthService>.Instance, () => _now);

        var act = () => auth.GetAccessTokenAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<HeatingDisconnectedException>()).Which.Message.Should().Contain("reconectează");
        var s = await _settings.GetAsync(CancellationToken.None);
        s.Token.Should().BeNull();
        s.AuthError.Should().Contain("invalid_grant");
        (await RawSettingsAsync())[HeatingSettingKeys.ViessmannRefreshToken].Should().BeEmpty();
    }

    // ---- worker ----

    [Fact]
    public async Task Worker_NotConnected_MakesNoCall()
    {
        var (worker, state, client, _) = CreateWorker();
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "client-test", 120, 0.31m, "m3", 10.55), CancellationToken.None);

        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.NotCalled);

        client.InstallationCalls.Should().Be(0);
        client.FeatureCalls.Should().Be(0);
        state.Status.Should().Contain("nu e conectat");
    }

    [Fact]
    public async Task Worker_DiscoversThenReads_AndStoresReadingDayPeriodsAndRawDump()
    {
        await ConnectAsync();
        var (worker, state, client, _) = CreateWorker();

        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);

        client.InstallationCalls.Should().Be(1);
        client.FeatureCalls.Should().Be(1);
        client.LastFeatureToken.Should().Be("AT-stored");
        state.Budget.CallsLast24h.Should().Be(2);
        state.Calls.Today.Should().Be(2, "installations + features, no token call");

        var repo = Repo();
        var device = (await repo.ListDevicesAsync(CancellationToken.None)).Single();
        device.Should().Match<HeatingDevice>(d => d.Provider == "viessmann" && d.InstallationId == "1000001" && d.GatewaySerial == "1111222233334444"
            && d.DeviceId == "0" && d.ModelId == "E3_Vitodens_050_0822" && d.Name == "Vitodens 050" && d.LastSeenUtc == _now && d.LastError == null);

        var reading = (await repo.ListReadingsAsync(device.Id, _now.AddMinutes(-1), _now, CancellationToken.None)).Single();
        reading.SupplyTempC.Should().Be(36.2);
        reading.DhwTempC.Should().Be(34.1);
        reading.BurnerStarts.Should().Be(31089);
        reading.PressureBar.Should().BeNull();
        reading.StatusCode.Should().Be("S.6");

        var day = (await repo.ListDailyAsync(device.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), CancellationToken.None)).Single();
        day.Day.Should().Be(new DateOnly(2026, 11, 1));
        day.GasHeatingMaxM3.Should().Be(1.3);
        day.GasHeatingLastM3.Should().Be(1.3);
        day.GasDhwMaxM3.Should().Be(0.1);
        (await repo.ListPeriodsAsync(device.Id, CancellationToken.None)).Should().HaveCount(36);
        (await repo.GetRawAsync(device.Id, CancellationToken.None))!.Value.Json.Should().Contain("heating.gas.consumption.summary.dhw");

        state.Current!.Snapshot!.SupplyTempC.Should().Be(36.2);
        state.Current.Totals!.GasHeatingM3.Should().Be(1.3);
        state.Current.Periods.Should().HaveCount(36);
        state.IsOnline(_now).Should().BeTrue();
        state.Status.Should().BeNull();

        // Second poll: no new discovery.
        _now = _now.AddMinutes(2);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);
        client.InstallationCalls.Should().Be(1);
        client.FeatureCalls.Should().Be(2);
    }

    [Fact]
    public async Task Worker_DayRollover_ByOurLocalTime_ResetAfterMidnightIsLoggedAndTodayUsesTheLastValue()
    {
        await ConnectAsync(expires: _now.AddDays(3));
        var (worker, state, client, _) = CreateWorker();
        var day1 = new DateTimeOffset(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);   // 22:00 local (EET)
        _now = day1;
        client.FeaturesJson = () => GasJson(4.0, 0.5, day1);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);

        // 23:50 local: the day's last value.
        _now = day1.AddMinutes(110);
        client.FeaturesJson = () => GasJson(4.6, 0.7, day1.AddMinutes(110));
        await worker.PollOnceAsync(CancellationToken.None);

        // 00:02 local: the boiler still reports yesterday's total; it lands on the new day (our reading's time).
        _now = day1.AddMinutes(122);
        client.FeaturesJson = () => GasJson(4.8, 0.7, day1.AddMinutes(122));
        await worker.PollOnceAsync(CancellationToken.None);

        // 06:00 local: the boiler resets its counter late; the drop is logged and today counts with the last value.
        _now = day1.AddHours(10);
        client.FeaturesJson = () => GasJson(0.1, 0.0, day1.AddHours(10));
        await worker.PollOnceAsync(CancellationToken.None);

        var repo = Repo();
        var id = state.Current!.Device.Id;
        var days = await repo.ListDailyAsync(id, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), CancellationToken.None);
        days.Select(d => (d.Day, d.GasHeatingMaxM3, d.GasHeatingLastM3, d.GasDhwMaxM3, d.GasDhwLastM3)).Should().Equal(
            (new DateOnly(2026, 11, 1), (double?)4.6, (double?)4.6, (double?)0.7, (double?)0.7),
            (new DateOnly(2026, 11, 2), (double?)4.8, (double?)0.1, (double?)0.7, (double?)0.0));
        // Closed day 1 Nov: highest (4.6); today 2 Nov: last value (0.1).
        state.Current.Totals!.GasHeatingM3.Should().BeApproximately(4.7, 1e-9);
        state.Current.Totals.GasDhwM3.Should().BeApproximately(0.7, 1e-9);
    }

    [Fact]
    public async Task Worker_LogsAGasDecreaseInTheSameLocalDay()
    {
        await ConnectAsync(expires: _now.AddDays(1));
        var log = new ListLogger<HeatingPollWorker>();
        var client = new FakeViessmannClient { Clock = () => _now };
        var state = new HeatingState(new ApiCallBudget(() => _now), new DailyCallCounter(() => _now, Zone));
        var auth = new ViessmannAuthService(_settings, client, NullLogger<ViessmannAuthService>.Instance, () => _now, state.Calls);
        var worker = new HeatingPollWorker(_scopes, _settings, auth, client, state, log, () => _now, Zone);
        client.FeaturesJson = () => GasJson(2.0, 0.3, _now);
        await worker.PollOnceAsync(CancellationToken.None);
        _now = _now.AddMinutes(2);
        client.FeaturesJson = () => GasJson(1.5, 0.3, _now);
        await worker.PollOnceAsync(CancellationToken.None);

        log.Messages.Should().ContainSingle(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Information && m.Text.Contains("received 1.5 m3 < stored highest 2 m3"))
            .Which.Text.Should().Contain("heating").And.Contain("2026-11-01");
    }

    [Fact]
    public void NextDelay_IntervalAfterRead_DoublingBackoffAfterFailures_Capped()
    {
        var now = _now;
        HeatingPollWorker.NextDelay(HeatingPollResult.Read, 0, 120, null, now).Should().Be(TimeSpan.FromSeconds(120));
        Enumerable.Range(1, 6).Select(n => HeatingPollWorker.NextDelay(HeatingPollResult.CallFailed, n, 120, null, now).TotalSeconds)
            .Should().Equal(120, 240, 480, 960, 1800, 1800);
        HeatingPollWorker.NextDelay(HeatingPollResult.CallFailed, 3, 3600, null, now).Should().Be(TimeSpan.FromHours(1), "never below the interval");
        HeatingPollWorker.NextDelay(HeatingPollResult.RateLimited, 0, 120, now.AddSeconds(30), now).Should().Be(TimeSpan.FromSeconds(120));
        HeatingPollWorker.NextDelay(HeatingPollResult.RateLimited, 0, 120, now.AddHours(2), now).Should().Be(TimeSpan.FromHours(2));
        HeatingPollWorker.NextDelay(HeatingPollResult.NotCalled, 0, 120, null, now).Should().Be(HeatingPollWorker.IdleDelay);
    }

    [Fact]
    public async Task Loop_PersistentServerError_OneCallPerBackoffStep_NeverFasterThanTheInterval()
    {
        await ConnectAsync(expires: _now.AddDays(2));
        var delays = new List<TimeSpan>();
        var done = new TaskCompletionSource();
        FakeViessmannClient? fake = null;
        var (worker, _, client, _) = CreateWorker(wait: (d, _) =>
        {
            delays.Add(d);
            _now += d;
            // After the first successful read every features call fails with 502 (gateway offline).
            if (delays.Count == 1)
                for (var i = 0; i < 20; i++) fake!.FeatureErrors.Enqueue(new ViessmannApiException(502, "DEVICE_COMMUNICATION_ERROR", "gateway offline"));
            if (delays.Count == 7)
            {
                done.TrySetResult();
                throw new OperationCanceledException();   // ends the loop
            }
            return Task.CompletedTask;
        });
        fake = client;

        await worker.StartAsync(CancellationToken.None);
        (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(20)))).Should().Be(done.Task);
        await worker.StopAsync(CancellationToken.None);

        delays.Should().HaveCount(7);
        delays.Should().OnlyContain(d => d >= TimeSpan.FromSeconds(120), "never a retry faster than the interval");
        delays.Select(d => d.TotalSeconds).Should().Equal(120, 120, 240, 480, 960, 1800, 1800);
        client.FeatureCalls.Should().Be(7, "one call per cycle: 1 read + 6 failures, no extra retries");
    }

    [Fact]
    public async Task Calls_CountTokenAndApiRequestsOnlyWhenTheyLeave()
    {
        await ConnectAsync(expires: _now.AddMinutes(2));   // expires within 5 minutes: the first call refreshes
        var (worker, state, client, _) = CreateWorker();

        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);

        client.RefreshCalls.Should().Be(1);
        state.Calls.Today.Should().Be(3, "1 refresh + installations + features");
        state.Budget.CallsLast24h.Should().Be(2, "the budget counts API calls only");

        // Disconnected: nothing is called and nothing counted.
        await _settings.DisconnectAsync("test", CancellationToken.None);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.NotCalled);
        state.Calls.Today.Should().Be(3);
    }

    [Fact]
    public async Task RefreshServerError_IsTemporary_TokensKept()
    {
        await ConnectAsync(expires: _now.AddMinutes(1));
        var handler = new FakeTokenHandler((_, _) => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("") });
        var auth = new ViessmannAuthService(_settings, new ViessmannClient(TimeSpan.FromSeconds(5), handler, () => _now),
            NullLogger<ViessmannAuthService>.Instance, () => _now);

        await ((Func<Task>)(() => auth.GetAccessTokenAsync(CancellationToken.None))).Should().ThrowAsync<ViessmannApiException>();
        (await _settings.GetAsync(CancellationToken.None)).Token!.RefreshToken.Should().Be("RT-stored", "a 5xx does not disconnect");

        var handler429 = new FakeTokenHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"error\":\"too_many\"}") });
        var auth429 = new ViessmannAuthService(_settings, new ViessmannClient(TimeSpan.FromSeconds(5), handler429, () => _now),
            NullLogger<ViessmannAuthService>.Instance, () => _now);
        await ((Func<Task>)(() => auth429.GetAccessTokenAsync(CancellationToken.None))).Should().ThrowAsync<ViessmannApiException>();
        (await _settings.GetAsync(CancellationToken.None)).Token.Should().NotBeNull();
    }

    [Fact]
    public async Task OAuthState_IsTiedToTheBrowserCookie_AndThePendingListIsCapped()
    {
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "client-test", 120, 0.31m, "m3", 10.55), CancellationToken.None);
        var (_, _, _, auth) = CreateWorker();
        var state = (await auth.BeginAuthorizeAsync("http://h/centrala/callback", CancellationToken.None))!.Value.State;
        var cookie = ViessmannAuthService.StateCookieValue(state);

        cookie.Should().NotContain(state).And.MatchRegex("^[0-9A-F]{64}$");
        ViessmannAuthService.CookieMatches(state, cookie).Should().BeTrue();
        ViessmannAuthService.CookieMatches(state, cookie.ToLowerInvariant()).Should().BeTrue();
        ViessmannAuthService.CookieMatches(state, null).Should().BeFalse();
        ViessmannAuthService.CookieMatches(state, ViessmannAuthService.StateCookieValue("other")).Should().BeFalse();
        ViessmannAuthService.CookieMatches(null, cookie).Should().BeFalse();

        for (var i = 0; i < 25; i++) await auth.BeginAuthorizeAsync("http://h/centrala/callback", CancellationToken.None);
        auth.PendingCount.Should().Be(ViessmannAuthService.MaxPending);
    }

    [Fact]
    public async Task Worker_429_PausesUntilTheResetTime()
    {
        await ConnectAsync(expires: _now.AddDays(1));
        var (worker, state, client, _) = CreateWorker();
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);
        var reset = _now.AddHours(2);
        client.FeatureErrors.Enqueue(new ViessmannRateLimitException(reset, "ViCare day limit"));

        _now = _now.AddMinutes(2);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.RateLimited);
        state.Budget.BlockedUntil.Should().Be(reset);
        state.Status.Should().Contain("Limita de apeluri");
        state.Current!.LastPollOk.Should().BeFalse();
        var calls = client.FeatureCalls;

        _now = _now.AddMinutes(30);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.NotCalled);
        client.FeatureCalls.Should().Be(calls, "no call while paused");

        _now = reset.AddSeconds(1);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);
        client.FeatureCalls.Should().Be(calls + 1);
    }

    [Fact]
    public async Task Worker_Api401_RefreshesOnceAndRetries()
    {
        await ConnectAsync();
        var (worker, _, client, _) = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);
        client.FeatureErrors.Enqueue(new ViessmannAuthException("API HTTP 401"));

        _now = _now.AddMinutes(2);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);

        client.RefreshCalls.Should().Be(1);
        client.LastFeatureToken.Should().Be("AT-refreshed-1");
    }

    [Fact]
    public async Task Worker_AfterRestart_ShowsTheStoredDumpBeforeTheFirstPoll()
    {
        await ConnectAsync();
        var (worker, _, _, _) = CreateWorker();
        await worker.PollOnceAsync(CancellationToken.None);

        var (restarted, state, client, _) = CreateWorker();
        await restarted.LoadStoredAsync(CancellationToken.None);

        state.Current!.Snapshot!.DhwTempC.Should().Be(34.1);
        state.Current.LastPollOk.Should().BeFalse();
        state.Current.Periods.Should().HaveCount(36);
        client.FeatureCalls.Should().Be(0);
    }
}
