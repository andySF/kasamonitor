using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>/dispozitive (0.10.0): the projection of every device into one list, the filters and the uniform withdraw.</summary>
public class DeviceCatalogTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static Plug Plug(string mac, string alias, string protocol = PlugProtocols.Legacy, PlugRole role = PlugRole.Plug, bool archived = false,
        string? unsupported = null, int channel = 0) =>
        new(Guid.NewGuid(), mac, IPAddress.Parse("192.0.2.10"), alias, false, "KP115(EU)", "1.0", "1.0.12 Build 1", "", Now.AddDays(-30),
            Now.AddDays(-2), archived, null, protocol, unsupported, channel, role);

    /// <summary>Like the poller: a reading when online, none (but the attempt's time) when the poll failed.</summary>
    private static PlugLiveState Live(Plug p, bool online, DateTimeOffset sampleUtc) =>
        new(p.Id, p.Mac, p.Alias, online, online ? new EmeterReading(5_000, 230_000, 30, 100) : null, sampleUtc, p.Model, p.Protocol, p.Channel, p.Role);

    private static ClimateSensor Sensor(string source, string name, bool archived = false, bool outdoor = false, string room = "") =>
        new(Guid.NewGuid(), source, "ext-" + name, name, room, outdoor, archived, Now.AddMinutes(-5), null);

    private static IReadOnlyList<DeviceRow> Build(IReadOnlyList<Plug>? plugs = null, Dictionary<Guid, PlugLiveState>? live = null,
        IReadOnlyList<MieleDevice>? miele = null, MieleState? mieleState = null, IReadOnlyList<HeatingDevice>? heating = null,
        HeatingState? heatingState = null, IReadOnlyList<ClimateSensor>? sensors = null, ClimateState? climate = null) =>
        DeviceCatalogRules.Build(plugs ?? [], live ?? new(), miele ?? [], mieleState, heating ?? [], heatingState, sensors ?? [], climate, Now);

    [Fact]
    public void Plugs_OnlineFromTheLiveState_LastReadIsTheLastSample_NotTheDiscovery()
    {
        var online = Plug("aabbccddee00", "Frigider");
        var offline = Plug("aabbccddee01", "Uscător");
        var never = Plug("aabbccddee02", "Nou");
        var live = new Dictionary<Guid, PlugLiveState> { [online.Id] = Live(online, true, Now.AddSeconds(-8)), [offline.Id] = Live(offline, false, Now.AddHours(-3)) };

        var rows = Build([online, offline, never], live).ToDictionary(r => r.Key);

        var a = rows[DeviceRow.PlugKey(online.Id)];
        a.Health.Should().Be(DeviceHealth.Online);
        a.LastReadUtc.Should().Be(Now.AddSeconds(-8), "R3: the live state's last sample, not Plugs.LastSeenUtc (discovery)");
        a.Integration.Should().Be(IntegrationKeys.TpLink);
        a.Category.Should().Be(DeviceCategory.Plug);
        a.Model.Should().Be("KP115");
        a.Address.Should().Be("192.0.2.10");
        a.DataUrl.Should().Be($"/history?plug={online.Id}#curent");
        rows[DeviceRow.PlugKey(offline.Id)].Health.Should().Be(DeviceHealth.Offline);
        rows[DeviceRow.PlugKey(offline.Id)].LastReadUtc.Should().BeNull("a failed poll carries its time but no reading");
        rows[DeviceRow.PlugKey(never.Id)].Should().Match<DeviceRow>(r => r.Health == DeviceHealth.Offline && r.LastReadUtc == null);
    }

    [Fact]
    public void Plugs_Meter_Unsupported_Retired_AndTheShellyIntegration()
    {
        var main = Plug("aabbccddee10", "Casa", PlugProtocols.ShellyRpc, PlugRole.MainMeter);
        var sub = Plug("aabbccddee10", "Pompa", PlugProtocols.ShellyRpc, PlugRole.SubMeter, channel: 1);
        var tapo = Plug("aabbccddee11", "Tapo", PlugProtocols.Klap, unsupported: "lipsesc credențialele TP-Link");
        var gone = Plug("aabbccddee12", "Veche", archived: true);

        var rows = Build([main, sub, tapo, gone]).ToDictionary(r => r.Key);

        rows[DeviceRow.PlugKey(main.Id)].Should().Match<DeviceRow>(r => r.Category == DeviceCategory.Meter && r.Integration == IntegrationKeys.Shelly && r.DataUrl == "/contor");
        rows[DeviceRow.PlugKey(sub.Id)].Model.Should().EndWith("· ch1");
        rows[DeviceRow.PlugKey(tapo.Id)].Should().Match<DeviceRow>(r => r.Health == DeviceHealth.Unsupported && r.Note == "lipsesc credențialele TP-Link");
        rows[DeviceRow.PlugKey(gone.Id)].Should().Match<DeviceRow>(r => r.Health == DeviceHealth.Retired && r.IsArchived);
        rows[DeviceRow.PlugKey(main.Id)].RetireWarning.Should().Contain("contorul general", "withdrawing the main meter asks first");
        rows[DeviceRow.PlugKey(sub.Id)].RetireWarning.Should().BeNull();
        rows[DeviceRow.PlugKey(tapo.Id)].RetireWarning.Should().BeNull();
        Build([main, sub, tapo, gone]).Last().Key.Should().Be(DeviceRow.PlugKey(gone.Id), "retired rows go last");
    }

    [Fact]
    public void ClimateSensors_ArchivedIsRetired_FreshnessGivesLate_OpenMeteoIsWeather()
    {
        var climate = new ClimateState(clock: () => Now);
        var fresh = Sensor(ClimateSources.Govee, "Baie", room: "Baie");
        var late = Sensor(ClimateSources.Govee, "Dormitor");
        var archived = Sensor(ClimateSources.Shelly, "Camera tehnică", archived: true);
        var outdoor = Sensor(ClimateSources.OpenMeteo, "Afară", outdoor: true, room: "Afară");
        climate.Publish(new ClimateSensorLive(fresh, 22.5, 50, null, Now.AddMinutes(-5)));
        climate.Publish(new ClimateSensorLive(late, 21, 48, null, Now.AddMinutes(-40)));   // Govee every 10 min: 40 min = late, not stale
        climate.Publish(new ClimateSensorLive(outdoor, 7, 80, null, Now.AddMinutes(-10)));

        var rows = Build(sensors: [fresh, late, archived, outdoor], climate: climate).ToDictionary(r => r.Key);

        rows[DeviceRow.ClimateKey(fresh.Id)].Should().Match<DeviceRow>(r => r.Health == DeviceHealth.Online && r.Room == "Baie"
            && r.Integration == IntegrationKeys.Govee && r.Category == DeviceCategory.ClimateSensor && r.LastReadUtc == Now.AddMinutes(-5));
        rows[DeviceRow.ClimateKey(late.Id)].Health.Should().Be(DeviceHealth.Late);
        rows[DeviceRow.ClimateKey(archived.Id)].Should().Match<DeviceRow>(r => r.Health == DeviceHealth.Retired && r.IsArchived);
        rows[DeviceRow.ClimateKey(outdoor.Id)].Should().Match<DeviceRow>(r => r.Category == DeviceCategory.Weather && r.Integration == IntegrationKeys.OpenMeteo
            && r.Room == "Afară");
    }

    [Fact]
    public void Miele_OnlineFromItsState_LinkedPlugOnBothRows_ArchivedIsRetired()
    {
        var plug = Plug("aabbccddee20", "Priza mașină");
        var washer = new MieleDevice("000123", 1, "Mașină de spălat", "WWD660", "Washer", "Mașina", plug.Id, false, Now.AddMinutes(-1));
        var dryer = new MieleDevice("000124", 2, "Uscător", "TWD360", "Dryer", "Dryer", null, true, Now.AddDays(-9));
        var state = new MieleState();
        state.SetStream(true, MieleStreamMode.Sse, Now);
        state.Publish(new MieleApplianceLive(washer, Values(1), Now.AddMinutes(-1), null, null, 4));

        var rows = Build([plug], miele: [washer, dryer], mieleState: state).ToDictionary(r => r.Key);

        rows[DeviceRow.MieleKey("000123")].Should().Match<DeviceRow>(r => r.Health == DeviceHealth.Online && r.LinkedTo == "priza Priza mașină"
            && r.NameIsCustom && r.Category == DeviceCategory.Appliance && r.DataUrl == "/aparate");
        rows[DeviceRow.PlugKey(plug.Id)].LinkedTo.Should().Be("alimentează Mașina");
        rows[DeviceRow.MieleKey("000124")].Health.Should().Be(DeviceHealth.Retired);
    }

    private static MieleStateValues Values(int status) => new(status, "Pornit", null, null, null, null, null, null, null, null, null, null, null, null,
        null, null, null, null, null, null, null, null, null, null);

    [Fact]
    public void Boiler_DisabledIsRetired_OnlineFromHeatingState()
    {
        var device = new HeatingDevice(Guid.NewGuid(), "viessmann", "1", "g", "0", "E3_Vitodens_100", "Centrală", true, Now, null);
        var off = device with { Id = Guid.NewGuid(), Enabled = false };
        var state = new HeatingState { PollIntervalSeconds = 120 };
        state.Publish(new HeatingLive(device, null, null, Now.AddSeconds(-60), null, true));

        var rows = Build(heating: [device, off], heatingState: state).ToDictionary(r => r.Key);

        rows[DeviceRow.HeatingKey(device.Id)].Should().Match<DeviceRow>(r => r.Category == DeviceCategory.Boiler && r.Integration == IntegrationKeys.Viessmann
            && r.Model == "E3_Vitodens_100" && r.DataUrl == "/centrala");
        rows[DeviceRow.HeatingKey(device.Id)].Health.Should().Be(DeviceHealth.Late, "online but no snapshot: the values are not the present");
        rows[DeviceRow.HeatingKey(off.Id)].Health.Should().Be(DeviceHealth.Retired);
        rows[DeviceRow.HeatingKey(device.Id)].RetireWarning.Should().NotBeNullOrEmpty("withdrawing the boiler asks first");
        Build(heating: [device], heatingState: new HeatingState()).Single().Health.Should().Be(DeviceHealth.Offline, "nothing read yet");
    }

    [Fact]
    public void Filters_ByCategoryVendorState_RetiredOnlyOnRequest()
    {
        var climate = new ClimateState(clock: () => Now);
        var plug = Plug("aabbccddee30", "Frigider");
        var meter = Plug("aabbccddee31", "Casa", PlugProtocols.ShellyRpc, PlugRole.MainMeter);
        var gone = Plug("aabbccddee32", "Veche", archived: true);
        var sensor = Sensor(ClimateSources.Shelly, "Hol");
        var rows = Build([plug, meter, gone], new() { [plug.Id] = Live(plug, true, Now) }, sensors: [sensor], climate: climate);

        new DeviceFilter().Apply(rows).Should().HaveCount(3, "retired are hidden by default");
        new DeviceFilter(ShowRetired: true).Apply(rows).Should().HaveCount(4);
        new DeviceFilter(Category: DeviceCategory.Meter).Apply(rows).Single().Name.Should().Be("Casa");
        new DeviceFilter(Integration: IntegrationKeys.Shelly).Apply(rows).Select(r => r.Name).Should().BeEquivalentTo("Casa", "Hol");
        new DeviceFilter(Health: DeviceHealth.Online).Apply(rows).Single().Name.Should().Be("Frigider");
        new DeviceFilter(Health: DeviceHealth.Retired).Apply(rows).Single().Name.Should().Be("Veche", "asking for retired shows them");
        DeviceHealths.FromSlug("intarziat").Should().Be(DeviceHealth.Late);
        DeviceCategories.FromSlug("climatesensor").Should().Be(DeviceCategory.ClimateSensor);
        DeviceHealths.All.Select(DeviceHealths.Name).Should().Equal("Online", "Întârziat", "Offline", "Nesuportat", "Retras");
    }

    private sealed class Rig : IDisposable
    {
        private readonly TestDbFactory _db = new();
        public TestScopeFactory Scopes { get; }
        public ClimateState Climate { get; } = new();
        public HeatingState Heating { get; } = new();
        public MieleState Miele { get; } = new();
        public ClimateRecorder ClimateRecorder { get; }
        public ClimateSettingsService ClimateSettings { get; }
        public DeviceCatalog Catalog { get; }
        public int HeatingWakes;

        public Rig()
        {
            Scopes = new TestScopeFactory(_db.CreateContext);
            ClimateRecorder = new ClimateRecorder(Scopes, Climate);
            ClimateSettings = new ClimateSettingsService(Scopes, new ClimateSettingsDefaults(), new GoveeApiKey("k", "test", null));
            Catalog = new DeviceCatalog(Scopes, new PlugStateBroadcaster(), Climate, Heating, Miele, ClimateRecorder,
                new MieleRecorder(Scopes, Miele, NullLogger<MieleRecorder>.Instance), ClimateSettings, wakeHeating: () => HeatingWakes++);
        }

        public T Repo<T>() where T : class => (T)Scopes.CreateScope().ServiceProvider.GetService(typeof(T))!;

        public void Dispose() => _db.Dispose();
    }

    [Fact]
    public async Task Retire_AndBringBack_GoThroughEachOwner()
    {
        using var rig = new Rig();
        var plugId = await rig.Repo<IPlugRepository>().UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.40"),
            new PlugInfo("aabbccddee40", "Frigider", "KP115(EU)", "1.0", "1.0", "", true, -50, TimeSpan.Zero)), Now, Ct);
        await rig.Repo<IMieleRepository>().UpsertDeviceAsync("000777", null, Now, Ct);
        var boiler = await rig.Repo<IHeatingRepository>().UpsertDeviceAsync(new HeatingDevice(Guid.Empty, "viessmann", "1", "g", "0", "Vitodens", "Centrală", true, Now, null), Ct);
        rig.Heating.Publish(new HeatingLive(boiler, null, null, Now, null, true));
        await rig.ClimateRecorder.RecordAsync(new ClimateSample(ClimateSources.Shelly, "aabbccddee41:0", "Hol", Now, 21, 40), Ct);
        await rig.ClimateSettings.SetGoveeHomeAsync("AA:BB:CC:DD:EE:FF:00:42", true, Ct);
        await rig.ClimateRecorder.RecordAsync(new ClimateSample(ClimateSources.Govee, "AA:BB:CC:DD:EE:FF:00:42", "Baie", Now, 23, 55), Ct);
        var sensors = await rig.Repo<IClimateRepository>().ListSensorsAsync(true, Ct);
        var hol = sensors.Single(s => s.Name == "Hol");
        var baie = sensors.Single(s => s.Name == "Baie");

        foreach (var key in new[] { DeviceRow.PlugKey(plugId), DeviceRow.MieleKey("000777"), DeviceRow.HeatingKey(boiler.Id), DeviceRow.ClimateKey(hol.Id), DeviceRow.ClimateKey(baie.Id) })
            (await rig.Catalog.SetRetiredAsync(key, true, Ct)).Should().NotBe("Dispozitiv necunoscut.");

        (await rig.Repo<IPlugRepository>().ListAllAsync(Ct)).Single().IsArchived.Should().BeTrue();
        (await rig.Repo<IMieleRepository>().ListDevicesAsync(true, Ct)).Single().IsArchived.Should().BeTrue();
        (await rig.Repo<IHeatingRepository>().ListDevicesAsync(Ct)).Single().Enabled.Should().BeFalse();
        rig.Heating.Current!.Device.Enabled.Should().BeFalse("the poll worker reads the device from the state");
        rig.HeatingWakes.Should().Be(1);
        (await rig.Repo<IClimateRepository>().ListSensorsAsync(true, Ct)).Should().OnlyContain(s => s.IsArchived);
        rig.Climate.Sensors.Should().BeEmpty("archived sensors leave /clima");
        (await rig.ClimateSettings.GetAsync(Ct)).GoveeHomeDevices.Should().BeEmpty("a Govee thermometer is withdrawn by unticking it, which the worker follows");

        var rows = await rig.Catalog.ListAsync(Now, Ct);
        rows.Should().HaveCount(5).And.OnlyContain(r => r.Health == DeviceHealth.Retired);

        await rig.Catalog.SetRetiredAsync(DeviceRow.ClimateKey(baie.Id), false, Ct);
        await rig.Catalog.SetRetiredAsync(DeviceRow.HeatingKey(boiler.Id), false, Ct);
        (await rig.ClimateSettings.GetAsync(Ct)).GoveeHomeDevices.Should().Contain("AA:BB:CC:DD:EE:FF:00:42");
        rig.Climate.Sensors.Should().ContainSingle(s => s.Sensor.Name == "Baie");
        (await rig.Repo<IHeatingRepository>().ListDevicesAsync(Ct)).Single().Enabled.Should().BeTrue();
        (await rig.Catalog.SetRetiredAsync("nimic:1", true, Ct)).Should().Be("Dispozitiv necunoscut.");
    }

    [Fact]
    public async Task ShellyClimateSensorsStoredAsPlugs_AreArchivedOnce_AndABringBackSticks()
    {
        using var rig = new Rig();
        var plugs = rig.Repo<IPlugRepository>();
        var info = new PlugInfo("aabbccddee50", "shellyhtg3", "S3SN-0U12A", "HTG3", "1.4", "", false, 0, TimeSpan.Zero);
        var ht = await plugs.UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.50"), info, PlugProtocols.ShellyRpc, ShellySupport.NoMeterReason), Now, Ct);
        var otherNoMeter = await plugs.UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.0.2.51"), info with { Mac = "aabbccddee51" },
            PlugProtocols.ShellyRpc, ShellySupport.NoMeterReason), Now, Ct);
        await rig.ClimateRecorder.RecordAsync(new ClimateSample(ClimateSources.Shelly, ShellyClimateParser.ExternalId("aabbccddee50", 0), "Camera tehnică", Now, 22, 48), Ct);
        var discovery = new PlugDiscoveryService(new FakeKasaClient(), new FakeTapoClient(), rig.Scopes, TestSettings.Create(rig.Scopes),
            new PlugDiscoveredChannel(), TimeSpan.FromMilliseconds(50), NullLogger<PlugDiscoveryService>.Instance);

        (await discovery.ArchiveShellyClimatePlugsOnceAsync(Ct)).Should().Be(1);
        var all = await rig.Repo<IPlugRepository>().ListAllAsync(Ct);
        all.Single(p => p.Id == ht).IsArchived.Should().BeTrue();
        all.Single(p => p.Id == otherNoMeter).IsArchived.Should().BeFalse("no climate sensor with that MAC: not „de acest fel”");

        await rig.Catalog.SetRetiredAsync(DeviceRow.PlugKey(ht), false, Ct);
        (await discovery.ArchiveShellyClimatePlugsOnceAsync(Ct)).Should().Be(0, "only once");
        (await rig.Repo<IPlugRepository>().ListAllAsync(Ct)).Single(p => p.Id == ht).IsArchived.Should().BeFalse();
    }
}
