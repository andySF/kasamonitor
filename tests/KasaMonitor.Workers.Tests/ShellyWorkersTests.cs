using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>Shelly Gen2/Gen3 through discovery, polling, history and settings (fake device = captured EM Gen3 answers).</summary>
public class ShellyWorkersTests
{
    private const string EmIp = "192.168.254.141";
    private const string EmMac = "d885ac0b743c";
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    private sealed class Rig : IDisposable
    {
        public TestDbFactory Db { get; } = new();
        public TestScopeFactory Scopes { get; }
        public FakeShellyClient Shelly { get; } = new();
        public FakeKasaClient Kasa { get; } = new() { OnGetSysInfo = _ => Task.FromException<PlugInfo>(new TimeoutException("no 9999")) };
        public FakeTapoClient Tapo { get; } = new();
        public PlugDiscoveredChannel Channel { get; } = new();
        public KasaSettingsService Settings { get; }
        public PlugDiscoveryService Discovery { get; }

        public Rig(KasaSettingsDefaults? defaults = null)
        {
            Scopes = new TestScopeFactory(Db.CreateContext);
            Settings = TestSettings.Create(Scopes, defaults ?? new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255"));
            Discovery = new PlugDiscoveryService(Kasa, Tapo, Scopes, Settings, Channel, TimeSpan.FromMilliseconds(100),
                NullLogger<PlugDiscoveryService>.Instance, Shelly);
        }

        public PollingWorker Polling(PlugStateBroadcaster bc) => new(Kasa, Tapo, Scopes, bc, Settings, TimeSpan.FromSeconds(10),
            NullLogger<PollingWorker>.Instance, shelly: Shelly);

        public HistorySyncWorker History(DateTime nowUtc) => new(Kasa, Scopes, () => nowUtc, NullLogger<HistorySyncWorker>.Instance,
            Tapo, Settings, Plus2, shelly: Shelly);

        public async Task<IReadOnlyList<Plug>> PlugsAsync()
        {
            using var ctx = Db.CreateContext();
            return (await new PlugRepository(ctx).ListAllAsync(CancellationToken.None)).OrderBy(p => p.Mac).ThenBy(p => p.Channel).ToList();
        }

        public void AnnounceEm() => Shelly.MdnsAnswers.Add(new ShellyMdnsAnswer("shellyemg3-d885ac0b743c", IPAddress.Parse(EmIp), 80,
            new Dictionary<string, string> { ["gen"] = "3", ["app"] = "EMG3" }));

        public void Dispose() => Db.Dispose();
    }

    [Fact]
    public async Task Discovery_Mdns_AddsOnePlugPerChannel()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        rig.AnnounceEm();

        var scan = await rig.Discovery.ScanAsync(CancellationToken.None);

        scan.Items.Should().HaveCount(2).And.OnlyContain(i => i.Status == DiscoveryStatus.Added);
        var plugs = await rig.PlugsAsync();
        plugs.Select(p => (p.Mac, p.Channel, p.Alias)).Should().Equal(
            (EmMac, 0, "shellyemg3-d885ac0b743c ch0"),
            (EmMac, 1, "shellyemg3-d885ac0b743c ch1"));
        plugs.Should().OnlyContain(p => p.Protocol == PlugProtocols.ShellyRpc && p.IsSupported && p.Model == "S3EM-002CXCEU");

        // A second scan finds the same channels: known, nothing duplicated.
        var again = await rig.Discovery.ScanAsync(CancellationToken.None);
        again.Items.Should().OnlyContain(i => i.Status == DiscoveryStatus.Known);
        (await rig.PlugsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Discovery_PasswordMissing_IsUnsupported_ThenSupportedOncePasswordIsSaved()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp).Password = "device-pass";
        rig.AnnounceEm();

        await rig.Discovery.ScanAsync(CancellationToken.None);
        var placeholder = (await rig.PlugsAsync()).Should().ContainSingle().Subject;
        placeholder.IsSupported.Should().BeFalse();
        placeholder.UnsupportedReason.Should().Be(ShellySupport.MissingPasswordReason);

        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "192.168.254.255", true, "", null, false,
            NewShellyPassword: "device-pass"), CancellationToken.None);
        await rig.Discovery.ScanAsync(CancellationToken.None);

        var plugs = await rig.PlugsAsync();
        plugs.Should().HaveCount(2).And.OnlyContain(p => p.IsSupported);
    }

    [Fact]
    public async Task Discovery_ProbeHosts_FindDevicesWithoutMdns_AndCanBeTurnedOff()
    {
        using var rig = new Rig(new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", ShellyProbeHosts: "192.168.254.140-142"));
        rig.Shelly.Add(EmIp);

        await rig.Discovery.ScanAsync(CancellationToken.None);
        (await rig.PlugsAsync()).Should().HaveCount(2);
        rig.Shelly.DeviceInfoCalls.Should().Be(3);   // .140 and .142 do not answer

        using var off = new Rig(new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", ShellyDiscoveryEnabled: false, ShellyProbeHosts: EmIp));
        off.Shelly.Add(EmIp);
        await off.Discovery.ScanAsync(CancellationToken.None);
        (await off.PlugsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AddByIp_TriesShellyAfterLegacyAndTapo()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);

        var result = await rig.Discovery.AddByIpAsync(IPAddress.Parse(EmIp), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("Shelly EM Gen3 (EMG3)").And.Contain("2 canal(e)");
        (await rig.PlugsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Polling_OneGetStatusPerDevice_ForAllChannels()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        rig.AnnounceEm();
        await rig.Discovery.ScanAsync(CancellationToken.None);
        var plugs = await rig.PlugsAsync();
        rig.Shelly.StatusCalls = 0;
        var bc = new PlugStateBroadcaster();

        await rig.Polling(bc).RunOnceAsync(CancellationToken.None);

        rig.Shelly.StatusCalls.Should().Be(1);
        var ch0 = bc.Snapshot()[plugs[0].Id];
        var ch1 = bc.Snapshot()[plugs[1].Id];
        ch0.IsOnline.Should().BeTrue();
        ch0.LastReading!.PowerMw.Should().Be(556_200);
        ch0.LastReading.RelayOn.Should().BeTrue();
        ch0.Protocol.Should().Be(PlugProtocols.ShellyRpc);
        ch0.Model.Should().Be("S3EM-002CXCEU");
        ch0.MacLabel.Should().Be(EmMac);
        ch1.MacLabel.Should().Be(EmMac + "-1");
        ch1.LastReading!.CurrentMa.Should().Be(20);

        using var ctx = rig.Db.CreateContext();
        var samples = await new SampleRepository(ctx).ListByPlugAsync(plugs[0].Id, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        samples.Single().PowerMw.Should().Be(556_200);
        samples.Single().RelayOn.Should().BeTrue();
    }

    [Fact]
    public async Task Polling_DeviceFailure_MarksEveryChannelOffline_WithTheReason()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        rig.AnnounceEm();
        await rig.Discovery.ScanAsync(CancellationToken.None);
        rig.Shelly.Devices[EmIp].Password = "now-protected";
        var bc = new PlugStateBroadcaster();

        await rig.Polling(bc).RunOnceAsync(CancellationToken.None);

        bc.Snapshot().Values.Should().HaveCount(2).And.OnlyContain(s => !s.IsOnline);
        using var ctx = rig.Db.CreateContext();
        var plugs = await rig.PlugsAsync();
        var samples = await new SampleRepository(ctx).ListByPlugAsync(plugs[1].Id, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
        samples.Single().ErrorMessage.Should().Be(ShellySupport.MissingPasswordReason);
    }

    [Fact]
    public async Task History_Em1_ReadsOneWindowPerDayWithRecords_ThenFromTheLastKnownDay()
    {
        using var rig = new Rig();
        var device = rig.Shelly.Add(EmIp);
        // 7 Wh on 10-28, nothing on 10-29, 30 Wh at 23:59 on 10-30, 20 Wh at 00:00 and 12:00 on 10-31, 5 Wh on 11-01 (local +2).
        long Ts(int month, int day, int hour, int min) => new DateTimeOffset(2026, month, day, hour, min, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds();
        device.Energy[0] = new List<ShellyEnergyRow>
        {
            new(Ts(10, 28, 12, 0), 7, 0), new(Ts(10, 30, 23, 59), 30, 0), new(Ts(10, 31, 0, 0), 10, 0), new(Ts(10, 31, 12, 0), 10, 0), new(Ts(11, 1, 9, 0), 5, 0),
        };
        rig.AnnounceEm();
        await rig.Discovery.ScanAsync(CancellationToken.None);
        var ch0 = (await rig.PlugsAsync())[0];
        var worker = rig.History(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc));

        await worker.SyncOneAsync(ch0.Id, CancellationToken.None);

        rig.Shelly.DataRequests.Should().HaveCount(4);   // 10-28, 10-30, 10-31, 11-01: 10-29 has no record block
        using (var ctx = rig.Db.CreateContext())
            (await new HistoryRepository(ctx).GetDailyRangeAsync(ch0.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), CancellationToken.None))
                .Should().Equal(new DailyKwh(2026, 10, 28, 7), new DailyKwh(2026, 10, 30, 30), new DailyKwh(2026, 10, 31, 20), new DailyKwh(2026, 11, 1, 5));

        rig.Shelly.DataRequests.Clear();
        await worker.SyncOneAsync(ch0.Id, CancellationToken.None);
        rig.Shelly.DataRequests.Should().ContainSingle();   // only the last known day (today) again
    }

    [Fact]
    public async Task History_MeteredSwitch_FromOwnReadings()
    {
        using var rig = new Rig();
        rig.Shelly.Add("192.168.1.60", new FakeShellyClient.Device
        {
            DeviceInfo = """{"id":"shellyplusplugs-80646f000001","mac":"80646F000001","model":"SNPL-00112EU","gen":2,"ver":"1.4.4","app":"PlugS","auth_en":false}""",
            Status = FakeShellyClient.Fixture("shelly_plusplugs_status.json"),
            Config = "{}",
        });
        await rig.Discovery.AddByIpAsync(IPAddress.Parse("192.168.1.60"), CancellationToken.None);
        var plug = (await rig.PlugsAsync()).Single();
        plug.Alias.Should().Be("shellyplusplugs-80646f000001");
        var t0 = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.FromHours(2));
        using (var ctx = rig.Db.CreateContext())
            await new SampleRepository(ctx).InsertManyAsync(new[]
            {
                new Sample(0, plug.Id, t0, true, 1000, null, null, 12_000, null, true, null),
                new Sample(0, plug.Id, t0.AddHours(1), true, 1000, null, null, 12_040, null, true, null),
                new Sample(0, plug.Id, t0.AddHours(2), false, null, null, null, null, null, null, "timeout"),
                new Sample(0, plug.Id, t0.AddHours(3), true, 1000, null, null, 12_100, null, true, null),
            }, CancellationToken.None);

        await rig.History(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc)).SyncOneAsync(plug.Id, CancellationToken.None);

        rig.Shelly.DataRequests.Should().BeEmpty();
        using var read = rig.Db.CreateContext();
        (await new HistoryRepository(read).GetDailyRangeAsync(plug.Id, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), CancellationToken.None))
            .Should().Equal(new DailyKwh(2026, 11, 1, 100));
    }

    [Fact]
    public async Task Settings_ShellyPasswordIsProtected_ProbeHostsValidated()
    {
        using var rig = new Rig();

        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "192.168.254.255", true, "", null, false,
            ShellyDiscoveryEnabled: false, ShellyProbeHosts: "192.168.1.10-12", NewShellyPassword: "shelly-secret"), CancellationToken.None);
        var s = await rig.Settings.GetAsync(CancellationToken.None);

        s.ShellyDiscoveryEnabled.Should().BeFalse();
        s.ShellyCredentials!.Password.Should().Be("shelly-secret");
        s.ShellyCredentials.ToString().Should().NotContain("shelly-secret");
        s.ShellyProbeList.Should().HaveCount(3);
        using (var ctx = rig.Db.CreateContext())
            (await new SettingsRepository(ctx).GetAsync(SettingKeys.ShellyPassword, CancellationToken.None)).Should().NotContain("shelly-secret");

        var bad = () => rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false, ShellyProbeHosts: "10.0.0.0/8"), CancellationToken.None);
        await bad.Should().ThrowAsync<FormatException>();

        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false, ClearShellyPassword: true), CancellationToken.None);
        (await rig.Settings.GetAsync(CancellationToken.None)).HasShellyPassword.Should().BeFalse();
    }

    [Fact]
    public async Task DeviceDetails_ShellyChannel()
    {
        using var rig = new Rig();
        rig.Shelly.Add(EmIp);
        rig.AnnounceEm();
        await rig.Discovery.ScanAsync(CancellationToken.None);
        var ch1 = (await rig.PlugsAsync())[1];
        var service = new DeviceDetailsService(rig.Kasa, rig.Tapo, rig.Settings, shelly: rig.Shelly);

        var d = await service.GetAsync(ch1, forceRefresh: true, CancellationToken.None);

        d.Errors.Should().BeEmpty();
        d.Get("Channel").Should().Be("1");
        d.Get("CT type").Should().Be("50AEMG3");
        d.Get("Update available").Should().Be("2.1.1");
    }
    [Fact]
    public async Task History_Em1_IncompleteDayIsNotStored_SmallerValueNeverReplacesStored_FutureRecordsIgnored()
    {
        using var rig = new Rig();
        var device = rig.Shelly.Add(EmIp);
        long Ts(int month, int day, int hour, int min) => new DateTimeOffset(2026, month, day, hour, min, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds();
        device.Energy[0] = new List<ShellyEnergyRow>
        {
            new(Ts(10, 30, 12, 0), 30, 0),
            new(Ts(10, 31, 12, 0), 20, 0),
            new(Ts(11, 1, 9, 0), 5, 0),
            new(Ts(11, 1, 18, 0), 50, 0),    // now = 11-01 12:00 local: stamped in the future, ignored
        };
        device.IncompleteWindows.Add(ShellyEnergyData.DayWindow(new DateOnly(2026, 10, 31), Plus2).FromTs);
        rig.AnnounceEm();
        await rig.Discovery.ScanAsync(CancellationToken.None);
        var ch0 = (await rig.PlugsAsync())[0];
        using (var ctx = rig.Db.CreateContext())
            await new HistoryRepository(ctx).UpsertDailyAsync(ch0.Id, new[] { new DailyKwh(2026, 10, 29, 999) }, CancellationToken.None);
        // Stored 10-29 is the last known day, so the sync starts there; make the device report less for it.
        device.Energy[0].Add(new(Ts(10, 29, 12, 0), 10, 0));

        await rig.History(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc)).SyncOneAsync(ch0.Id, CancellationToken.None);

        using var read = rig.Db.CreateContext();
        (await new HistoryRepository(read).GetDailyRangeAsync(ch0.Id, new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 30), CancellationToken.None))
            .Should().Equal(
                new DailyKwh(2026, 10, 29, 999),   // device says 10: the larger stored value stays
                new DailyKwh(2026, 10, 30, 30),
                // 10-31: incomplete read, not stored
                new DailyKwh(2026, 11, 1, 5));     // the 18:00 record is in the future
    }
}
