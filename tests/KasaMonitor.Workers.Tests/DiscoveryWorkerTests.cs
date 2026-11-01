using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class DiscoveryWorkerTests
{
    private static DiscoveredPlug Make(string mac, string alias = "X", string ip = "192.168.254.105") =>
        new(IPAddress.Parse(ip), new PlugInfo(mac, alias, "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));

    private static TapoDiscoveryResponse Tapo(string mac, string encrypt, string ip = "192.168.254.146", string type = "SMART.TAPOPLUG") =>
        new(IPAddress.Parse(ip), mac, type, "P115(EU)", encrypt, 80, 2, false);

    private sealed record Rig(TestDbFactory Db, FakeKasaClient Kasa, FakeTapoClient Tapo, PlugDiscoveryChannelProbe Channel,
        KasaSettingsService Settings, PlugDiscoveryService Service, DiscoveryWorker Worker) : IDisposable
    {
        public void Dispose() => Db.Dispose();

        public async Task<IReadOnlyList<Plug>> PlugsAsync()
        {
            using var ctx = Db.CreateContext();
            return await new PlugRepository(ctx).ListAllAsync(CancellationToken.None);
        }
    }

    private sealed class PlugDiscoveryChannelProbe
    {
        public readonly PlugDiscoveredChannel Channel = new();

        public async Task<List<Guid>> DrainAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var got = new List<Guid>();
            try { await foreach (var id in Channel.ReadAllAsync(cts.Token)) got.Add(id); }
            catch (OperationCanceledException) { }
            return got;
        }
    }

    private static Rig CreateRig(bool autoAdd = true, string? tapoUser = null, string? tapoPassword = null)
    {
        var db = new TestDbFactory();
        var scopes = new TestScopeFactory(db.CreateContext);
        var kasa = new FakeKasaClient();
        var tapo = new FakeTapoClient();
        var channel = new PlugDiscoveryChannelProbe();
        var settings = TestSettings.Create(scopes, new KasaSettingsDefaults(
            BroadcastNetworks: "192.168.254.255", AutoAddDiscoveredPlugs: autoAdd,
            TapoUsername: tapoUser, TapoPassword: tapoPassword));
        var service = new PlugDiscoveryService(kasa, tapo, scopes, settings, channel.Channel,
            TimeSpan.FromMilliseconds(200), NullLogger<PlugDiscoveryService>.Instance);
        var worker = new DiscoveryWorker(service, settings, NullLogger<DiscoveryWorker>.Instance);
        return new Rig(db, kasa, tapo, channel, settings, service, worker);
    }

    [Fact]
    public async Task RunOnceAsync_NewPlugs_AreUpserted_AndPublishedAsDiscovered()
    {
        using var rig = CreateRig();
        rig.Kasa.DiscoveredPlugs.Add(Make("aa", ip: "192.168.254.105"));
        rig.Kasa.DiscoveredPlugs.Add(Make("bb", ip: "192.168.254.106"));

        var scan = await rig.Worker.RunOnceAsync(CancellationToken.None);

        (await rig.PlugsAsync()).Should().HaveCount(2);
        scan.Items.Should().OnlyContain(i => i.Status == DiscoveryStatus.Added);
        (await rig.Channel.DrainAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task RunOnceAsync_AlreadyKnownPlug_NotPublishedAgain()
    {
        using var rig = CreateRig();
        using (var ctx = rig.Db.CreateContext())
            await new PlugRepository(ctx).UpsertFromDiscoveryAsync(Make("aa"), DateTimeOffset.UtcNow, CancellationToken.None);
        rig.Kasa.DiscoveredPlugs.Add(Make("aa"));

        var scan = await rig.Worker.RunOnceAsync(CancellationToken.None);

        scan.Items.Single().Status.Should().Be(DiscoveryStatus.Known);
        (await rig.Channel.DrainAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AutoAddOff_NewPlugIsReportedButNotInserted_AndCanBeAddedWithOneClick()
    {
        using var rig = CreateRig(autoAdd: false);
        rig.Kasa.DiscoveredPlugs.Add(Make("aa"));

        var scan = await rig.Service.ScanAsync(CancellationToken.None);

        scan.AutoAdd.Should().BeFalse();
        scan.Items.Single().Status.Should().Be(DiscoveryStatus.New);
        scan.Items.Single().PlugId.Should().BeNull();
        (await rig.PlugsAsync()).Should().BeEmpty();

        await rig.Service.AddAsync(scan.Items.Single().Plug, CancellationToken.None);

        (await rig.PlugsAsync()).Single().Mac.Should().Be("aa");
        rig.Service.LastScan!.Items.Single().Status.Should().Be(DiscoveryStatus.Added);
    }

    [Fact]
    public async Task AutoAddSettingSavedInDatabase_OverridesEnvironmentDefault()
    {
        using var rig = CreateRig(autoAdd: true);
        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "192.168.254.255", AutoAddDiscoveredPlugs: false, "", null, false),
            CancellationToken.None);
        rig.Kasa.DiscoveredPlugs.Add(Make("aa"));

        await rig.Service.ScanAsync(CancellationToken.None);

        (await rig.PlugsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TapoKlapWithoutCredentials_IsListedAsUnsupported()
    {
        using var rig = CreateRig();
        rig.Tapo.Responses.Add(Tapo("b01921c4c028", "KLAP"));

        var scan = await rig.Service.ScanAsync(CancellationToken.None);

        scan.Items.Single().Status.Should().Be(DiscoveryStatus.Unsupported);
        var plug = (await rig.PlugsAsync()).Single();
        plug.Protocol.Should().Be(PlugProtocols.Klap);
        plug.UnsupportedReason.Should().Be(TapoSupport.MissingCredentialsReason);
        plug.IpAddress.ToString().Should().Be("192.168.254.146");
    }

    [Fact]
    public async Task TapoTpap_IsListedAsUnsupportedWithProtocolName()
    {
        using var rig = CreateRig();
        rig.Tapo.Responses.Add(Tapo("b01921c4c028", "TPAP"));

        await rig.Service.ScanAsync(CancellationToken.None);

        var plug = (await rig.PlugsAsync()).Single();
        plug.Protocol.Should().Be(PlugProtocols.Unsupported);
        plug.UnsupportedReason.Should().Be("protocol TPAP");
        TapoSupport.IsUnknownProtocolReason(plug.UnsupportedReason).Should().BeTrue();
        TapoSupport.Describe(plug.Model, plug.UnsupportedReason!).Should().Be("nesuportat: P115(EU), protocol TPAP");
    }

    [Fact]
    public async Task TapoKlapWithCredentials_IsQueriedAndAddedAsSupported()
    {
        using var rig = CreateRig(tapoUser: "user@example.com", tapoPassword: "pw");
        rig.Tapo.Responses.Add(Tapo("b01921c4c028", "KLAP"));
        TapoCredentials? used = null;
        rig.Tapo.OnGetDeviceInfo = (ip, c) =>
        {
            used = c;
            return Task.FromResult(new PlugInfo("b01921c4c028", "Frigider", "P115", "1.0", "1.4.3", "OEM", true, -45, TimeSpan.Zero));
        };

        await rig.Service.ScanAsync(CancellationToken.None);

        used.Should().Be(new TapoCredentials("user@example.com", "pw"));
        var plug = (await rig.PlugsAsync()).Single();
        plug.IsSupported.Should().BeTrue();
        plug.Protocol.Should().Be(PlugProtocols.Klap);
        plug.Alias.Should().Be("Frigider");
    }

    [Fact]
    public async Task TapoAesWithCredentials_IsAddedWithAesProtocol()
    {
        using var rig = CreateRig(tapoUser: "user@example.com", tapoPassword: "pw");
        rig.Tapo.Responses.Add(Tapo("b01921c4c68b", "AES", ip: "192.168.254.130"));
        rig.Tapo.OnGetDeviceInfo = (_, _) =>
            Task.FromResult(new PlugInfo("b01921c4c68b", "A doua", "P115", "1.0", "1.0.0", "OEM", true, -50, TimeSpan.Zero));

        await rig.Service.ScanAsync(CancellationToken.None);

        var plug = (await rig.PlugsAsync()).Single();
        plug.IsSupported.Should().BeTrue();
        plug.Protocol.Should().Be(PlugProtocols.Aes);
        plug.Alias.Should().Be("A doua");
    }

    [Fact]
    public async Task DeviceAnsweringBothProtocols_LegacyWins()
    {
        using var rig = CreateRig();
        rig.Kasa.DiscoveredPlugs.Add(Make("c006c3702bfc"));
        rig.Tapo.Responses.Add(Tapo("c006c3702bfc", "KLAP", type: "IOT.SMARTPLUGSWITCH"));

        await rig.Service.ScanAsync(CancellationToken.None);

        var plug = (await rig.PlugsAsync()).Single();
        plug.Protocol.Should().Be(PlugProtocols.Legacy);
        plug.IsSupported.Should().BeTrue();
    }

    [Fact]
    public async Task AddByIp_FallsBackToTapoWhenLegacyFails()
    {
        using var rig = CreateRig();
        rig.Kasa.OnGetSysInfo = _ => Task.FromException<PlugInfo>(new TimeoutException("no 9999"));
        rig.Tapo.Responses.Add(Tapo("b01921c4c028", "TPAP"));

        var result = await rig.Service.AddByIpAsync(IPAddress.Parse("192.168.254.146"), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.Message.Should().Contain("nesuportat: P115(EU), protocol TPAP");
        (await rig.PlugsAsync()).Single().UnsupportedReason.Should().Be("protocol TPAP");
    }

    [Fact]
    public async Task AddByIp_LegacyAnswer_AddsLegacyPlug()
    {
        using var rig = CreateRig();
        rig.Kasa.OnGetSysInfo = _ => Task.FromResult(Make("dd").Info);

        var result = await rig.Service.AddByIpAsync(IPAddress.Parse("192.168.254.150"), CancellationToken.None);

        result.Success.Should().BeTrue();
        var plug = (await rig.PlugsAsync()).Single();
        plug.Protocol.Should().Be(PlugProtocols.Legacy);
        plug.IpAddress.ToString().Should().Be("192.168.254.150");
    }

    [Fact]
    public async Task AddByIp_NothingAnswers_ReportsFailure()
    {
        using var rig = CreateRig();
        rig.Kasa.OnGetSysInfo = _ => Task.FromException<PlugInfo>(new TimeoutException("no 9999"));

        var result = await rig.Service.AddByIpAsync(IPAddress.Parse("192.168.254.151"), CancellationToken.None);

        result.Success.Should().BeFalse();
        (await rig.PlugsAsync()).Should().BeEmpty();
    }
}
