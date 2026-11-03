using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>Scripted Miele API: tokens, devices answers and SSE streams; records every call.</summary>
internal sealed class FakeMieleClient : IMieleClient
{
    public List<string> Calls { get; } = new();
    public Func<string, MieleToken>? OnRefresh { get; set; }
    public Func<MieleToken>? OnExchange { get; set; }
    public Func<string> Devices { get; set; } = () => "{}";
    /// <summary>Each OpenEvents call takes the next script; null entry = throw (connection refused).</summary>
    public Queue<string[]?> Streams { get; } = new();
    public List<string> RefreshTokensSeen { get; } = new();
    /// <summary>lastUsedPrograms answer per (deviceId, calculateEnergyMonitoring); default: no history.</summary>
    public Func<string, bool, string> LastUsed { get; set; } = (_, _) => "[]";

    public Task<MieleToken> ExchangeCodeAsync(string clientId, string clientSecret, string code, string redirectUri, CancellationToken ct)
    {
        Calls.Add("exchange " + code + " " + redirectUri + " secret=" + clientSecret);
        return Task.FromResult(OnExchange?.Invoke() ?? throw new MieleApiException(500, "no exchange scripted"));
    }

    public Task<MieleToken> RefreshAsync(string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        Calls.Add("refresh");
        RefreshTokensSeen.Add(refreshToken);
        return Task.FromResult(OnRefresh?.Invoke(refreshToken) ?? throw new MieleApiException(500, "no refresh scripted"));
    }

    public Task LogoutAsync(string accessToken, CancellationToken ct)
    {
        Calls.Add("logout " + accessToken);
        return Task.CompletedTask;
    }

    public Task<string> GetDevicesJsonAsync(string accessToken, CancellationToken ct)
    {
        Calls.Add("devices");
        return Task.FromResult(Devices());
    }

    public Task<string> GetLastUsedProgramsJsonAsync(string accessToken, string deviceId, bool calculateEnergyMonitoring, CancellationToken ct)
    {
        Calls.Add("lastUsed " + deviceId + (calculateEnergyMonitoring ? " em" : ""));
        return Task.FromResult(LastUsed(deviceId, calculateEnergyMonitoring));
    }

    public Task<IMieleEventStream> OpenEventsAsync(string accessToken, CancellationToken ct)
    {
        Calls.Add("events");
        if (Streams.Count == 0 || Streams.Dequeue() is not { } lines) throw new HttpRequestException("connection refused");
        return Task.FromResult<IMieleEventStream>(new Stream(lines));
    }

    private sealed class Stream : IMieleEventStream
    {
        private readonly Queue<string> _lines;
        public Stream(IEnumerable<string> lines) => _lines = new Queue<string>(lines);
        public Task<string?> ReadLineAsync(CancellationToken ct) => Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class MieleRig : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    public const string Device = "000148508574";   // the serial of the spec's example

    public TestDbFactory Db { get; } = new();
    public TestScopeFactory Scopes { get; }
    public FakeMieleClient Client { get; } = new();
    public MieleSettingsService Settings { get; }
    public MieleAuthService Auth { get; }
    public MieleState State { get; } = new();
    public MieleRecorder Recorder { get; }
    public DateTimeOffset Now = T0;

    public MieleRig(string? secret = "s3cret")
    {
        Scopes = new TestScopeFactory(Db.CreateContext);
        Settings = new MieleSettingsService(Scopes, new MieleSettingsDefaults("client-1"), new FakeSecretProtector(),
            new MieleClientSecret(secret, "test", null));
        Auth = new MieleAuthService(Settings, Client, NullLogger<MieleAuthService>.Instance, () => Now);
        Recorder = new MieleRecorder(Scopes, State, NullLogger<MieleRecorder>.Instance);
    }

    public MieleHistorySync History() => new(Auth, Client, Recorder, State, Scopes, NullLogger<MieleHistorySync>.Instance, () => Now,
        (d, _) => { Now += d; return Task.CompletedTask; });

    public MieleStreamWorker Worker(List<TimeSpan>? waits = null) => new(Settings, Auth, Client, Recorder, State,
        NullLogger<MieleStreamWorker>.Instance, () => Now, (d, _) => { waits?.Add(d); Now += d; return Task.CompletedTask; }, TimeSpan.FromSeconds(1));

    public Task ConnectAsync(string refresh = "R1", TimeSpan? lifetime = null)
    {
        var life = (long)(lifetime ?? TimeSpan.FromDays(30)).TotalSeconds;
        return Settings.SaveTokenAsync(new MieleToken("A1", refresh, Now.AddSeconds(life), life), CancellationToken.None);
    }

    public async Task<T> Repo<T>(Func<MieleRepository, Task<T>> f)
    {
        using var ctx = Db.CreateContext();
        return await f(new MieleRepository(ctx));
    }

    public static string Json(int status, int phase, int remainingMin = 30, double kwh = 0, double water = 0, string deviceName = "")
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return "{\"" + Device + "\":{\"ident\":{\"type\":{\"value_raw\":1,\"value_localized\":\"Washing machine\"},\"deviceName\":\"" + deviceName + "\","
            + "\"deviceIdentLabel\":{\"fabNumber\":\"" + Device + "\",\"techType\":\"WWV980\"}},"
            + "\"state\":{\"ProgramID\":{\"value_raw\":1,\"value_localized\":\"Cottons\"},\"status\":{\"value_raw\":" + status + "},"
            + "\"programPhase\":{\"value_raw\":" + phase + "},\"remainingTime\":[0," + remainingMin + "],\"elapsedTime\":[0,0],\"signalDoor\":false,"
            + "\"ecoFeedback\":{\"currentEnergyConsumption\":{\"unit\":\"kWh\",\"value\":" + kwh.ToString(inv) + "},"
            + "\"currentWaterConsumption\":{\"unit\":\"l\",\"value\":" + water.ToString(inv) + "},\"energyForecast\":0.4,\"waterForecast\":0.3}}}}";
    }

    public void Dispose() => Db.Dispose();
}

public class MieleAuthTests
{
    [Fact]
    public async Task Callback_UnknownState_RejectedWithoutAnyCall_ValidState_ExchangesWithTheSecret_AndConnects()
    {
        using var rig = new MieleRig();
        (await rig.Auth.CompleteAsync("forged", "code", null, CancellationToken.None)).Result.Should().Be(MieleCallbackResult.InvalidState);
        rig.Client.Calls.Should().BeEmpty();

        var start = await rig.Auth.BeginAuthorizeAsync("http://192.168.254.2:8910/miele/callback", CancellationToken.None);
        start.Should().NotBeNull();
        start!.Value.Url.Should().StartWith(MieleApi.AuthorizeUrl + "?response_type=code&client_id=client-1&redirect_uri=http%3A%2F%2F192.168.254.2%3A8910%2Fmiele%2Fcallback")
            .And.Contain("scope=openid%20mcs_thirdparty_read").And.NotContain("code_challenge").And.NotContain("s3cret");
        var connected = 0;
        rig.Auth.Connected += () => connected++;
        rig.Client.OnExchange = () => new MieleToken("A1", "R1", rig.Now.AddDays(30), 2592000);
        (await rig.Auth.CompleteAsync(start.Value.State, "the-code", null, CancellationToken.None)).Result.Should().Be(MieleCallbackResult.Connected);
        rig.Client.Calls.Should().Equal("exchange the-code http://192.168.254.2:8910/miele/callback secret=s3cret");
        connected.Should().Be(1);
        (await rig.Auth.CompleteAsync(start.Value.State, "the-code", null, CancellationToken.None)).Result.Should().Be(MieleCallbackResult.InvalidState, "single use");

        var s = await rig.Settings.GetAsync(CancellationToken.None);
        s.IsConnected.Should().BeTrue();
        s.Token!.RefreshToken.Should().Be("R1");
        using var ctx = rig.Db.CreateContext();
        var stored = await new SettingsRepository(ctx).GetAllAsync(CancellationToken.None);
        stored[MieleSettingKeys.MieleRefreshToken].Should().StartWith("enc:", "tokens are stored encrypted");
        stored.Values.Should().NotContain(v => v.Contains("s3cret"), "the client secret never goes into the database");
    }

    [Fact]
    public async Task Refresh_AtTenPercentBeforeExpiry_RotatesTheRefreshToken()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync("R1", TimeSpan.FromDays(30));
        var n = 1;
        rig.Client.OnRefresh = _ => new MieleToken("A" + ++n, "R" + n, rig.Now.AddDays(30), 2592000);

        rig.Now = MieleRig.T0.AddDays(26);
        (await rig.Auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("A1", "27 of 30 days = refresh due");
        rig.Client.Calls.Should().BeEmpty();

        rig.Now = MieleRig.T0.AddDays(27).AddMinutes(1);
        (await rig.Auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("A2");
        (await rig.Settings.GetAsync(CancellationToken.None)).Token!.RefreshToken.Should().Be("R2", "the new refresh token replaces the old one");

        rig.Now += TimeSpan.FromDays(27).Add(TimeSpan.FromMinutes(1));
        (await rig.Auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("A3");
        rig.Client.RefreshTokensSeen.Should().Equal("R1", "R2");
    }

    [Fact]
    public async Task RefusedRefresh_Disconnected_RetriedAtMostOnceAnHour_ThenRecovers()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync("R1", TimeSpan.FromHours(1));
        rig.Now = MieleRig.T0.AddMinutes(55);
        rig.Client.OnRefresh = _ => throw new MieleAuthException("token endpoint HTTP 400 invalid_grant");

        await rig.Auth.Invoking(a => a.GetAccessTokenAsync(CancellationToken.None)).Should().ThrowAsync<MieleDisconnectedException>();
        var s = await rig.Settings.GetAsync(CancellationToken.None);
        s.IsConnected.Should().BeFalse();
        s.AuthError.Should().Contain("refuzată");
        s.Token.Should().NotBeNull("the tokens are kept for the hourly retry");

        rig.Now = MieleRig.T0.AddMinutes(55 + 59);
        await rig.Auth.Invoking(a => a.GetAccessTokenAsync(CancellationToken.None)).Should().ThrowAsync<MieleDisconnectedException>();
        await rig.Auth.Invoking(a => a.ForceRefreshAsync(CancellationToken.None)).Should().ThrowAsync<MieleDisconnectedException>();
        rig.Client.Calls.Count(c => c == "refresh").Should().Be(1, "no retry within the hour");

        rig.Now = MieleRig.T0.AddMinutes(55 + 61);
        rig.Client.OnRefresh = r => new MieleToken("A2", "R2", rig.Now.AddHours(1), 3600);
        (await rig.Auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("A2");
        (await rig.Settings.GetAsync(CancellationToken.None)).IsConnected.Should().BeTrue();
        rig.Client.Calls.Count(c => c == "refresh").Should().Be(2);
    }

    [Fact]
    public async Task TemporaryRefreshFailure_KeepsUsingTheValidAccessToken()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync("R1", TimeSpan.FromDays(30));
        rig.Now = MieleRig.T0.AddDays(28);
        rig.Client.OnRefresh = _ => throw new MieleApiException(503, "unavailable");
        (await rig.Auth.GetAccessTokenAsync(CancellationToken.None)).Should().Be("A1");
        (await rig.Settings.GetAsync(CancellationToken.None)).IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Logout_InvalidatesAtMiele_ClearsTheTokens_RaisesDisconnected()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        var raised = 0;
        rig.Auth.Disconnected += () => raised++;
        await rig.Auth.LogoutAsync(CancellationToken.None);
        rig.Client.Calls.Should().Equal("logout A1");
        var s = await rig.Settings.GetAsync(CancellationToken.None);
        s.Token.Should().BeNull();
        s.AuthError.Should().Be("Deconectat de utilizator.");
        raised.Should().Be(1);
    }

    [Fact]
    public async Task ClientSecret_FromEnvOrFile_NeverWithoutOne_AndAnotherClientIdClearsTheTokens()
    {
        MieleClientSecret.Load(new MieleSettingsDefaults(MieleClientSecret: " env-secret "), _ => null).Value.Should().Be("env-secret");
        // 0.12.0: a mounted file wins over the variable; an empty file leaves the variable in charge.
        MieleClientSecret.Load(new MieleSettingsDefaults(MieleClientSecret: "env-secret"), _ => "file-secret").Value.Should().Be("file-secret");
        MieleClientSecret.Load(new MieleSettingsDefaults(MieleClientSecret: "env-secret"), _ => "").Source.Should().Contain("KasaMonitor__MieleClientSecret");
        var fromFile = MieleClientSecret.Load(new MieleSettingsDefaults(MieleClientSecretFile: "/run/secrets/x"), p => p == "/run/secrets/x" ? "file-secret\n" : null);
        fromFile.Value.Should().Be("file-secret");
        fromFile.Source.Should().Be("fișierul /run/secrets/x");
        fromFile.ToString().Should().NotContain("file-secret");
        MieleClientSecret.Load(new MieleSettingsDefaults(), _ => null).Problem.Should().Be("fișierul " + MieleApi.DefaultClientSecretFile + " nu există");
        MieleClientSecret.Load(new MieleSettingsDefaults(), _ => "  ").IsSet.Should().BeFalse();

        using var rig = new MieleRig(secret: null);
        (await rig.Auth.BeginAuthorizeAsync("http://x/miele/callback", CancellationToken.None)).Should().BeNull("no secret: the flow cannot start");
        await rig.ConnectAsync();
        await rig.Settings.SaveClientIdAsync("client-1", CancellationToken.None);
        (await rig.Settings.GetAsync(CancellationToken.None)).Token.Should().NotBeNull("same client");
        await rig.Settings.SaveClientIdAsync("client-2", CancellationToken.None);
        (await rig.Settings.GetAsync(CancellationToken.None)).Token.Should().BeNull("another client must connect again");
    }
}

public class MieleRecorderTests
{
    [Fact]
    public async Task Cycle_StoredWithPhases_ApiAndPlugEnergy_SnapshotsOnChangeOnly_RestoredAfterRestart()
    {
        using var rig = new MieleRig();
        var plug = Guid.NewGuid();
        using (var ctx = rig.Db.CreateContext())
        {
            // The plug's live minutes: 600 W (10 Wh per minute) for 2 hours.
            var minutes = Enumerable.Range(0, 120).Select(i => new LiveMinuteAggregate(plug, MieleRig.T0.AddMinutes(i), 0, 600, 600, null, 10, 60, 60)).ToList();
            await new LiveRepository(ctx).SaveBatchAsync(Array.Empty<LiveSample>(), minutes, CancellationToken.None);
        }
        var t = MieleRig.T0;
        await rig.Recorder.LoadAsync(CancellationToken.None);
        await rig.Recorder.ProcessAsync(MieleRig.Json(4, 256), t, CancellationToken.None);
        await rig.Repo(r => r.SetPlugAsync(MieleRig.Device, plug, CancellationToken.None).ContinueWith(_ => 0));
        await rig.Recorder.ReloadDeviceAsync(MieleRig.Device, CancellationToken.None);
        await rig.Recorder.ProcessAsync(MieleRig.Json(4, 256), t.AddMinutes(1), CancellationToken.None);   // unchanged: no snapshot
        await rig.Recorder.ProcessAsync(MieleRig.Json(5, 260, 60, 0.1, 5), t.AddMinutes(10), CancellationToken.None);
        await rig.Recorder.ProcessAsync(MieleRig.Json(5, 260, 60, 0.1, 5), t.AddMinutes(16), CancellationToken.None);   // heartbeat (≥ 5 min)

        // Restart in the middle of the cycle: a new recorder continues the open cycle.
        var state2 = new MieleState();
        var recorder2 = new MieleRecorder(rig.Scopes, state2, NullLogger<MieleRecorder>.Instance);
        await recorder2.LoadAsync(CancellationToken.None);
        state2.Appliances.Should().ContainSingle().Which.OpenCycle.Should().NotBeNull();
        await recorder2.ProcessAsync(MieleRig.Json(5, 266, 10, 0.9, 45), t.AddMinutes(50), CancellationToken.None);
        await recorder2.ProcessAsync(MieleRig.Json(7, 268, 0, 0, 0), t.AddMinutes(70), CancellationToken.None);

        var snaps = await rig.Repo(r => r.ListSnapshotsAsync(MieleRig.Device, t, t.AddHours(2), CancellationToken.None));
        snaps.Select(s => (s.TsUtc - t).TotalMinutes).Should().Equal(0, 10, 16, 50, 70);
        snaps[0].Values.StatusText.Should().Be("Așteaptă pornirea");
        snaps[2].Values.PhaseText.Should().Be("Spălare principală");

        var cycle = (await rig.Repo(r => r.ListCyclesAsync(MieleRig.Device, 10, CancellationToken.None))).Should().ContainSingle().Subject;
        cycle.StartUtc.Should().Be(t.AddMinutes(10));
        cycle.EndUtc.Should().Be(t.AddMinutes(70));
        cycle.EndReason.Should().Be(MieleEndReasons.End);
        cycle.Program.Should().Be("Cottons");
        cycle.Phases.Select(p => p.PhaseText).Should().Equal("Spălare principală", "Centrifugare", "Terminat");
        cycle.EnergyKwh.Should().Be(0.9);
        cycle.WaterL.Should().Be(45);
        cycle.PlugEnergyWh.Should().BeApproximately(600, 1e-6, "60 minutes × 10 Wh from the linked plug");

        var live = state2.Appliances.Should().ContainSingle().Subject;
        live.CyclesTotal.Should().Be(1);
        live.LastCycle!.EnergyKwh.Should().Be(0.9);
        live.OpenCycle.Should().BeNull();
        live.Device.Alias.Should().Be("Mașină de spălat", "no friendly name in the app: the type text");
        live.Device.TechType.Should().Be("WWV980");
    }

    [Fact]
    public async Task SilentAppliance_CycleClosedByTheTick_After30MinutesWithoutRunning()
    {
        using var rig = new MieleRig();
        var t = MieleRig.T0;
        await rig.Recorder.ProcessAsync(MieleRig.Json(5, 260), t, CancellationToken.None);
        await rig.Recorder.ProcessAsync(MieleRig.Json(255, 260), t.AddMinutes(5), CancellationToken.None);
        await rig.Recorder.TickAsync(t.AddMinutes(29), CancellationToken.None);
        (await rig.Repo(r => r.ListOpenCyclesAsync(CancellationToken.None))).Should().ContainSingle();
        await rig.Recorder.TickAsync(t.AddMinutes(31), CancellationToken.None);
        var c = (await rig.Repo(r => r.ListCyclesAsync(MieleRig.Device, 5, CancellationToken.None))).Should().ContainSingle().Subject;
        c.EndReason.Should().Be(MieleEndReasons.Timeout);
        c.EndUtc.Should().Be(t);
        c.PlugEnergyWh.Should().BeNull("no plug linked");
    }
}

public class MieleStreamWorkerTests
{
    [Fact]
    public async Task NotConnected_NoCall_IdleStatus()
    {
        using var rig = new MieleRig();
        var delay = await rig.Worker().RunOnceAsync(CancellationToken.None);
        delay.Should().Be(MieleStreamWorker.IdleDelay);
        rig.Client.Calls.Should().BeEmpty();
        rig.State.Status.Should().Be("Contul Miele nu e conectat.");
    }

    [Fact]
    public async Task FullGetFirst_ThenSse_DevicesEventsRecorded_PingTicks()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        rig.Client.Devices = () => MieleRig.Json(4, 256);
        rig.Client.Streams.Enqueue(new[]
        {
            "event: devices", "data: " + MieleRig.Json(5, 260), "",
            "event: ping", "data: ", "",
            "event: actions", "data: {}", "",
        });
        var w = rig.Worker();
        var delay = await w.RunOnceAsync(CancellationToken.None);
        rig.Client.Calls.Should().Equal("devices", "events");
        delay.Should().Be(MieleApi.MinReconnectDelay, "the server ended a stream that worked: reconnect after 5 s");
        w.ConsecutiveFailures.Should().Be(1);
        rig.State.LastEventName.Should().Be("actions");
        var a = rig.State.Appliances.Should().ContainSingle().Subject;
        a.Values!.IsRunning.Should().BeTrue();
        a.OpenCycle.Should().NotBeNull();
        rig.State.StreamConnected.Should().BeFalse("closed after the stream ended");
    }

    [Fact]
    public async Task ThreeFailedStreams_FallBackToPollingEvery60s_UntilSseWorksAgain()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        rig.Client.Devices = () => MieleRig.Json(4, 256);
        for (var i = 0; i < 3; i++) rig.Client.Streams.Enqueue(null);
        var waits = new List<TimeSpan>();
        var w = rig.Worker(waits);
        var delays = new List<TimeSpan>();
        for (var i = 0; i < 3; i++) delays.Add(await w.RunOnceAsync(CancellationToken.None));
        delays.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        w.Polling.Should().BeTrue();
        rig.State.Mode.Should().Be(MieleStreamMode.Polling);

        // The wait in polling mode: at least 60 s, a GET every 60 s (here 300 s: 4 polls, the 5th is the next attempt's GET).
        rig.Client.Calls.Clear();
        var wait = typeof(MieleStreamWorker).GetMethod("WaitAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)wait.Invoke(w, new object[] { TimeSpan.FromMinutes(5), CancellationToken.None })!;
        rig.Client.Calls.Should().Equal("devices", "devices", "devices", "devices");
        waits.TakeLast(5).Should().OnlyContain(d => d == TimeSpan.FromSeconds(60));

        rig.Client.Streams.Enqueue(new[] { "event: ping", "", });
        rig.Client.Calls.Clear();
        await w.RunOnceAsync(CancellationToken.None);
        rig.Client.Calls.Should().Equal("devices", "events");
        w.Polling.Should().BeFalse("an event arrived: SSE works again");
    }

    [Fact]
    public async Task Api401_OneForcedRefresh_AndRetry()
    {
        using var rig = new MieleRig();
        await rig.ConnectAsync();
        var first = true;
        rig.Client.Devices = () =>
        {
            if (!first) return "{}";
            first = false;
            throw new MieleAuthException("devices HTTP 401");
        };
        rig.Client.OnRefresh = _ => new MieleToken("A2", "R2", rig.Now.AddDays(30), 2592000);
        await rig.Worker().FullReadAsync(CancellationToken.None);
        rig.Client.Calls.Should().Equal("devices", "refresh", "devices");
    }
}
