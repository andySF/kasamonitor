using FluentAssertions;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>
/// R1 (0.10.0): /settings saves per tab, each tab with a partial update. Saving tab X must leave every key of the
/// other tabs as stored, even when another tab changed it in between. The key sets mirror the tabs' save calls.
/// </summary>
public class SettingsTabSaveTests
{
    private static readonly string[] AppKeys =
    [
        SettingKeys.TariffRonPerKwh, SettingKeys.AutoDiscoveryEnabled, SettingKeys.AutoDiscoveryIntervalMinutes, SettingKeys.AutoAddDiscoveredPlugs,
        SettingKeys.ReadingsRetentionMonths, SettingKeys.LiveStreamEnabled, SettingKeys.LiveStreamIntervalSeconds, SettingKeys.LiveRawRetentionDays,
        SettingKeys.LiveBurstTriggerW, SettingKeys.NilmEnabled, SettingKeys.MinEventDeltaW, SettingKeys.ClusterWindowDays, SettingKeys.MlEnabled,
        SettingKeys.GrafanaUrl, HeatingSettingKeys.GasPricePerKwh, HeatingSettingKeys.GasUnit, HeatingSettingKeys.GasKwhPerM3,
    ];

    private static readonly string[] ExportKeys = [SettingKeys.RemoteWriteEnabled, SettingKeys.RemoteWriteUrl, SettingKeys.RemoteWriteUser, SettingKeys.RemoteWritePassword];
    private static readonly string[] TpLinkKeys = [SettingKeys.BroadcastNetworks, SettingKeys.TapoUsername, SettingKeys.TapoPassword];
    private static readonly string[] ShellyKeys = [SettingKeys.ShellyDiscoveryEnabled, SettingKeys.ShellyProbeHosts, SettingKeys.ShellyPassword, SettingKeys.AllowRelayControl];
    private static readonly string[] ViessmannKeys =
    [
        HeatingSettingKeys.HeatingEnabled, HeatingSettingKeys.ViessmannClientId, HeatingSettingKeys.ViessmannPollIntervalSeconds,
        HeatingSettingKeys.HeatingReadingsRetentionDays, HeatingSettingKeys.BoilerTimeZone,
    ];

    private sealed class Rig : IDisposable
    {
        private readonly TestDbFactory _db = new();
        public TestScopeFactory Scopes { get; }
        public KasaSettingsService Kasa { get; }
        public HeatingSettingsService Heating { get; }

        public Rig()
        {
            Scopes = new TestScopeFactory(_db.CreateContext);
            Kasa = TestSettings.Create(Scopes);
            Heating = new HeatingSettingsService(Scopes, new HeatingSettingsDefaults(), new FakeSecretProtector());
        }

        public async Task<IReadOnlyDictionary<string, string>> RowsAsync()
        {
            using var ctx = _db.CreateContext();
            return await new SettingsRepository(ctx).GetAllAsync(CancellationToken.None);
        }

        /// <summary>Every tab saved once, so every key has a stored value.</summary>
        public async Task SeedAsync()
        {
            await Kasa.SaveAsync(App(1.30m, 5, nilm: true), CancellationToken.None);
            await Heating.SaveAsync(new HeatingSettingsUpdate(GasPricePerKwh: 0.31m, GasUnit: "m3", GasKwhPerM3: 10.55), CancellationToken.None);
            await Kasa.SaveAsync(new KasaSettingsUpdate(RemoteWriteEnabled: true, RemoteWriteUrl: "http://p.example:9090/api/v1/write",
                NewRemoteWriteUser: "rw", NewRemoteWritePassword: "rw-pw"), CancellationToken.None);
            await Kasa.SaveAsync(new KasaSettingsUpdate(BroadcastNetworks: "192.168.1.255", TapoUsername: "a@example.com", NewTapoPassword: "tapo-pw"), CancellationToken.None);
            await Kasa.SaveAsync(new KasaSettingsUpdate(ShellyDiscoveryEnabled: true, ShellyProbeHosts: "10.0.0.5", NewShellyPassword: "sh-pw",
                AllowRelayControl: true), CancellationToken.None);
            await Heating.SaveAsync(new HeatingSettingsUpdate(HeatingEnabled: true, ClientId: "client-1", PollIntervalSeconds: 120,
                ReadingsRetentionDays: 0, BoilerTimeZone: "Europe/Berlin"), CancellationToken.None);
        }

        public void Dispose() => _db.Dispose();
    }

    private static KasaSettingsUpdate App(decimal tariff, int interval, bool nilm) => new(
        AutoDiscoveryEnabled: true, AutoDiscoveryIntervalMinutes: interval, AutoAddDiscoveredPlugs: true, ReadingsRetentionMonths: 0,
        LiveStreamEnabled: true, LiveStreamIntervalSeconds: 1, LiveRawRetentionDays: 30, LiveBurstTriggerW: 50, NilmEnabled: nilm,
        MinEventDeltaW: 30, ClusterWindowDays: 30, MlEnabled: true, GrafanaUrl: "", TariffRonPerKwh: tariff);

    private static void OnlyTheseChanged(IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after, string[] tabKeys)
    {
        foreach (var (key, value) in before.Where(kv => !tabKeys.Contains(kv.Key)))
            after.Should().ContainKey(key).WhoseValue.Should().Be(value, $"„{key}” belongs to another tab");
        after.Keys.Except(before.Keys).Should().BeSubsetOf(tabKeys, "a tab adds only its own keys");
    }

    [Fact]
    public async Task SavingTheAppTab_LeavesTheTpLinkShellyExportAndViessmannKeys()
    {
        using var rig = new Rig();
        await rig.SeedAsync();
        var before = await rig.RowsAsync();

        await rig.Kasa.SaveAsync(App(1.55m, 9, nilm: false), CancellationToken.None);
        await rig.Heating.SaveAsync(new HeatingSettingsUpdate(GasPricePerKwh: 0.40m, GasUnit: "kWh", GasKwhPerM3: 10.6), CancellationToken.None);
        var after = await rig.RowsAsync();

        OnlyTheseChanged(before, after, AppKeys);
        after[SettingKeys.TariffRonPerKwh].Should().Be("1.55");
        after[SettingKeys.NilmEnabled].Should().Be("false");
        after[HeatingSettingKeys.GasPricePerKwh].Should().Be("0.40");
        (await rig.Kasa.GetAsync(CancellationToken.None)).TapoCredentials!.Password.Should().Be("tapo-pw");
    }

    [Fact]
    public async Task SavingTheTpLinkTab_AfterTheAppTabChangedDiscovery_KeepsTheNewDiscoveryValues()
    {
        using var rig = new Rig();
        await rig.SeedAsync();
        // „Aplicație” saved in another browser tab: discovery off, interval 30.
        await rig.Kasa.SaveAsync(new KasaSettingsUpdate(AutoDiscoveryEnabled: false, AutoDiscoveryIntervalMinutes: 30), CancellationToken.None);
        var before = await rig.RowsAsync();

        await rig.Kasa.SaveAsync(new KasaSettingsUpdate(BroadcastNetworks: "10.0.0.255", TapoUsername: "b@example.com", NewTapoPassword: ""), CancellationToken.None);
        var after = await rig.RowsAsync();

        OnlyTheseChanged(before, after, TpLinkKeys);
        var s = await rig.Kasa.GetAsync(CancellationToken.None);
        s.AutoDiscoveryEnabled.Should().BeFalse();
        s.AutoDiscoveryIntervalMinutes.Should().Be(30);
        s.TapoCredentials.Should().Be(new KasaMonitor.Core.Models.TapoCredentials("b@example.com", "tapo-pw"), "an empty password keeps the stored one");
    }

    [Fact]
    public async Task SavingTheShellyAndExportTabs_TouchOnlyTheirKeys()
    {
        using var rig = new Rig();
        await rig.SeedAsync();

        var before = await rig.RowsAsync();
        await rig.Kasa.SaveAsync(new KasaSettingsUpdate(ShellyDiscoveryEnabled: false, ShellyProbeHosts: "", ClearShellyPassword: true,
            AllowRelayControl: false), CancellationToken.None);
        var afterShelly = await rig.RowsAsync();
        OnlyTheseChanged(before, afterShelly, ShellyKeys);

        await rig.Kasa.SaveAsync(new KasaSettingsUpdate(RemoteWriteEnabled: false, RemoteWriteUrl: "", ClearRemoteWriteAuth: true), CancellationToken.None);
        OnlyTheseChanged(afterShelly, await rig.RowsAsync(), ExportKeys);
    }

    [Fact]
    public async Task EnablingRemoteWrite_WithoutAnUrlInTheUpdate_IsCheckedAgainstTheStoredUrl()
    {
        using var rig = new Rig();
        var bad = () => rig.Kasa.SaveAsync(new KasaSettingsUpdate(RemoteWriteEnabled: true), CancellationToken.None);
        await bad.Should().ThrowAsync<FormatException>("no URL stored and none sent");
        await rig.Kasa.SaveAsync(new KasaSettingsUpdate(RemoteWriteUrl: "http://p.example:9090/api/v1/write"), CancellationToken.None);
        await rig.Kasa.SaveAsync(new KasaSettingsUpdate(RemoteWriteEnabled: true), CancellationToken.None);
        (await rig.Kasa.GetAsync(CancellationToken.None)).RemoteWriteEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ViessmannTabAndGasTariff_SaveWithoutOverwritingEachOther()
    {
        using var rig = new Rig();
        await rig.SeedAsync();

        var before = await rig.RowsAsync();
        await rig.Heating.SaveAsync(new HeatingSettingsUpdate(HeatingEnabled: false, ClientId: "client-1", PollIntervalSeconds: 300,
            ReadingsRetentionDays: 90, BoilerTimeZone: "Europe/Bucharest"), CancellationToken.None);
        var after = await rig.RowsAsync();
        OnlyTheseChanged(before, after, ViessmannKeys);

        var h = await rig.Heating.GetAsync(CancellationToken.None);
        h.GasPricePerKwh.Should().Be(0.31m);
        h.GasKwhPerM3.Should().Be(10.55);
        h.PollIntervalSeconds.Should().Be(300);

        await rig.Heating.SaveAsync(new HeatingSettingsUpdate(GasPricePerKwh: 0.5m), CancellationToken.None);
        h = await rig.Heating.GetAsync(CancellationToken.None);
        h.HeatingEnabled.Should().BeFalse("the gas tariff save leaves the boiler's switch");
        h.BoilerTimeZoneId.Should().Be("Europe/Bucharest");
    }

    [Fact]
    public async Task InvalidGas_IsRefusedByValidate_AndTheServiceWritesNothing()
    {
        using var rig = new Rig();
        await rig.SeedAsync();
        var before = await rig.RowsAsync();
        var bad = new HeatingSettingsUpdate(GasPricePerKwh: 99_999m, GasKwhPerM3: 10.55);

        HeatingSettingsService.Validate(bad).Should().Contain("Prețul gazului");
        HeatingSettingsService.Validate(new HeatingSettingsUpdate(GasKwhPerM3: 0.5)).Should().Contain("Factorul de conversie");
        HeatingSettingsService.Validate(new HeatingSettingsUpdate(GasPricePerKwh: 0.4m, GasKwhPerM3: 10.6)).Should().BeNull();
        var act = () => rig.Heating.SaveAsync(bad with { HeatingEnabled = false }, CancellationToken.None);
        await act.Should().ThrowAsync<FormatException>();
        (await rig.RowsAsync()).Should().BeEquivalentTo(before, "a refused update writes no key at all");
    }

    [Fact]
    public async Task TariffRonPerKwh_IsSavedThroughTheService_AndValidated()
    {
        using var rig = new Rig();
        await rig.Kasa.SaveAsync(new KasaSettingsUpdate(TariffRonPerKwh: 1.42m), CancellationToken.None);
        (await rig.Kasa.GetAsync(CancellationToken.None)).TariffRonPerKwh.Should().Be(1.42m);
        var bad = () => rig.Kasa.SaveAsync(new KasaSettingsUpdate(TariffRonPerKwh: 0m), CancellationToken.None);
        await bad.Should().ThrowAsync<FormatException>();
    }
}
