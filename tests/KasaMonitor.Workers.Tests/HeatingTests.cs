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

    /// <summary>A Romanian local time (the app's zone, EET/EEST) as UTC.</summary>
    private static DateTimeOffset Ro(int y, int mo, int d, int h, int mi)
    {
        var local = new DateTime(y, mo, d, h, mi, 0);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }

    private (HeatingPollWorker Worker, HeatingState State, FakeViessmannClient Client, ListLogger<HeatingPollWorker> Log) CreateLoggedWorker()
    {
        var log = new ListLogger<HeatingPollWorker>();
        var client = new FakeViessmannClient { Clock = () => _now };
        var state = new HeatingState(new ApiCallBudget(() => _now), new DailyCallCounter(() => _now, Zone));
        var auth = new ViessmannAuthService(_settings, client, NullLogger<ViessmannAuthService>.Instance, () => _now, state.Calls);
        return (new HeatingPollWorker(_scopes, _settings, auth, client, state, log, () => _now, Zone), state, client, log);
    }

    private async Task ReadAtAsync(HeatingPollWorker worker, FakeViessmannClient client, DateTimeOffset at, double heating, double dhw)
    {
        _now = at;
        client.FeaturesJson = () => GasJson(heating, dhw, at);
        (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);
    }

    private async Task<List<(DateOnly Day, double? HMax, double? HLast, double? DMax, double? DLast)>> DaysAsync(Guid id) =>
        (await Repo().ListDailyAsync(id, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), CancellationToken.None))
        .Select(d => (d.Day, d.GasHeatingMaxM3, d.GasHeatingLastM3, d.GasDhwMaxM3, d.GasDhwLastM3)).ToList();

    [Fact]
    public async Task Worker_PublishesTheBurnerPurpose_OfTheCurrentRun()
    {
        await ConnectAsync(expires: _now.AddDays(1));
        var (worker, state, client, _) = CreateLoggedWorker();
        async Task<BurnerPurpose?> ReadAsync(int minute, bool on, double heating, double dhw)
        {
            var at = new DateTimeOffset(2026, 11, 2, 10, minute, 0, TimeSpan.Zero);
            _now = at;
            client.FeaturesJson = () => GasJson(heating, dhw, at).Replace("\"value\":true", "\"value\":" + (on ? "true" : "false"));
            (await worker.PollOnceAsync(CancellationToken.None)).Should().Be(HeatingPollResult.Read);
            return state.Current!.BurnerPurpose;
        }

        (await ReadAsync(0, false, 1.0, 0.1)).Should().Be(BurnerPurpose.Off);
        (await ReadAsync(2, true, 1.0, 0.1)).Should().Be(BurnerPurpose.Unknown, "no counter and no temperature moved yet");
        (await ReadAsync(4, true, 1.0, 0.2)).Should().Be(BurnerPurpose.Dhw, "the hot water counter grew during the run");
        (await ReadAsync(6, false, 1.0, 0.2)).Should().Be(BurnerPurpose.Off);
    }

    [Fact]
    public async Task Worker_BoilerDay_ReadingsBefore01RomanianTimeStayOnTheBoilersPreviousDay()
    {
        // The measured night of 1→2 Nov 2026: the boiler (CET) resets at 01:00 Romanian time.
        await ConnectAsync(expires: _now.AddDays(3));
        var (worker, state, client, log) = CreateLoggedWorker();
        (await _settings.GetAsync(CancellationToken.None)).BoilerTimeZoneId.Should().Be("Europe/Berlin");

        await ReadAtAsync(worker, client, Ro(2026, 11, 1, 23, 50), 1.3, 0.1);
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 0, 30), 1.4, 0.1);   // 23:30 CET: the boiler's 1 Nov
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 4), 0.0, 0.0);    // 00:04 CET: the boiler's new day
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 3, 0), 0.7, 0.0);

        var id = state.Current!.Device.Id;
        (await DaysAsync(id)).Should().Equal(
            (new DateOnly(2026, 11, 1), (double?)1.4, (double?)1.4, (double?)0.1, (double?)0.1),
            (new DateOnly(2026, 11, 2), (double?)0.7, (double?)0.7, (double?)0.0, (double?)0.0));
        state.Current.Totals!.GasHeatingM3.Should().BeApproximately(2.1, 1e-9);
        state.Current.Totals.GasDhwM3.Should().BeApproximately(0.1, 1e-9);
        state.BoilerTimeZone.Id.Should().Be("Europe/Berlin");
        log.Messages.Should().NotContain(m => m.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task Worker_RealNight_StaleValueAfterTheBoilerMidnightStaysOnYesterday_TotalNeverJumps()
    {
        // 00:58 EET 1.4 → 01:01 EET still 1.4 (stale) → 01:04 EET 0 (the reset) → 01:30 EET 0.1.
        await ConnectAsync(expires: _now.AddDays(3));
        var (worker, state, client, log) = CreateLoggedWorker();
        var totals = new List<double>();

        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 0, 58), 1.4, 0.1);   // 23:58 CET 1 Nov
        totals.Add(state.Current!.Totals!.GasHeatingM3);
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 1), 1.4, 0.1);    // 00:01 CET: yesterday's total, stale
        totals.Add(state.Current!.Totals!.GasHeatingM3);
        var id = state.Current.Device.Id;
        (await DaysAsync(id)).Should().ContainSingle("the new day gets nothing from a stale value").Which.Day.Should().Be(new DateOnly(2026, 11, 1));
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 4), 0.0, 0.0);    // 00:04 CET: the reset
        totals.Add(state.Current!.Totals!.GasHeatingM3);
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 30), 0.1, 0.0);
        totals.Add(state.Current!.Totals!.GasHeatingM3);

        totals.Should().Equal(1.4, 1.4, 1.4, 1.5);
        (await DaysAsync(id)).Should().Equal(
            (new DateOnly(2026, 11, 1), (double?)1.4, (double?)1.4, (double?)0.1, (double?)0.1),
            (new DateOnly(2026, 11, 2), (double?)0.1, (double?)0.1, (double?)0.0, (double?)0.0));
        state.Current.Totals!.GasDhwM3.Should().BeApproximately(0.1, 1e-9);
        log.Messages.Should().ContainSingle(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Information && m.Text.Contains("is still 2026-11-01's total"));
        log.Messages.Should().NotContain(m => m.Level >= Microsoft.Extensions.Logging.LogLevel.Warning);
    }

    [Fact]
    public async Task Worker_StaleValueRaisesYesterday()
    {
        await ConnectAsync(expires: _now.AddDays(3));
        var (worker, state, client, _) = CreateLoggedWorker();

        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 0, 50), 1.3, 0.1);   // 23:50 CET
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 2), 1.4, 0.1);    // 00:02 CET: the last minutes of yesterday
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 6), 0.0, 0.0);

        (await DaysAsync(state.Current!.Device.Id)).Should().Equal(
            (new DateOnly(2026, 11, 1), (double?)1.4, (double?)1.4, (double?)0.1, (double?)0.1),
            (new DateOnly(2026, 11, 2), (double?)0.0, (double?)0.0, (double?)0.0, (double?)0.0));
    }

    [Fact]
    public async Task Worker_FirstNightWithoutYesterday_TheResetInTheWindowCarriesTheValueBack()
    {
        // Nothing stored for 1 Nov: yesterday's total read at 00:01 CET lands on 2 Nov; the reset at 00:04 CET is accepted
        // and carries it back to 1 Nov.
        await ConnectAsync(expires: _now.AddDays(3));
        var (worker, state, client, log) = CreateLoggedWorker();

        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 1), 1.4, 0.1);
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 4), 0.0, 0.0);

        (await DaysAsync(state.Current!.Device.Id)).Should().Equal(
            (new DateOnly(2026, 11, 1), (double?)1.4, (double?)1.4, (double?)0.1, (double?)0.1),
            (new DateOnly(2026, 11, 2), (double?)0.0, (double?)0.0, (double?)0.0, (double?)0.0));
        log.Messages.Should().Contain(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Information && m.Text.Contains("boiler reset accepted")
            && m.Text.Contains("2026-11-02 00:04:00 boiler time (2026-11-02 01:04:00 local)"));
    }

    [Fact]
    public async Task Worker_DropAt1400_LeavesTheDay_WarningOncePerDay()
    {
        await ConnectAsync(expires: _now.AddDays(3));
        var (worker, state, client, log) = CreateLoggedWorker();

        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 13, 58), 2.0, 0.3);
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 14, 0), 1.5, 0.3);
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 14, 2), 1.4, 0.3);

        var day = (await DaysAsync(state.Current!.Device.Id)).Single();
        (day.HMax, day.HLast).Should().Be(((double?)2.0, (double?)2.0));
        state.Current.Totals!.GasHeatingM3.Should().BeApproximately(2.0, 1e-9, "the total never goes down because of a drop");
        log.Messages.Where(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Should().ContainSingle()
            .Which.Text.Should().Contain("received 1.5 m3 < stored highest 2 m3").And.Contain("heating").And.Contain("not the daily reset");
    }

    [Fact]
    public async Task Worker_ResetUnderTwentyPercentDuringTheDay_DoesNotRewriteTheDay_TheCounterKeepsGoing()
    {
        await ConnectAsync(expires: _now.AddDays(3));
        var (worker, state, client, log) = CreateLoggedWorker();
        var totals = new List<double>();

        foreach (var (h, v) in new[] { (13, 2.0), (14, 0.1), (16, 1.0), (18, 2.3) })
        {
            await ReadAtAsync(worker, client, Ro(2026, 11, 2, h, 0), v, 0.3);
            totals.Add(state.Current!.Totals!.GasHeatingM3);
        }

        totals.Should().Equal(2.0, 2.0, 2.0, 2.3);
        var day = (await DaysAsync(state.Current!.Device.Id)).Single();
        (day.HMax, day.HLast).Should().Be(((double?)2.3, (double?)2.3));
        log.Messages.Should().NotContain(m => m.Text.Contains("boiler reset accepted"));
        log.Messages.Where(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Warning).Should().ContainSingle().Which.Text.Should().Contain("not the daily reset");
    }

    [Fact]
    public async Task Worker_RepairsTodaysCaseAtStartup()
    {
        await ConnectAsync(expires: _now.AddDays(3));
        var repo = Repo();
        var device = await repo.UpsertDeviceAsync(new HeatingDevice(Guid.NewGuid(), "viessmann", "1", "g", "0", "E3_Vitodens_050_0822", "Vitodens 050", true, null, null), CancellationToken.None);
        // As left by 0.8.0 on 2 Nov: the readings 00:00–01:00 put yesterday's 1.4 into today's highest.
        await repo.UpsertDailyAsync(new[]
        {
            new HeatingEnergyDay(device.Id, new DateOnly(2026, 11, 1), 1.4, 0.1, null, null, null, null, HeatingKinds.SourceSummary, 1.4, 0.1),
            new HeatingEnergyDay(device.Id, new DateOnly(2026, 11, 2), 1.4, 0.1, null, null, null, null, HeatingKinds.SourceSummary, 0.7, 0.0),
        }, CancellationToken.None);
        _now = Ro(2026, 11, 2, 10, 0);
        var (worker, state, _, log) = CreateLoggedWorker();

        await worker.LoadStoredAsync(CancellationToken.None);

        (await DaysAsync(device.Id)).Should().Equal(
            (new DateOnly(2026, 11, 1), (double?)1.4, (double?)1.4, (double?)0.1, (double?)0.1),
            (new DateOnly(2026, 11, 2), (double?)0.7, (double?)0.7, (double?)0.0, (double?)0.0));
        log.Messages.Where(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Information && m.Text.Contains("repaired 2026-11-02")).Should().HaveCount(2);
        state.Current!.Totals!.GasHeatingM3.Should().BeApproximately(2.1, 1e-9);
    }

    private async Task<HeatingDevice> SeedLeakedDayAsync(double todayLast)
    {
        var repo = Repo();
        var device = await repo.UpsertDeviceAsync(new HeatingDevice(Guid.NewGuid(), "viessmann", "1000001", "1111222233334444", "0", "E3_Vitodens_050_0822", "Vitodens 050", true, null, null), CancellationToken.None);
        // As left by 0.8.0 on 2 Nov: the readings 00:00–01:00 Romanian time put yesterday's totals (gas, heat,
        // electricity) into today's highest.
        await repo.UpsertDailyAsync(new[]
        {
            new HeatingEnergyDay(device.Id, new DateOnly(2026, 11, 1), 1.4, 0.1, 14.0, 1.0, 0.30, null, HeatingKinds.SourceSummary, 1.4, 0.1),
            new HeatingEnergyDay(device.Id, new DateOnly(2026, 11, 2), 1.4, 0.1, 14.0, 1.0, 0.30, null, HeatingKinds.SourceSummary, todayLast, 0.1),
        }, CancellationToken.None);
        return device;
    }

    [Fact]
    public async Task Worker_RepairsTheDeploymentCaseAtStartup_GasHeatAndElectricity()
    {
        // The real data at the deployment: 1 Nov 1.4 / 1.4, 2 Nov 1.4 / 0.8 (last above 50 %).
        await ConnectAsync(expires: _now.AddDays(3));
        var device = await SeedLeakedDayAsync(todayLast: 0.8);
        _now = Ro(2026, 11, 2, 10, 30);
        var (worker, state, _, log) = CreateLoggedWorker();

        await worker.LoadStoredAsync(CancellationToken.None);

        var days = await Repo().ListDailyAsync(device.Id, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2), CancellationToken.None);
        days.Select(d => (d.Day, d.GasHeatingMaxM3, d.GasHeatingLastM3, d.HeatHeatingKwh, d.HeatDhwKwh, d.PowerHeatingKwh)).Should().Equal(
            (new DateOnly(2026, 11, 1), (double?)1.4, (double?)1.4, (double?)14.0, (double?)1.0, (double?)0.30),
            (new DateOnly(2026, 11, 2), (double?)0.8, (double?)0.8, (double?)null, (double?)null, (double?)null));
        state.Current!.Totals!.GasHeatingM3.Should().BeApproximately(2.2, 1e-9);
        state.Current.Totals.HeatHeatingKwh.Should().BeApproximately(14.0, 1e-9);
        log.Messages.Count(m => m.Text.Contains("repaired 2026-11-02")).Should().Be(4, "gas heating + heat heating, heat dhw, electricity heating");

        // A second start the same day changes nothing.
        var (again, _, _, log2) = CreateLoggedWorker();
        await again.LoadStoredAsync(CancellationToken.None);
        log2.Messages.Should().NotContain(m => m.Text.Contains("repaired"));
    }

    [Fact]
    public async Task Worker_Total_IsTheDatabaseSum_ADropIsLoggedAtWarningOnce()
    {
        // Published at 00:10 CET (before the repair is due), repaired at the poll of 00:40 CET: today's heat and
        // electricity were yesterday's, the totals drop once and that is logged; the published value is the database's.
        await ConnectAsync(expires: _now.AddDays(3));
        await SeedLeakedDayAsync(todayLast: 0.6);
        _now = Ro(2026, 11, 2, 1, 10);
        var (worker, state, client, log) = CreateLoggedWorker();
        await worker.LoadStoredAsync(CancellationToken.None);
        state.Current!.Totals!.HeatHeatingKwh.Should().BeApproximately(28.0, 1e-9);

        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 40), 0.6, 0.1);

        state.Current!.Totals!.HeatHeatingKwh.Should().BeApproximately(14.0, 1e-9);
        state.Current.Totals.GasHeatingM3.Should().BeApproximately(2.0, 1e-9);
        log.Messages.Where(m => m.Level == Microsoft.Extensions.Logging.LogLevel.Warning && m.Text.Contains("Heating total")).Select(m => m.Text)
            .Should().ContainSingle(t => t.Contains("heat heating kWh dropped from 28 to 14"));
        await ReadAtAsync(worker, client, Ro(2026, 11, 2, 1, 42), 0.6, 0.1);
        log.Messages.Count(m => m.Text.Contains("heat heating kWh dropped")).Should().Be(1, "once a day");
    }

    [Fact]
    public async Task Settings_BoilerTimeZone_DefaultsToBerlin_UnknownRefused()
    {
        (await _settings.GetAsync(CancellationToken.None)).BoilerTimeZoneId.Should().Be("Europe/Berlin");
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "c", 120, 0.31m, "m3", 10.55, BoilerTimeZone: "Europe/Bucharest"), CancellationToken.None);
        var s = await _settings.GetAsync(CancellationToken.None);
        s.BoilerTimeZoneId.Should().Be("Europe/Bucharest");
        s.BoilerTimeZone.BaseUtcOffset.Should().Be(TimeSpan.FromHours(2));

        var act = () => _settings.SaveAsync(new HeatingSettingsUpdate(true, "c", 120, 0.31m, "m3", 10.55, BoilerTimeZone: "Mars/Olympus"), CancellationToken.None);
        await act.Should().ThrowAsync<FormatException>();
        // Saving without a zone keeps the stored one.
        await _settings.SaveAsync(new HeatingSettingsUpdate(true, "c", 120, 0.31m, "m3", 10.55), CancellationToken.None);
        (await _settings.GetAsync(CancellationToken.None)).BoilerTimeZoneId.Should().Be("Europe/Bucharest");
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
