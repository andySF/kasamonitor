using FluentAssertions;
using KasaMonitor.Core.Models;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class DeviceDetailsServiceTests
{
    private static Plug MakePlug(string protocol = PlugProtocols.Legacy, string? unsupported = null) =>
        new(Guid.NewGuid(), "aa", IPAddress.Parse("192.168.1.30"), "Birou", false, "KP115", "", "", "",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null, protocol, unsupported);

    private static DeviceDetails Sample(string relay) =>
        new(DateTimeOffset.UnixEpoch, new[] { new DeviceDetailItem("State", "Relay", relay) }, Array.Empty<string>());

    [Fact]
    public async Task Legacy_IsCachedFor60Seconds_UnlessForced()
    {
        using var f = new TestDbFactory();
        var now = DateTimeOffset.Parse("2026-11-01T12:00:00Z");
        var calls = 0;
        var kasa = new FakeKasaClient { OnGetDeviceDetails = _ => { calls++; return Task.FromResult(Sample("on")); } };
        var svc = new DeviceDetailsService(kasa, new FakeTapoClient(), TestSettings.Create(new TestScopeFactory(f.CreateContext)), () => now);
        var plug = MakePlug();

        var first = await svc.GetAsync(plug, false, CancellationToken.None);
        now = now.AddSeconds(59);
        await svc.GetAsync(plug, false, CancellationToken.None);
        calls.Should().Be(1);
        first.ReadAtUtc.Should().Be(DateTimeOffset.Parse("2026-11-01T12:00:00Z"));

        now = now.AddSeconds(2);
        await svc.GetAsync(plug, false, CancellationToken.None);
        calls.Should().Be(2);

        await svc.GetAsync(plug, true, CancellationToken.None);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task Tapo_UsesCredentials_AndMissingCredentialsGiveAGentleError()
    {
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);
        TapoCredentials? used = null;
        var tapo = new FakeTapoClient { OnGetDeviceDetails = (_, c) => { used = c; return Task.FromResult(Sample("off")); } };
        var plug = MakePlug(PlugProtocols.Klap);

        var withCreds = new DeviceDetailsService(new FakeKasaClient(), tapo,
            TestSettings.Create(scopes, new KasaSettingsDefaults(TapoUsername: "u@example.com", TapoPassword: "pw")));
        (await withCreds.GetAsync(plug, false, CancellationToken.None)).Get("Relay").Should().Be("off");
        used.Should().Be(new TapoCredentials("u@example.com", "pw"));

        var noCreds = new DeviceDetailsService(new FakeKasaClient(), tapo, TestSettings.Create(scopes));
        var d = await noCreds.GetAsync(plug, false, CancellationToken.None);
        d.Items.Should().BeEmpty();
        d.Errors.Should().ContainSingle().Which.Should().Contain("credentials");
    }

    [Fact]
    public async Task DeviceError_BecomesAnErrorEntry_NotAnException()
    {
        using var f = new TestDbFactory();
        var kasa = new FakeKasaClient { OnGetDeviceDetails = _ => Task.FromException<DeviceDetails>(new TimeoutException("timed out")) };
        var svc = new DeviceDetailsService(kasa, new FakeTapoClient(), TestSettings.Create(new TestScopeFactory(f.CreateContext)));

        var d = await svc.GetAsync(MakePlug(), false, CancellationToken.None);

        d.Errors.Should().ContainSingle().Which.Should().Contain("timed out");
    }

    [Fact]
    public async Task UnsupportedPlug_IsNotQueried()
    {
        using var f = new TestDbFactory();
        var svc = new DeviceDetailsService(new FakeKasaClient(), new FakeTapoClient(), TestSettings.Create(new TestScopeFactory(f.CreateContext)));

        var d = await svc.GetAsync(MakePlug(PlugProtocols.Unsupported, "protocol TPAP"), false, CancellationToken.None);

        d.Errors.Should().ContainSingle().Which.Should().Contain("not supported");
    }
}
