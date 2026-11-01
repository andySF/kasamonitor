using System.Net;
using System.Text.Json;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>Fake WebSocket of one device: answers Shelly.GetStatus with the captured EM Gen3 status and EM1.GetStatus per channel.</summary>
internal sealed class FakeRpcSocket : IShellyRpcSocket
{
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(string Method, int? Id)> Calls { get; } = new();
    public Func<string, int?, JsonElement>? Answer { get; set; }
    public event Action<string, JsonElement>? Notification;
    public Task Completion => _closed.Task;

    public Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        int? id = parameters is IDictionary<string, object> p && p.TryGetValue("id", out var v) ? (int)v : null;
        lock (Calls) Calls.Add((method, id));
        if (_closed.Task.IsCompleted) throw new System.Net.WebSockets.WebSocketException("closed");
        if (method == "Shelly.GetStatus") return Task.FromResult(FakeShellyClient.Json(FakeShellyClient.Fixture("shelly_emg3_status.json")));
        return Task.FromResult(Answer?.Invoke(method, id)
            ?? FakeShellyClient.Json($$"""{"id":{{id}},"voltage":230.1,"current":2.5,"act_power":{{500 + 100 * (id ?? 0)}}.0,"aprt_power":600,"pf":0.9,"freq":50.0}"""));
    }

    public void Notify(string method, string json) => Notification?.Invoke(method, FakeShellyClient.Json(json));
    public void Close() => _closed.TrySetResult();
    public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
}

internal sealed class FakeRpcSocketFactory : IShellyRpcSocketFactory
{
    public List<FakeRpcSocket> Sockets { get; } = new();
    public int Connects;
    public Exception? ConnectError { get; set; }

    public Task<IShellyRpcSocket> ConnectAsync(IPAddress ip, string? password, CancellationToken ct)
    {
        Interlocked.Increment(ref Connects);
        if (ConnectError is not null) throw ConnectError;
        var s = new FakeRpcSocket();
        lock (Sockets) Sockets.Add(s);
        return Task.FromResult<IShellyRpcSocket>(s);
    }

    public FakeRpcSocket Last { get { lock (Sockets) return Sockets[^1]; } }
}

public class LiveStreamTests
{
    private const string EmIp = "192.168.254.141";
    private const string EmMac = "d885ac0b743c";
    private const string KasaIp = "192.168.254.105";
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        public TestDbFactory Db { get; } = new();
        public TestScopeFactory Scopes { get; }
        public KasaSettingsService Settings { get; }
        public LiveStreamBroadcaster Broadcaster { get; } = new();
        public PlugStateBroadcaster States { get; } = new();
        public FakeRpcSocketFactory Sockets { get; } = new();
        public FakeShellyClient Shelly { get; } = new();
        public FakeKasaClient Kasa { get; } = new();
        public FakeTapoClient Tapo { get; } = new();
        public DateTimeOffset Now = T0;

        public Rig(KasaSettingsDefaults? defaults = null)
        {
            Scopes = new TestScopeFactory(Db.CreateContext);
            Settings = TestSettings.Create(Scopes, defaults ?? new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255"));
        }

        /// <summary>Worker on a fake clock: ticks every few ms, samples are stamped with <see cref="Now"/>.</summary>
        public LiveStreamWorker Worker() => new(Scopes, Settings, Broadcaster, Sockets, Kasa, Tapo,
            NullLogger<LiveStreamWorker>.Instance, () => Now, TimeSpan.FromMilliseconds(5));

        public RelayControlService Relay() => new(Shelly, Scopes, Settings, Broadcaster, States, NullLogger<RelayControlService>.Instance);

        public async Task<Guid> AddAsync(string mac, int channel, PlugRole role, string protocol = PlugProtocols.ShellyRpc, string ip = EmIp)
        {
            using var ctx = Db.CreateContext();
            return await new PlugRepository(ctx).UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse(ip),
                new PlugInfo(mac, $"{mac} ch{channel}", protocol == PlugProtocols.ShellyRpc ? "S3EM-002CXCEU" : "KP115(EU)", "EMG3", "2.1.1", "gen3", true, -50, TimeSpan.Zero),
                protocol, null, channel, role), DateTimeOffset.UtcNow, CancellationToken.None);
        }

        public async Task SetStreamAsync(Guid id, bool on)
        {
            using var ctx = Db.CreateContext();
            await new PlugRepository(ctx).SetStreamEnabledAsync(id, on, CancellationToken.None);
        }

        /// <summary>Moves the fake clock one second per step, giving the worker time to tick in between.</summary>
        public async Task RunSecondsAsync(int seconds, int stepMs = 25)
        {
            for (var i = 0; i < seconds; i++)
            {
                Now = Now.AddSeconds(1);
                await Task.Delay(stepMs);
            }
        }

        public void Dispose() => Db.Dispose();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Targets_EveryStreamedChannel_ShellyGroupedByMac_SubMetersOffByDefault()
    {
        using var rig = new Rig(new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", TapoUsername: "u@x", TapoPassword: "pw"));
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        await rig.AddAsync(EmMac, 1, PlugRole.SubMeter);                                            // free clamp: off by default
        await rig.AddAsync("aabbccddeeff", 0, PlugRole.Plug);                                       // Shelly plug: own WebSocket
        var kasa = await rig.AddAsync("c006c3702bfc", 0, PlugRole.Plug, PlugProtocols.Legacy, KasaIp);
        var tapo = await rig.AddAsync("b01921c4c68b", 0, PlugRole.Plug, PlugProtocols.Klap, "192.168.254.110");
        using var ctx = rig.Db.CreateContext();
        var plugs = await new PlugRepository(ctx).ListAllAsync(CancellationToken.None);

        var targets = LiveStreamWorker.Targets(plugs, await rig.Settings.GetAsync(CancellationToken.None), rig.Tapo);

        targets.Select(t => (t.Key, t.Kind)).Should().BeEquivalentTo(new[]
        {
            (EmMac, LiveStreamWorker.StreamKind.Shelly), ("aabbccddeeff", LiveStreamWorker.StreamKind.Shelly),
            ("c006c3702bfc", LiveStreamWorker.StreamKind.Legacy), ("b01921c4c68b", LiveStreamWorker.StreamKind.Tapo),
        });
        var em = targets.Single(t => t.Key == EmMac);
        em.Channels.Should().ContainSingle().Which.Should().Be(new LiveStreamWorker.StreamChannel(main, 0, $"{EmMac} ch0", true));
        em.AllChannels.Should().HaveCount(2);
        targets.Single(t => t.Kind == LiveStreamWorker.StreamKind.Legacy).Channels.Single().PlugId.Should().Be(kasa);
        targets.Single(t => t.Kind == LiveStreamWorker.StreamKind.Tapo).Channels.Single().PlugId.Should().Be(tapo);

        var noCreds = await new Rig().Settings.GetAsync(CancellationToken.None);
        LiveStreamWorker.Targets(plugs, noCreds, rig.Tapo).Should().NotContain(t => t.Kind == LiveStreamWorker.StreamKind.Tapo);
        var off = await new Rig(new KasaSettingsDefaults(LiveStreamEnabled: false)).Settings.GetAsync(CancellationToken.None);
        LiveStreamWorker.Targets(plugs, off).Should().BeEmpty();
    }

    [Fact]
    public async Task Shelly_MainMeterAt1s_AggregatesEverySample_ButWritesOnlyChangesAndTheWitness()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        var sub = await rig.AddAsync(EmMac, 1, PlugRole.SubMeter);
        await rig.SetStreamAsync(sub, true);
        var worker = rig.Worker();
        using var cts = new CancellationTokenSource();

        await worker.SuperviseOnceAsync(cts.Token);
        await WaitUntilAsync(() => rig.Broadcaster.Latest(main) is not null && rig.Broadcaster.Latest(sub) is not null);
        await rig.RunSecondsAsync(70);   // 18:00:00 → 18:01:10, constant power
        cts.Cancel();
        await Task.Delay(50);
        await worker.FlushAsync(CancellationToken.None);

        var socket = rig.Sockets.Last;
        socket.Calls[0].Method.Should().Be("Shelly.GetStatus");                    // first frame: full status (valid src)
        socket.Calls.Skip(1).Should().OnlyContain(c => c.Method == "EM1.GetStatus");
        rig.Broadcaster.Relay(main)!.On.Should().BeTrue();                          // switch:0 of the initial status

        using var ctx = rig.Db.CreateContext();
        var repo = new LiveRepository(ctx);
        var stored = await repo.ListSamplesAsync(main, T0, T0.AddMinutes(2), CancellationToken.None);
        stored.Select(s => s.TsUtc).Should().Equal(T0, T0.AddSeconds(60));       // first + 60 s witness, nothing in between
        stored[0].VoltageV.Should().Be(230.1);
        var minute = (await repo.ListMinutesAsync(main, T0, T0, CancellationToken.None)).Should().ContainSingle().Subject;
        minute.SampleCount.Should().BeGreaterThanOrEqualTo(55);                      // aggregated from every sample received
        minute.AvgW.Should().Be(500);
        minute.EnergyWh.Should().BeApproximately(500.0 * minute.CoveredSeconds / 3600, 1e-9);
        worker.SamplesReceived.Should().BeGreaterThan(worker.SamplesWritten * 10);
    }

    [Fact]
    public async Task Legacy_QuietPlug_SlowsDownTo10s_AndAHouseEventBringsItBackTo1s()
    {
        using var rig = new Rig();
        var kasa = await rig.AddAsync("c006c3702bfc", 0, PlugRole.Plug, PlugProtocols.Legacy, KasaIp);
        var calls = new List<DateTimeOffset>();
        rig.Kasa.OnGetRealtime = _ => { lock (calls) calls.Add(rig.Now); return Task.FromResult(new EmeterReading(40_000, 230_000, 200, 10)); };
        var worker = rig.Worker();
        using var cts = new CancellationTokenSource();

        await worker.SuperviseOnceAsync(cts.Token);
        await WaitUntilAsync(() => calls.Count > 0);
        await rig.RunSecondsAsync(130);
        int quietCalls;
        lock (calls) quietCalls = calls.Count;

        rig.Broadcaster.PublishHouseEvent(rig.Now);   // the main meter jumped
        await Task.Delay(40);
        var before = quietCalls;
        await rig.RunSecondsAsync(10);
        cts.Cancel();

        quietCalls.Should().BeInRange(66, 80);   // 61 reads in the first minute at 1 s, then 2, 3, … 10 s apart (not 131)
        List<DateTimeOffset> after;
        lock (calls) after = calls.Skip(before).ToList();
        after.Should().HaveCountGreaterThanOrEqualTo(9);   // back at 1 s
        worker.BoostUntil.Should().BeAfter(rig.Now);
    }

    [Fact]
    public async Task Tapo_StreamsGetEnergyUsage_OnTheSharedClient()
    {
        using var rig = new Rig(new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", TapoUsername: "u@x", TapoPassword: "pw"));
        var tapo = await rig.AddAsync("b01921c4c68b", 0, PlugRole.Plug, PlugProtocols.Klap, "192.168.254.110");
        rig.Tapo.OnGetEnergyUsage = (_, _) => Task.FromResult(new EmeterReading(12_300, null, null, 100));
        var worker = rig.Worker();
        using var cts = new CancellationTokenSource();

        await worker.SuperviseOnceAsync(cts.Token);
        await WaitUntilAsync(() => rig.Broadcaster.Latest(tapo) is not null);
        cts.Cancel();

        rig.Broadcaster.Latest(tapo)!.PowerW.Should().Be(12.3);
        rig.Tapo.LiveCalls.Should().BeGreaterThan(0);
        rig.Tapo.EnergyCalls.Should().Be(0);   // no get_emeter_data on the live path
    }

    [Fact]
    public async Task MainMeterJump_AboveTheBurstTrigger_PublishesAHouseEvent()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        var events = new List<DateTimeOffset>();
        rig.Broadcaster.HouseEvent += t => { lock (events) events.Add(t); };
        var power = 500.0;
        var worker = rig.Worker();
        using var cts = new CancellationTokenSource();
        await worker.SuperviseOnceAsync(cts.Token);
        await WaitUntilAsync(() => rig.Broadcaster.Latest(main) is not null);
        rig.Sockets.Last.Answer = (_, id) => FakeShellyClient.Json($$"""{"id":{{id}},"act_power":{{power}},"voltage":230}""");

        await rig.RunSecondsAsync(2);
        events.Should().BeEmpty();
        power = 540;    // +40 W: normal noise of a house, below the default 50 W trigger
        await rig.RunSecondsAsync(2);
        events.Should().BeEmpty();
        worker.BurstTriggerW.Should().Be(50);
        power = 2500;   // kettle on
        await rig.RunSecondsAsync(2);
        cts.Cancel();

        events.Should().ContainSingle();
        worker.BoostUntil.Should().BeAfter(rig.Now);
    }

    [Fact]
    public async Task Shelly_RelayNotification_IsPublished_AndTheStreamReconnects()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        var worker = rig.Worker();
        using var cts = new CancellationTokenSource();
        await worker.SuperviseOnceAsync(cts.Token);
        await WaitUntilAsync(() => rig.Broadcaster.Latest(main) is not null);

        rig.Sockets.Last.Notify("NotifyStatus", """{"ts":1793560000.1,"switch:0":{"id":0,"output":false,"source":"button"}}""");
        rig.Broadcaster.Relay(main)!.On.Should().BeFalse();

        rig.Sockets.Last.Close();   // device rebooted
        await WaitUntilAsync(() => rig.Sockets.Connects >= 2, 5000);   // backoff starts at 1 s
        cts.Cancel();
    }

    [Fact]
    public async Task Stream_StopsWhenTheBoxIsUnticked_OrTheStreamIsDisabled()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        var worker = rig.Worker();
        using var cts = new CancellationTokenSource();
        await worker.SuperviseOnceAsync(cts.Token);
        worker.StreamedDevices.Should().Equal(EmMac);

        await rig.SetStreamAsync(main, false);
        await worker.SuperviseOnceAsync(cts.Token);
        worker.StreamedDevices.Should().BeEmpty();

        await rig.SetStreamAsync(main, true);
        await worker.SuperviseOnceAsync(cts.Token);
        worker.StreamedDevices.Should().Equal(EmMac);
        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "192.168.254.255", true, "", null, false, LiveStreamEnabled: false), CancellationToken.None);
        await worker.SuperviseOnceAsync(cts.Token);
        worker.StreamedDevices.Should().BeEmpty();
        cts.Cancel();
    }

    [Fact]
    public void Accept_DropsASecondSampleInTheSameSecond_AndAFailingSubscriberDoesNotCostTheSample()
    {
        using var rig = new Rig();
        var worker = rig.Worker();
        var id = Guid.NewGuid();
        rig.Broadcaster.SampleReceived += _ => throw new InvalidOperationException("circuit gone");
        var seen = new List<double>();
        rig.Broadcaster.SampleReceived += s => seen.Add(s.PowerW);
        var t = T0.AddSeconds(5);

        worker.Accept(new LiveSample(id, t, 100));
        worker.Accept(new LiveSample(id, t, 200));
        worker.Accept(new LiveSample(id, t.AddSeconds(1), 300));

        worker.SamplesReceived.Should().Be(2);
        worker.SamplesWritten.Should().Be(2);   // 100 → 300 W is a change
        seen.Should().Equal(100, 300);          // the second subscriber still gets everything
    }

    [Fact]
    public async Task Flush_AfterARestartInTheSameMinute_MergesInsteadOfOverwriting()
    {
        using var rig = new Rig();
        var id = Guid.NewGuid();
        var first = rig.Worker();
        for (var s = 0; s < 24; s++) first.Accept(new LiveSample(id, T0.AddSeconds(s), 1000));
        await first.FlushAsync(CancellationToken.None, all: true);            // clean stop at 18:00:23

        var second = rig.Worker();                                            // restart, same minute
        for (var s = 32; s < 60; s++) second.Accept(new LiveSample(id, T0.AddSeconds(s), 2000));
        second.Accept(new LiveSample(id, T0.AddMinutes(1), 2000));            // closes 18:00
        await second.FlushAsync(CancellationToken.None);

        using var ctx = rig.Db.CreateContext();
        var m = (await new LiveRepository(ctx).ListMinutesAsync(id, T0, T0, CancellationToken.None)).Should().ContainSingle().Subject;
        m.SampleCount.Should().Be(52);
        m.CoveredSeconds.Should().Be(52);
        m.EnergyWh.Should().BeApproximately((24 * 1000 + 28 * 2000) / 3600.0, 1e-9);
        m.MinW.Should().Be(1000);
        m.MaxW.Should().Be(2000);
    }

    [Fact]
    public async Task LiveRetention_DeletesOnlyOldRawSamples_AndKeepsMinuteAggregates()
    {
        using var rig = new Rig();
        var now = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var plug = Guid.NewGuid();
        using (var ctx = rig.Db.CreateContext())
            await new LiveRepository(ctx).SaveBatchAsync(
                new[] { new LiveSample(plug, now.AddDays(-31), 1), new LiveSample(plug, now.AddDays(-30).AddSeconds(1), 2), new LiveSample(plug, now, 3) },
                new[] { new LiveMinuteAggregate(plug, now.AddDays(-40), 1, 1, 1, null, 0.01, 60, 60) }, CancellationToken.None);
        var retention = new ReadingsRetentionService(rig.Scopes, NullLogger<ReadingsRetentionService>.Instance, () => now);

        var plan = await retention.PlanLiveRawAsync(30, CancellationToken.None);
        plan.RowsToDelete.Should().Be(1);
        plan.NeedsConfirmation.Should().BeTrue();
        (await retention.PlanLiveRawAsync(0, CancellationToken.None)).NeedsConfirmation.Should().BeFalse();
        (await retention.ApplyLiveRawAsync(0, CancellationToken.None)).Should().Be(0);   // unlimited
        (await retention.ApplyLiveRawAsync(30, CancellationToken.None)).Should().Be(1);

        using var check = rig.Db.CreateContext();
        check.LiveSamples.Count().Should().Be(2);
        check.LiveMinuteAggregates.Count().Should().Be(1);
        (await rig.Settings.GetAsync(CancellationToken.None)).LiveRawRetentionDays.Should().Be(30);   // default
    }

    private static void PublishLive(Rig rig, Guid id, bool? relay) => rig.States.Publish(new PlugLiveState(id, EmMac, "EM", true,
        new EmeterReading(1, null, null, null, RelayOn: relay), DateTimeOffset.UtcNow, "S3EM-002CXCEU", PlugProtocols.ShellyRpc, 0, PlugRole.MainMeter));

    [Fact]
    public async Task Relay_SendsSwitchSetOnTheChannel_AndLogsIt()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        PublishLive(rig, main, relay: true);
        var relay = rig.Relay();

        var off = await relay.SetAsync(main, false, RelayCommandSources.Ui, CancellationToken.None);

        off.Success.Should().BeTrue();
        rig.Shelly.SwitchSets.Should().Equal((EmIp, 0, false));
        rig.Broadcaster.Relay(main)!.On.Should().BeFalse();
        var log = await relay.RecentAsync(null, 20, CancellationToken.None);
        log.Should().ContainSingle().Which.Should().Match<RelayCommand>(c => c.PlugId == main && !c.On && c.Source == "ui" && c.Result == "ok");
    }

    [Fact]
    public async Task Relay_ChannelWithoutRelay_IsRefusedByTheServer()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        var ch1 = await rig.AddAsync(EmMac, 1, PlugRole.SubMeter);
        PublishLive(rig, ch1, relay: null);   // EM Gen3 channel 1: no switch

        var r = await rig.Relay().SetAsync(ch1, false, RelayCommandSources.Ui, CancellationToken.None);

        r.Message.Should().Be(RelayControlService.NoRelayMessage);
        rig.Shelly.SwitchSets.Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_Disabled_IsRefused_WithoutTouchingTheDevice()
    {
        using var rig = new Rig(new KasaSettingsDefaults(AllowRelayControl: false));
        rig.Shelly.Add(EmIp);
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        PublishLive(rig, main, relay: true);

        var r = await rig.Relay().SetAsync(main, true, RelayCommandSources.Ui, CancellationToken.None);

        r.Success.Should().BeFalse();
        r.Message.Should().Be(RelayControlService.DisabledMessage);
        rig.Shelly.SwitchSets.Should().BeEmpty();
        (await rig.Relay().RecentAsync(null, 20, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Relay_KasaPlug_IsRefused_AndADeviceErrorIsLogged()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        var kasa = await rig.AddAsync("c006c3702bfc", 0, PlugRole.Plug, PlugProtocols.Legacy, KasaIp);
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        PublishLive(rig, main, relay: true);
        var relay = rig.Relay();

        (await relay.SetAsync(kasa, false, RelayCommandSources.Ui, CancellationToken.None)).Message.Should().Be(RelayControlService.NoRelayMessage);

        rig.Shelly.SwitchError = new TimeoutException("Switch.Set at 192.168.254.141: no answer within 3 s");
        var failed = await relay.SetAsync(main, true, RelayCommandSources.Ui, CancellationToken.None);

        failed.Success.Should().BeFalse();
        rig.Shelly.SwitchSets.Should().BeEmpty();
        (await relay.RecentAsync(main, 20, CancellationToken.None)).Should().ContainSingle().Which.Result.Should().Contain("no answer");
    }

    [Fact]
    public void Relay_HasRelay_AndCurrentState_PreferTheNewestReport()
    {
        var t = T0;
        var id = Guid.NewGuid();
        PlugLiveState Live(bool? relay, string protocol = PlugProtocols.ShellyRpc) =>
            new(id, "m", "a", true, new EmeterReading(1, null, null, null, RelayOn: relay), t, "S3EM-002CXCEU", protocol, 0, PlugRole.MainMeter);

        RelayControlService.HasRelay(Live(true)).Should().BeTrue();
        RelayControlService.HasRelay(Live(null)).Should().BeFalse();                           // channel 1 of EM Gen3: no relay
        RelayControlService.HasRelay(Live(true, PlugProtocols.Legacy)).Should().BeFalse();    // no Kasa/Tapo command
        RelayControlService.CurrentState(Live(true), new RelayState(id, false, t.AddSeconds(3))).Should().BeFalse();
        RelayControlService.CurrentState(Live(true), new RelayState(id, false, t.AddSeconds(-3))).Should().BeTrue();
        RelayControlService.CurrentState(Live(null), null).Should().BeNull();
    }

    [Fact]
    public async Task Polling_PublishesTheRole_InTheLiveState()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        var main = await rig.AddAsync(EmMac, 0, PlugRole.MainMeter);
        var sub = await rig.AddAsync(EmMac, 1, PlugRole.SubMeter);
        var bc = new PlugStateBroadcaster();
        var polling = new PollingWorker(new FakeKasaClient(), new FakeTapoClient(), rig.Scopes, bc, rig.Settings, TimeSpan.FromSeconds(10),
            NullLogger<PollingWorker>.Instance, shelly: rig.Shelly);

        await polling.RunOnceAsync(CancellationToken.None);

        bc.Snapshot()[main].Role.Should().Be(PlugRole.MainMeter);
        bc.Snapshot()[sub].Role.Should().Be(PlugRole.SubMeter);
    }
}
