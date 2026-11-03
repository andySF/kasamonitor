using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Protocol.Shelly;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

internal sealed class FakeGoveeClient : IGoveeClient
{
    public List<GoveeDevice> Devices { get; } = new();
    public Dictionary<string, GoveeState> States { get; } = new();
    public List<string> StateRequests { get; } = new();
    public int ListCalls;
    public Exception? StateError { get; set; }
    public string? LastKey { get; private set; }

    public Task<IReadOnlyList<GoveeDevice>> ListDevicesAsync(string apiKey, CancellationToken ct)
    {
        LastKey = apiKey;
        ListCalls++;
        return Task.FromResult<IReadOnlyList<GoveeDevice>>(Devices.ToList());
    }

    public Task<GoveeState> GetStateAsync(string apiKey, string sku, string device, CancellationToken ct)
    {
        LastKey = apiKey;
        StateRequests.Add(device);
        if (StateError is not null) throw StateError;
        return Task.FromResult(States[device]);
    }
}

internal sealed class FakeOpenMeteoClient : IOpenMeteoClient
{
    public List<(double Lat, double Lon)> Requests { get; } = new();
    public OpenMeteoCurrent Current { get; set; } = new(null, 9.4, 78);

    public Task<OpenMeteoCurrent> GetCurrentAsync(double latitude, double longitude, CancellationToken ct)
    {
        Requests.Add((latitude, longitude));
        return Task.FromResult(Current);
    }
}

/// <summary>The climate section on a real (in-memory) database: recorder, the four sources, settings, retention.</summary>
public sealed class ClimateTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Baie = "AA:BB:CC:DD:EE:FF:00:11";
    private const string Client = "AA:BB:CC:DD:EE:FF:00:22";

    private readonly TestDbFactory _db = new();
    private readonly TestScopeFactory _scopes;
    private readonly ClimateState _state = new();
    private readonly ClimateRecorder _recorder;
    private readonly FakeGoveeClient _govee = new();
    private readonly FakeOpenMeteoClient _meteo = new();
    private readonly FakeShellyClient _shelly = new();
    private DateTimeOffset _now = T0;

    public ClimateTests()
    {
        _scopes = new TestScopeFactory(_db.CreateContext);
        _recorder = new ClimateRecorder(_scopes, _state);
    }

    public void Dispose() => _db.Dispose();

    private ClimateSettingsService Settings(string? key = "test-key") =>
        new(_scopes, new ClimateSettingsDefaults(), new GoveeApiKey(key, key is null ? null : "test", key is null ? "lipsă" : null));

    private ClimateWorker Worker(ClimateSettingsService? settings = null) =>
        new(_scopes, settings ?? Settings(), _recorder, _state, _govee, _meteo, NullLogger<ClimateWorker>.Instance,
            kasa: TestSettings.Create(_scopes), shelly: _shelly, clock: () => _now);

    private async Task<IReadOnlyList<ClimateReading>> ReadingsAsync(Guid sensorId)
    {
        using var scope = _scopes.CreateScope();
        var repo = (IClimateRepository)scope.ServiceProvider.GetService(typeof(IClimateRepository))!;
        return (await repo.ListReadingsAsync(new[] { sensorId }, T0.AddYears(-2), T0.AddYears(1), Ct))[sensorId];
    }

    private static ClimateSample Sample(DateTimeOffset ts, double? temp, double? rh = 50, bool online = true) =>
        new(ClimateSources.Govee, Baie, "Baie", ts, temp, rh, Online: online);

    [Fact]
    public async Task Recorder_WritesOnChangeOrHeartbeat_PublishesEveryRead()
    {
        (await _recorder.RecordAsync(Sample(T0, 21.0), Ct)).Should().Be(ClimateRecordResult.Stored);
        (await _recorder.RecordAsync(Sample(T0.AddMinutes(10), 21.05), Ct)).Should().Be(ClimateRecordResult.Unchanged);
        (await _recorder.RecordAsync(Sample(T0.AddMinutes(20), 21.1), Ct)).Should().Be(ClimateRecordResult.Stored);
        (await _recorder.RecordAsync(Sample(T0.AddMinutes(30), 21.1, 50.5), Ct)).Should().Be(ClimateRecordResult.Unchanged);
        (await _recorder.RecordAsync(Sample(T0.AddMinutes(40), 21.1, 51.5), Ct)).Should().Be(ClimateRecordResult.Stored);
        (await _recorder.RecordAsync(Sample(T0.AddMinutes(69), 21.1, 51.5), Ct)).Should().Be(ClimateRecordResult.Unchanged);
        (await _recorder.RecordAsync(Sample(T0.AddMinutes(70), 21.1, 51.5), Ct)).Should().Be(ClimateRecordResult.Stored);   // heartbeat

        var sensor = _state.Sensors.Single();
        sensor.Sensor.Name.Should().Be("Baie");
        sensor.TemperatureC.Should().Be(21.1);
        sensor.ReadUtc.Should().Be(T0.AddMinutes(70));
        (await ReadingsAsync(sensor.Sensor.Id)).Select(r => (r.TsUtc - T0).TotalMinutes).Should().Equal(0, 20, 40, 70);
    }

    [Fact]
    public async Task Recorder_Offline_StoresNothing_KeepsTheLastValue_RestartDoesNotDuplicate()
    {
        await _recorder.RecordAsync(Sample(T0, 21.0), Ct);
        (await _recorder.RecordAsync(Sample(T0.AddMinutes(10), null, null, online: false), Ct)).Should().Be(ClimateRecordResult.Offline);
        var live = _state.Sensors.Single();
        live.SourceOnline.Should().BeFalse();
        live.TemperatureC.Should().Be(21.0);
        _state.IsOnline(live, T0.AddMinutes(10)).Should().BeFalse();

        // A new process: the last stored reading is loaded, an unchanged value is not stored again.
        var state2 = new ClimateState();
        var recorder2 = new ClimateRecorder(_scopes, state2);
        await recorder2.LoadAsync(Ct);
        state2.Sensors.Single().TemperatureC.Should().Be(21.0);
        (await recorder2.RecordAsync(Sample(T0.AddMinutes(20), 21.0), Ct)).Should().Be(ClimateRecordResult.Unchanged);
        (await ReadingsAsync(live.Sensor.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Recorder_EditAndArchive_ArchivedIsHiddenAndNotStored()
    {
        await _recorder.RecordAsync(Sample(T0, 21.0), Ct);
        var id = _state.Sensors.Single().Sensor.Id;

        await _recorder.UpdateSensorAsync(id, "Baie mare", "Baie", false, Ct);
        _state.Sensors.Single().Sensor.Name.Should().Be("Baie mare");
        _state.Sensors.Single().TemperatureC.Should().Be(21.0);

        await _recorder.SetArchivedAsync(id, true, Ct);
        _state.Sensors.Should().BeEmpty();
        (await _recorder.RecordAsync(Sample(T0.AddHours(1), 25.0), Ct)).Should().Be(ClimateRecordResult.Archived);
        (await ReadingsAsync(id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Viessmann_ReadingsBecomeTheRoomSensorOnly_OnChange_ThenOnlyNewReadings()
    {
        HeatingDevice device;
        using (var scope = _scopes.CreateScope())
        {
            var heating = (IHeatingRepository)scope.ServiceProvider.GetService(typeof(IHeatingRepository))!;
            device = await heating.UpsertDeviceAsync(new HeatingDevice(Guid.NewGuid(), "viessmann", "1", "g", "0", "E3", "Vitodens", true, null, null), Ct);
            for (var i = 0; i < 5; i++)
                await heating.SaveReadingAsync(new HeatingReading(device.Id, T0.AddMinutes(-10 + 2 * i), null, null, 40 + i, null, null, 21.0, 48.0,
                    null, null, null, null, null, null, null, null, null, null, null, null), Ct);
        }

        (await Worker().ViessmannAsync(T0, Ct)).Should().Be(5);

        var sensors = _state.Sensors.ToDictionary(s => s.Sensor.Name);
        sensors.Keys.Should().Equal("Termostat (cameră)");   // supply and hot water are water, not climate (0.9.3)
        (await ReadingsAsync(sensors["Termostat (cameră)"].Sensor.Id)).Should().ContainSingle(); // never changed
        sensors["Termostat (cameră)"].Sensor.ExternalId.Should().Be(device.Id + ":room");

        var worker = Worker();
        (await worker.ViessmannAsync(T0.AddMinutes(1), Ct)).Should().Be(0);   // cursor from the sensors' last-seen time
        using (var scope = _scopes.CreateScope())
            await ((IHeatingRepository)scope.ServiceProvider.GetService(typeof(IHeatingRepository))!).SaveReadingAsync(new HeatingReading(device.Id,
                T0.AddMinutes(2), null, null, 50, null, null, 21.5, 48.0, null, null, null, null, null, null, null, null, null, null, null, null), Ct);
        (await worker.ViessmannAsync(T0.AddMinutes(3), Ct)).Should().Be(1);
        _state.Sensors.Single(s => s.Sensor.Name == "Termostat (cameră)").TemperatureC.Should().Be(21.5);
    }

    [Fact]
    public async Task Load_RemovesTheFormerBoilerWaterSensors_WithReadingsAndDays_Once()
    {
        var boiler = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var day = new DateOnly(2026, 11, 1);
        var stats = new ClimateStats(20, 21, 22, null, null, null, 3);
        Guid supply, dhw, room, govee, outdoor;
        using (var scope = _scopes.CreateScope())
        {
            var repo = (IClimateRepository)scope.ServiceProvider.GetService(typeof(IClimateRepository))!;
            async Task<Guid> Seed(string source, string externalId, string name)
            {
                var s = await repo.GetOrAddSensorAsync(source, externalId, name, "", false, Ct);
                await repo.AddReadingAsync(new ClimateReading(s.Id, T0, 21, null, null), Ct);
                await repo.AddReadingAsync(new ClimateReading(s.Id, T0.AddMinutes(30), 22, null, null), Ct);
                await repo.UpsertDailyAsync(new[] { new ClimateDayStats(s.Id, day, stats) }, Ct);
                return s.Id;
            }
            supply = await Seed(ClimateSources.Viessmann, ViessmannClimate.ExternalId(boiler, ViessmannClimate.SupplySuffix), ViessmannClimate.SupplyName);
            dhw = await Seed(ClimateSources.Viessmann, ViessmannClimate.ExternalId(boiler, ViessmannClimate.DhwSuffix), ViessmannClimate.DhwName);
            room = await Seed(ClimateSources.Viessmann, ViessmannClimate.ExternalId(boiler, ViessmannClimate.RoomSuffix), ViessmannClimate.RoomName);
            govee = await Seed(ClimateSources.Govee, Baie, "Baie");
            outdoor = await Seed(ClimateSources.OpenMeteo, OpenMeteoApi.OutdoorExternalId, "Afară");
        }

        await _recorder.LoadAsync(Ct);

        var kept = new[] { room, govee, outdoor };
        _state.Sensors.Select(s => s.Sensor.Id).Should().BeEquivalentTo(kept);
        _recorder.SensorsOf(ClimateSources.Viessmann).Select(s => s.Id).Should().Equal(room);
        async Task<(IReadOnlyList<ClimateSensor> Sensors, IReadOnlyCollection<Guid> ReadingSensors, IReadOnlyList<ClimateDayStats> Days)> DbAsync()
        {
            using var scope = _scopes.CreateScope();
            var repo = (IClimateRepository)scope.ServiceProvider.GetService(typeof(IClimateRepository))!;
            var all = new[] { supply, dhw, room, govee, outdoor };
            var readings = await repo.ListReadingsAsync(all, T0.AddDays(-1), T0.AddDays(1), Ct);
            return (await repo.ListSensorsAsync(true, Ct), readings.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).ToList(),
                await repo.ListDailyAsync(all, day, day, Ct));
        }
        var after = await DbAsync();
        after.Sensors.Select(s => s.Id).Should().BeEquivalentTo(kept);
        after.ReadingSensors.Should().BeEquivalentTo(kept);
        after.Days.Select(d => d.SensorId).Should().BeEquivalentTo(kept);
        (await ReadingsAsync(room)).Should().HaveCount(2);

        // A second start: nothing left to delete, nothing else touched.
        var recorder2 = new ClimateRecorder(_scopes, new ClimateState());
        await recorder2.LoadAsync(Ct);
        var again = await DbAsync();
        again.Sensors.Select(s => s.Id).Should().BeEquivalentTo(kept);
        again.ReadingSensors.Should().BeEquivalentTo(kept);
        again.Days.Should().HaveCount(3);
        (await ReadingsAsync(govee)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Govee_OnlyTickedThermometers_AreRead_KeyIsUsed_OfflineStoresNothing_UntickArchives()
    {
        _govee.Devices.AddRange(GoveeParser.ParseDevices(FakeShellyClient.Fixture("govee_devices.json")));
        _govee.States[Baie] = new GoveeState(true, 25.7, 56.7, null);
        _govee.States[Client] = new GoveeState(false, null, null, null);
        var settings = Settings();
        var worker = Worker(settings);

        await worker.GoveeAsync(T0, Ct);
        _govee.ListCalls.Should().Be(1);
        _govee.StateRequests.Should().BeEmpty();   // nothing ticked yet
        _state.GoveeDevices.Should().HaveCount(3);
        _state.Sensors.Should().BeEmpty();

        await settings.SetGoveeHomeAsync(Baie, true, Ct);
        await settings.SetGoveeHomeAsync(Client, true, Ct);
        await worker.GoveeAsync(T0.AddMinutes(10), Ct);
        _govee.ListCalls.Should().Be(1);   // the list is re-read only every 6 h
        _govee.StateRequests.Should().Equal(Baie, Client);
        _govee.LastKey.Should().Be("test-key");
        _state.GoveeCalls.Today.Should().BeGreaterThan(0);
        var baie = _state.Sensors.Single(s => s.Sensor.Name == "Baie");
        baie.TemperatureC.Should().Be(25.7);
        baie.HumidityPercent.Should().Be(56.7);
        var client = _state.Sensors.Single(s => s.Sensor.Name == "Client A");
        client.SourceOnline.Should().BeFalse();
        (await ReadingsAsync(client.Sensor.Id)).Should().BeEmpty();

        await settings.SetGoveeHomeAsync(Client, false, Ct);
        await worker.GoveeAsync(T0.AddMinutes(20), Ct);
        _state.Sensors.Select(s => s.Sensor.Name).Should().Equal("Baie");
        _govee.StateRequests.Should().Equal(Baie, Client, Baie);

        // Ticked again: shown and stored again in the same cycle, with its battery.
        await settings.SetGoveeHomeAsync(Client, true, Ct);
        _govee.States[Client] = new GoveeState(true, 19.5, 40, 55);
        await worker.GoveeAsync(T0.AddMinutes(30), Ct);
        var again = _state.Sensors.Single(s => s.Sensor.Name == "Client A");
        again.TemperatureC.Should().Be(19.5);
        again.BatteryPercent.Should().Be(55);
        (await ReadingsAsync(again.Sensor.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Govee_WithoutKey_CallsNothing_RateLimit_Pauses()
    {
        await Worker(Settings(key: null)).GoveeAsync(T0, Ct);
        _govee.ListCalls.Should().Be(0);

        _govee.Devices.AddRange(GoveeParser.ParseDevices(FakeShellyClient.Fixture("govee_devices.json")));
        var settings = Settings();
        await settings.SetGoveeHomeAsync(Baie, true, Ct);
        _govee.StateError = new GoveeRateLimitException("429");
        var worker = Worker(settings);
        await worker.GoveeAsync(T0, Ct);
        _state.SourceStatus(ClimateSources.Govee).Should().Contain("pauză");
        await worker.GoveeAsync(T0.AddMinutes(10), Ct);
        _govee.StateRequests.Should().ContainSingle();
        await worker.GoveeAsync(T0.AddMinutes(61), Ct);
        _govee.StateRequests.Should().HaveCount(2);
    }

    [Fact]
    public async Task OpenMeteo_OffWithoutLocation_ThenTheOutdoorSensor()
    {
        var settings = Settings();
        await Worker(settings).OpenMeteoAsync(T0, Ct);
        _meteo.Requests.Should().BeEmpty();

        await settings.SaveLocationAsync(44.4268, 26.1025, Ct);
        await Worker(settings).OpenMeteoAsync(T0, Ct);
        _meteo.Requests.Should().Equal((44.4268, 26.1025));
        var outdoor = _state.Outdoor(T0)!;
        outdoor.Sensor.Should().BeEquivalentTo(new { Name = "Afară", Room = "Afară", IsOutdoor = true, Source = ClimateSources.OpenMeteo });
        outdoor.TemperatureC.Should().Be(9.4);
        _state.OpenMeteoCalls.Today.Should().Be(1);
    }

    [Fact]
    public async Task Shelly_HtFoundByMdns_ReadWithBattery_MeterNotKept()
    {
        _shelly.Add("192.0.2.50", new FakeShellyClient.Device
        {
            DeviceInfo = FakeShellyClient.Fixture("shelly_htg3_deviceinfo.json"),
            Status = FakeShellyClient.Fixture("shelly_htg3_status.json"),
            Config = FakeShellyClient.Fixture("shelly_htg3_config.json"),
        });
        _shelly.Add("192.0.2.60");   // the EM Gen3 (no temperature components)
        _shelly.MdnsAnswers.Add(new ShellyMdnsAnswer("shellyhtg3-aabbccddee01", IPAddress.Parse("192.0.2.50"), 80, new Dictionary<string, string> { ["gen"] = "3" }));
        _shelly.MdnsAnswers.Add(new ShellyMdnsAnswer("shellyemg3-x", IPAddress.Parse("192.0.2.60"), 80, new Dictionary<string, string> { ["gen"] = "3" }));
        var worker = Worker();

        await worker.ShellyDiscoverAsync(T0, Ct);

        var ht = _state.Sensors.Single();
        ht.Sensor.Should().BeEquivalentTo(new { Name = "Camera tehnica", ExternalId = "aabbccddee01:0", Source = ClimateSources.Shelly, BatteryPercent = 87 });
        ht.TemperatureC.Should().Be(22.47);
        ht.HumidityPercent.Should().Be(48.4);
        ht.BatteryPowered.Should().BeTrue();
        _state.Expected(ht).Should().Be(ClimateFreshness.ShellyBatteryInterval);

        var infoCalls = _shelly.DeviceInfoCalls;
        await worker.ShellyDiscoverAsync(T0.AddMinutes(5), Ct);
        _shelly.DeviceInfoCalls.Should().Be(infoCalls);   // H&T known, EM re-checked only after an hour
        await worker.ShellyPollAsync(T0.AddMinutes(6), Ct);
        _state.Sensors.Single().ReadUtc.Should().Be(T0.AddMinutes(6));
    }

    [Fact]
    public async Task Retention_Unlimited_ReadingsOlderThanAYearStayAfterTheWorkerRuns()
    {
        // 0.9.2: the raw climate readings are kept without limit (the history of /history), the worker deletes nothing.
        await _recorder.RecordAsync(Sample(T0.AddDays(-400), 20), Ct);
        await _recorder.RecordAsync(Sample(T0, 21), Ct);
        var id = _state.Sensors.Single().Sensor.Id;
        var worker = Worker();
        await worker.RunDueAsync(Ct);
        _now = T0.AddDays(2);
        await worker.RunDueAsync(Ct);
        (await ReadingsAsync(id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task RunDue_ASourceFailing_DoesNotStopTheOthers()
    {
        var settings = Settings();
        await settings.SaveLocationAsync(44.4, 26.1, Ct);
        _govee.Devices.AddRange(GoveeParser.ParseDevices(FakeShellyClient.Fixture("govee_devices.json")));
        await settings.SetGoveeHomeAsync(Baie, true, Ct);
        _govee.StateError = new GoveeException("Govee state: HTTP 500", 500);

        await Worker(settings).RunDueAsync(Ct);

        _state.SourceStatus(ClimateSources.Govee).Should().Contain("HTTP 500");
        _state.Outdoor(T0)!.TemperatureC.Should().Be(9.4);
    }

    [Fact]
    public async Task Settings_Location_Validated_GoveeTicks_KeyNeverInTheDatabase()
    {
        var settings = Settings("very-secret-key");
        await settings.Invoking(s => s.SaveLocationAsync(44, null, Ct)).Should().ThrowAsync<ArgumentException>();
        await settings.Invoking(s => s.SaveLocationAsync(91, 26, Ct)).Should().ThrowAsync<ArgumentException>();
        await settings.Invoking(s => s.SaveLocationAsync(44, 181, Ct)).Should().ThrowAsync<ArgumentException>();
        await settings.SaveLocationAsync(44.42681234, 26.1025, Ct);
        await settings.SetGoveeHomeAsync(Baie, true, Ct);

        var s = await settings.GetAsync(Ct);
        s.Latitude.Should().Be(44.42681);
        s.HasLocation.Should().BeTrue();
        s.GoveeHomeDevices.Should().BeEquivalentTo(Baie);
        s.HasGoveeKey.Should().BeTrue();

        await settings.SaveLocationAsync(null, null, Ct);
        (await settings.GetAsync(Ct)).HasLocation.Should().BeFalse();

        using var scope = _scopes.CreateScope();
        var all = await ((ISettingsRepository)scope.ServiceProvider.GetService(typeof(ISettingsRepository))!).GetAllAsync(Ct);
        all.Values.Should().NotContain(v => v.Contains("very-secret-key"));
    }

    [Fact]
    public void GoveeKey_FromTheFileOrTheEnvironment_NeverInToString()
    {
        var fromEnv = GoveeApiKey.Load(new ClimateSettingsDefaults(GoveeApiKey: " k1 "), _ => null);
        fromEnv.Value.Should().Be("k1");
        fromEnv.Source.Should().Contain("KasaMonitor__GoveeApiKey");
        fromEnv.ToString().Should().NotContain("k1");

        // 0.12.0: a mounted file wins over the variable; an empty file leaves the variable in charge.
        var both = GoveeApiKey.Load(new ClimateSettingsDefaults(GoveeApiKey: "k1"), _ => "k2");
        both.Value.Should().Be("k2");
        both.Source.Should().StartWith("fișierul ");
        GoveeApiKey.Load(new ClimateSettingsDefaults(GoveeApiKey: "k1"), _ => " ").Value.Should().Be("k1");

        var fromFile = GoveeApiKey.Load(new ClimateSettingsDefaults(GoveeApiKeyFile: "/run/secrets/x.key"), p => p == "/run/secrets/x.key" ? "k2\n" : null);
        fromFile.Value.Should().Be("k2");
        fromFile.Source.Should().Be("fișierul /run/secrets/x.key");

        var missing = GoveeApiKey.Load(new ClimateSettingsDefaults(), _ => null);
        missing.IsSet.Should().BeFalse();
        missing.Problem.Should().Contain(GoveeApi.DefaultKeyFile);

        GoveeApiKey.Load(new ClimateSettingsDefaults(), _ => "  ").Problem.Should().Contain("e gol");
    }
}
