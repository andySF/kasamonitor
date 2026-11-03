using System.Text.Json.Nodes;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Workers.Entities;
using KasaMonitor.Workers.Mqtt;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>A transport without a broker: records what the worker asks, delivers what the test sends.</summary>
internal sealed class FakeMqttTransport : IMqttTransport
{
    public MqttConnectResult Result { get; init; } = new(true, null);
    public List<string> Subscribed { get; } = new();
    public List<(string Topic, string Payload, bool Retain)> Published { get; } = new();
    public MqttConnectOptions? Options { get; private set; }
    public bool IsConnected { get; private set; }
    public event Func<MqttMessage, Task>? MessageReceived;
    public event Func<string?, Task>? Disconnected;

    public Task<MqttConnectResult> ConnectAsync(MqttConnectOptions options, CancellationToken ct)
    {
        Options = options;
        IsConnected = Result.Connected;
        return Task.FromResult(Result);
    }

    public Task SubscribeAsync(IReadOnlyCollection<string> topicFilters, CancellationToken ct)
    {
        lock (Subscribed) Subscribed.AddRange(topicFilters);
        return Task.CompletedTask;
    }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct)
    {
        Published.Add((topic, payload, retain));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task DeliverAsync(string topic, string payload) => MessageReceived?.Invoke(new MqttMessage(topic, payload, true)) ?? Task.CompletedTask;

    public Task DropAsync(string reason)
    {
        IsConnected = false;
        return Disconnected?.Invoke(reason) ?? Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeMqttFactory : IMqttTransportFactory
{
    private readonly Queue<FakeMqttTransport> _next = new();
    public List<FakeMqttTransport> Created { get; } = new();

    public FakeMqttFactory(params FakeMqttTransport[] next)
    {
        foreach (var t in next) _next.Enqueue(t);
    }

    public IMqttTransport Create()
    {
        var t = _next.Count > 0 ? _next.Dequeue() : new FakeMqttTransport();
        lock (Created) Created.Add(t);
        return t;
    }
}

/// <summary>0.11, F2: the MQTT consumer on the documentation fixtures through a fake transport, the storage, the climate adapter, the catalog.</summary>
public class MqttTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Sensor = "zigbee2mqtt_0xaabbccddee000001";
    private const string Plug = "zigbee2mqtt_0xaabbccddee000002";
    private const string Door = "zigbee2mqtt_0xaabbccddee000003";

    private static IReadOnlyList<(string Topic, string Payload, bool Retain)> Fixture(params string[] files) => files.SelectMany(f =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "mqtt", f)).Where(l => l.Trim().Length > 0).Select(l =>
        {
            var o = JsonNode.Parse(l)!.AsObject();
            var p = o["payload"];
            return (o["topic"]!.GetValue<string>(), p is JsonValue v && v.TryGetValue<string>(out var s) ? s : p!.ToJsonString(), o["retain"]?.GetValue<bool>() ?? false);
        })).ToList();

    private sealed class Rig : IDisposable
    {
        private readonly TestDbFactory _db = new();
        public DateTimeOffset Now = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        public TestScopeFactory Scopes { get; }
        public ClimateState Climate { get; } = new();
        public ClimateRecorder ClimateRecorder { get; }
        public MqttState State { get; } = new();
        public GenericRecorder Recorder { get; }
        public MqttSettingsService Settings { get; }
        public FakeMqttFactory Factory { get; }
        public MqttDiscoveryWorker Worker { get; }
        public DeviceCatalog Catalog { get; }
        public DeviceEntityService Entities { get; }

        public Rig(bool autoAdd = false, FakeMqttFactory? factory = null)
        {
            Scopes = new TestScopeFactory(_db.CreateContext);
            ClimateRecorder = new ClimateRecorder(Scopes, Climate);
            Recorder = new GenericRecorder(Scopes, State, ClimateRecorder);
            Settings = new MqttSettingsService(Scopes, new MqttSettingsDefaults(Host: "broker.test", User: "kasamonitor"), new MqttPassword("parola-test", "test", null));
            if (autoAdd) Settings.SaveAsync(new MqttSettingsUpdate(AutoAdd: true), Ct).GetAwaiter().GetResult();
            Factory = factory ?? new FakeMqttFactory();
            Worker = new MqttDiscoveryWorker(Settings, Factory, State, Recorder, NullLogger<MqttDiscoveryWorker>.Instance, () => Now, TimeSpan.FromMilliseconds(20));
            Catalog = new DeviceCatalog(Scopes, new PlugStateBroadcaster(), Climate, new HeatingState(), new MieleState(), ClimateRecorder, null, null, null, null, Recorder, State);
            Entities = new DeviceEntityService(new IEntitySource[] { new GenericEntitySource(Scopes, Recorder, State) }, Scopes);
        }

        public T Repo<T>() where T : class => (T)Scopes.CreateScope().ServiceProvider.GetService(typeof(T))!;

        public async Task FeedAsync(params string[] files)
        {
            foreach (var (topic, payload, retain) in Fixture(files)) await Worker.ProcessAsync(new MqttMessage(topic, payload, retain), Ct);
        }

        public void Dispose() => _db.Dispose();
    }

    [Fact]
    public async Task Discovered_DevicesStayInMemory_UntilAdded_ThenEntitiesAndValuesAreStored()
    {
        using var rig = new Rig();
        await rig.FeedAsync("z2m-discovery.jsonl", "esphome-discovery.jsonl", "ha-discovery.jsonl", "z2m-states.jsonl", "esphome-states.jsonl");
        rig.State.Registry.Devices(rig.Now).Should().HaveCount(7);
        (await rig.Repo<IGenericRepository>().ListDevicesAsync(true, Ct)).Should().BeEmpty("nothing is stored before „Adaugă”");
        MqttDiscovered.NotAdded(rig.State, rig.Recorder, rig.Now).Should().HaveCount(7);

        var device = await rig.Worker.AddAsync(Plug, Ct);
        device.Category.Should().Be(DeviceCategory.Meter);
        device.Name.Should().Be("Priza birou");
        device.Manufacturer.Should().Be("Tuya");
        var entities = await rig.Repo<IGenericRepository>().ListEntitiesAsync(device.Id, Ct);
        entities.Should().HaveCount(5);
        var energy = entities.Single(e => e.Description.Class == DeviceClass.Energy);
        energy.LastNumber.Should().BeApproximately(12_340, 1e-6, "the retained / last state is stored at once, in Wh");
        (await rig.Repo<IGenericRepository>().ListReadingsAsync(energy.Id, rig.Now.AddHours(-1), rig.Now.AddHours(1), Ct)).Should().ContainSingle();
        MqttDiscovered.NotAdded(rig.State, rig.Recorder, rig.Now).Should().HaveCount(6);
    }

    [Fact]
    public async Task ClimateCapableDevice_IsRoutedToClimate_OneRowInTheCatalog_R3_R4()
    {
        using var rig = new Rig();
        await rig.FeedAsync("z2m-discovery.jsonl", "z2m-states.jsonl");
        var device = await rig.Worker.AddAsync(Sensor, Ct);
        device.Category.Should().Be(DeviceCategory.ClimateSensor);

        var live = rig.Climate.Sensors.Should().ContainSingle().Subject;
        live.Sensor.Source.Should().Be(ClimateSources.Mqtt);
        live.Sensor.ExternalId.Should().Be(Sensor);
        (live.TemperatureC, live.HumidityPercent, live.BatteryPercent).Should().Be((21.37, 48.2, 91));
        rig.Climate.Expected(live).Should().Be(TimeSpan.FromHours(1), "R3: no expire_after → an hour, not 10 min");
        rig.Climate.IsOnline(live, rig.Now.AddHours(3)).Should().BeTrue("a stable temperature stays online for hours");

        var rows = await rig.Catalog.ListAsync(rig.Now, Ct);
        rows.Should().ContainSingle("one row per physical device: the routed climate sensor is not a second row");
        var row = rows.Single();
        (row.Key, row.Integration, row.Category, row.Health).Should().Be((DeviceRow.GenericKey(device.Id), IntegrationKeys.Mqtt, DeviceCategory.ClimateSensor, DeviceHealth.Online));
        row.Capabilities.Should().Contain("baterie");
        rig.Recorder.ClimateRoutedKeys().Should().HaveCount(3);
    }

    [Fact]
    public async Task R12_ClimateIsRecordedAtMostOnceIn10Seconds_TheLastValueWins()
    {
        using var rig = new Rig();
        await rig.FeedAsync("z2m-discovery.jsonl", "z2m-states.jsonl");
        await rig.Worker.AddAsync(Sensor, Ct);
        var first = rig.Climate.Sensors.Single().ReadUtc;
        rig.Now = rig.Now.AddSeconds(2);
        await rig.Worker.ProcessAsync(new MqttMessage("zigbee2mqtt/Dormitor senzor", """{"temperature":22.5,"humidity":48.2}""", false), Ct);
        rig.Now = rig.Now.AddSeconds(2);
        await rig.Worker.ProcessAsync(new MqttMessage("zigbee2mqtt/Dormitor senzor", """{"temperature":22.9,"humidity":48.2}""", false), Ct);
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        rig.Climate.Sensors.Single().ReadUtc.Should().Be(first, "inside 10 s of the last record nothing is recorded");
        rig.Now = rig.Now.AddSeconds(7);
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        rig.Climate.Sensors.Single().TemperatureC.Should().Be(22.9, "the last value wins");
    }

    [Fact]
    public async Task WriteRule_AChangeInsideTheWindowIsDeferred_NotLost_AndHourlyRollupsAreWritten()
    {
        using var rig = new Rig();
        await rig.FeedAsync("z2m-discovery.jsonl", "z2m-states.jsonl");
        var device = await rig.Worker.AddAsync(Plug, Ct);
        var power = (await rig.Repo<IGenericRepository>().ListEntitiesAsync(device.Id, Ct)).Single(e => e.Description.Class == DeviceClass.Power);
        var t0 = rig.Now;
        rig.Now = t0.AddSeconds(3);
        await rig.Worker.ProcessAsync(new MqttMessage("zigbee2mqtt/Priza birou", """{"state":"ON","power":1200,"energy":12.34}""", false), Ct);
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        (await rig.Repo<IGenericRepository>().ListReadingsAsync(power.Id, t0.AddHours(-1), t0.AddHours(1), Ct)).Select(r => r.Number).Should().Equal(63.4);
        rig.Now = t0.AddSeconds(12);
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        var stored = await rig.Repo<IGenericRepository>().ListReadingsAsync(power.Id, t0.AddHours(-1), t0.AddHours(1), Ct);
        stored.Select(r => r.Number).Should().Equal(63.4, 1200);
        stored[1].TsUtc.Should().Be(t0.AddSeconds(10), "stored at the end of the 10 s window");

        rig.Now = t0.AddHours(2);
        (await rig.Recorder.RollupAsync(rig.Now, Ct)).Should().BeGreaterThan(0);
        var hours = await rig.Repo<IGenericRepository>().ListHourlyAsync([power.Id], t0, t0.AddHours(2), Ct);
        hours.First().Hour.Max.Should().Be(1200);
    }

    [Fact]
    public async Task AutoAdd_Removal_AndRetire_R2()
    {
        using var rig = new Rig(autoAdd: true);
        await rig.FeedAsync("z2m-discovery.jsonl", "z2m-states.jsonl");
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        var devices = await rig.Repo<IGenericRepository>().ListDevicesAsync(true, Ct);
        devices.Select(d => d.ExternalId).Should().BeEquivalentTo(Sensor, Plug, Door);
        var sensor = devices.Single(d => d.ExternalId == Sensor);

        await rig.Worker.ProcessAsync(new MqttMessage("homeassistant/sensor/0xaabbccddee000001/pressure/config", "", true), Ct);
        (await rig.Repo<IGenericRepository>().ListEntitiesAsync(sensor.Id, Ct)).Single(e => e.ExternalId.EndsWith("_pressure_zigbee2mqtt", StringComparison.Ordinal))
            .IsRemoved.Should().BeTrue("an empty retained payload removes the entity, its rows stay");

        (await rig.Catalog.SetRetiredAsync(DeviceRow.GenericKey(sensor.Id), true, Ct)).Should().StartWith("Dispozitivul nu mai e salvat");
        (await rig.Repo<IGenericRepository>().GetDeviceAsync(sensor.Id, Ct))!.IsArchived.Should().BeTrue();
        (await rig.Repo<IClimateRepository>().ListSensorsAsync(true, Ct)).Single(s => s.Source == ClimateSources.Mqtt).IsArchived
            .Should().BeTrue("R2: the routed climate sensor is withdrawn with its device");
        rig.Climate.Sensors.Should().BeEmpty();
        await rig.Catalog.SetRetiredAsync(DeviceRow.GenericKey(sensor.Id), false, Ct);
        (await rig.Repo<IClimateRepository>().ListSensorsAsync(true, Ct)).Single(s => s.Source == ClimateSources.Mqtt).IsArchived.Should().BeFalse();

        await rig.Recorder.UpdateDeviceAsync(sensor.Id, "Dormitor", "Dormitor", null, Ct);
        rig.Climate.Sensors.Single().Sensor.Name.Should().Be("Dormitor", "the routed sensor follows the device's name");
    }

    [Fact]
    public async Task GeneratedPage_LiveValues_CommandsShownNotEnabled_UnsupportedListed()
    {
        using var rig = new Rig(autoAdd: true);
        await rig.FeedAsync("z2m-discovery.jsonl", "esphome-discovery.jsonl", "ha-discovery.jsonl", "z2m-states.jsonl", "esphome-states.jsonl");
        var esp = (await rig.Repo<IGenericRepository>().ListDevicesAsync(true, Ct)).Single(d => d.ExternalId == "aabbccddee04");
        var page = await rig.Entities.GetAsync(DeviceRow.GenericKey(esp.Id), rig.Now, Ct);
        page.Should().NotBeNull();
        page!.Available.Should().BeTrue();
        page.Entities.Should().HaveCount(4);
        var co2 = page.Entities.Single(e => e.Class == DeviceClass.CarbonDioxide);
        page.ValueOf(co2.Key)!.Number.Should().Be(612);
        var relay = page.Entities.Single(e => e.Kind == EntityKind.Switch);
        EntityViewRules.For(relay, null).Should().Match<EntityViewDecision>(v => v.View == EntityView.Command && !v.CommandEnabled);
        relay.Command!.Topic.Should().Be("senzor-co2/switch/releu/command");

        var light = (await rig.Repo<IGenericRepository>().ListDevicesAsync(true, Ct)).Single(d => d.ExternalId == "kitchen-light");
        var lp = await rig.Entities.GetAsync(DeviceRow.GenericKey(light.Id), rig.Now, Ct);
        EntityViewRules.For(lp!.Entities.Single(), null).Section.Should().Be(EntitySection.Unsupported);
        rig.State.Registry.Problems.Should().ContainSingle(p => p.Topic.Contains("orphan"));
        (await rig.Entities.GetAsync("dev:999", rig.Now, Ct)).Should().BeNull();
        (await rig.Entities.GetAsync("dev:abc", rig.Now, Ct)).Should().BeNull();

        // The counter of the plug: what it counted today (from the hourly rollups + the open hour).
        var plug = (await rig.Repo<IGenericRepository>().ListDevicesAsync(true, Ct)).Single(d => d.ExternalId == Plug);
        var energyKey = (await rig.Entities.GetAsync(DeviceRow.GenericKey(plug.Id), rig.Now, Ct))!.Entities.Single(e => e.Class == DeviceClass.Energy).Key;
        (await rig.Entities.CounterAsync(DeviceRow.GenericKey(plug.Id), energyKey, DateTimeOffset.UtcNow, TimeZoneInfo.Utc, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Connection_RefusedThenConnected_SubscribesThePrefixAndTheStateTopics_AndResubscribesAfterADrop()
    {
        var refused = new FakeMqttTransport { Result = new MqttConnectResult(false, "autentificare refuzată") };
        var first = new FakeMqttTransport();
        var second = new FakeMqttTransport();
        using var rig = new Rig(factory: new FakeMqttFactory(refused, first, second));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await rig.Worker.StartAsync(cts.Token);
        try
        {
            await Until(() => first.IsConnected, "the second attempt connects");
            refused.Options!.Password.Should().Be("parola-test");
            refused.Options.ToString().Should().NotContain("parola-test", "the options never print the password");
            rig.State.LastError.Should().Be("autentificare refuzată", "v5 does not throw on a refused CONNACK: the result is checked");
            await Until(() => Subscribed(first).Contains("homeassistant/#"), "the discovery prefix");
            rig.State.Connected.Should().BeTrue();

            foreach (var (topic, payload, _) in Fixture("z2m-discovery.jsonl")) await first.DeliverAsync(topic, payload);
            await Until(() => Subscribed(first).Contains("zigbee2mqtt/Dormitor senzor"), "a discovered state topic is subscribed at once");

            await first.DropAsync("broker oprit");
            await Until(() => second.IsConnected && Subscribed(second).Contains("zigbee2mqtt/Priza birou"), "after a drop: reconnect and re-subscribe everything");
            Subscribed(second).Should().Contain(new[] { "homeassistant/#", "zigbee2mqtt/bridge/state" });

            (await rig.State.RequestRepublish!(Ct)).Should().Contain("homeassistant/status");
            second.Published.Should().ContainSingle().Which.Should().Be(("homeassistant/status", "online", false));
        }
        finally
        {
            cts.Cancel();
            await rig.Worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task R3_RetainedStatesAtAReconnection_AddNoRows_AndAnUnavailableEntityIsNotStored()
    {
        using var rig = new Rig();
        await rig.FeedAsync("esphome-discovery.jsonl", "esphome-states.jsonl");
        var esp = await rig.Worker.AddAsync("aabbccddee04", Ct);
        var repo = rig.Repo<IGenericRepository>();
        async Task<int> Rows()
        {
            var n = 0;
            foreach (var e in await repo.ListEntitiesAsync(esp.Id, Ct)) n += (await repo.ListReadingsAsync(e.Id, rig.Now.AddDays(-1), rig.Now.AddDays(1), Ct)).Count;
            return n;
        }
        (await Rows()).Should().Be(0, "ESPHome's states are retained: what the broker kept is not a reading made now");

        rig.Now = rig.Now.AddHours(2);
        await rig.FeedAsync("esphome-states.jsonl");   // a reconnection: the same retained messages again
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        (await Rows()).Should().Be(0);

        await rig.Worker.ProcessAsync(new MqttMessage("senzor-co2/sensor/co2/state", "640", false), Ct);
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        (await Rows()).Should().Be(1, "a live message is a reading");

        rig.Now = rig.Now.AddMinutes(40);
        await rig.Worker.ProcessAsync(new MqttMessage("senzor-co2/status", "offline", true), Ct);
        await rig.Worker.ProcessAsync(new MqttMessage("senzor-co2/sensor/co2/state", "900", false), Ct);
        await rig.Recorder.FlushAsync(rig.Now, Ct);
        (await Rows()).Should().Be(1, "the availability says offline: shown, not stored");
    }

    [Fact]
    public async Task R7_AnOversizedMessage_IsAListedProblem()
    {
        using var rig = new Rig();
        await rig.Worker.ProcessAsync(new MqttMessage("homeassistant/sensor/x/y/config", "", true, Oversized: true), Ct);
        await rig.Worker.ProcessAsync(new MqttMessage("homeassistant/sensor/x/z/config", new string('a', MqttNetTransport.MaxPayloadBytes + 1), true), Ct);
        rig.State.Registry.Problems.Should().HaveCount(2).And.OnlyContain(p => p.Reason.StartsWith("mesaj prea mare", StringComparison.Ordinal));
        rig.State.Registry.Devices(rig.Now).Should().BeEmpty();
    }

    [Fact]
    public void Settings_Validation_InRomanian()
    {
        MqttSettingsService.Validate(new MqttSettingsUpdate(Host: "mqtt://x:1883", Port: 70000, DiscoveryPrefix: "home/#", RawRetentionDays: -1, MetricsSeriesLimit: 1))
            .Should().HaveCount(5);
        MqttSettingsService.Validate(new MqttSettingsUpdate(Host: "192.0.2.4", Port: 1883, DiscoveryPrefix: "homeassistant", RawRetentionDays: 90, MetricsSeriesLimit: 500))
            .Should().BeEmpty();
        MqttPassword.Load(new MqttSettingsDefaults(PasswordFile: "/nu/exista"), _ => null).Problem.Should().Contain("nu există");
        MqttPassword.Load(new MqttSettingsDefaults(), _ => "secret\n").Value.Should().Be("secret");
        MqttPassword.Load(new MqttSettingsDefaults(), _ => "secret\n").ToString().Should().NotContain("secret\n").And.Contain("set");
        // 0.12.0: the mounted file wins over the variable; a missing or empty file leaves the variable in charge.
        MqttPassword.Load(new MqttSettingsDefaults(Password: "from-env"), _ => " from-file \n").Value.Should().Be(" from-file ", "only the line end is removed");
        MqttPassword.Load(new MqttSettingsDefaults(Password: "from-env"), _ => null).Source.Should().Contain("KasaMonitor__MqttPassword");
        MqttPassword.Load(new MqttSettingsDefaults(Password: "from-env"), _ => "\n").Value.Should().Be("from-env");
    }

    private static List<string> Subscribed(FakeMqttTransport t)
    {
        lock (t.Subscribed) return t.Subscribed.ToList();
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("not reached: " + what);
            await Task.Delay(20);
        }
    }
}
