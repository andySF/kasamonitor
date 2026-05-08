using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using System.Net;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class PlugRepositoryTests
{
    private static DiscoveredPlug MakeDiscovered(string mac = "c006c3702bfc", string alias = "Birou", string ip = "192.168.254.105") =>
        new(IPAddress.Parse(ip), new PlugInfo(
            Mac: mac, Alias: alias, Model: "KP115(EU)", HwVer: "1.0", SwVer: "1.0.20", OemId: "X",
            RelayOn: true, RssiDbm: -53, OnTime: TimeSpan.FromMinutes(10)));

    [Fact]
    public async Task UpsertFromDiscovery_NewPlug_InsertsRowAndReturnsId()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var now = DateTimeOffset.UtcNow;

        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), now, CancellationToken.None);

        id.Should().NotBe(Guid.Empty);
        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.Mac.Should().Be("c006c3702bfc");
        plug.FirstSeenUtc.Should().Be(now);
        plug.LastSeenUtc.Should().Be(now);
    }

    [Fact]
    public async Task UpsertFromDiscovery_ExistingPlug_UpdatesIpAndLastSeen_PreservesId()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);

        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var firstId = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(ip: "192.168.254.105"), t0, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-08T10:30:00Z");
        var secondId = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(ip: "192.168.254.106"), t1, CancellationToken.None);

        secondId.Should().Be(firstId);
        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.IpAddress.ToString().Should().Be("192.168.254.106");
        plug.FirstSeenUtc.Should().Be(t0); // unchanged
        plug.LastSeenUtc.Should().Be(t1);
    }

    [Fact]
    public async Task UpsertFromDiscovery_DoesNotOverwriteCustomAlias()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var now = DateTimeOffset.UtcNow;

        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(alias: "Birou"), now, CancellationToken.None);
        await repo.SetAliasAsync(id, "MyCustom", isCustom: true, CancellationToken.None);

        await repo.UpsertFromDiscoveryAsync(MakeDiscovered(alias: "DeviceAlias"), now, CancellationToken.None);

        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.Alias.Should().Be("MyCustom");
        plug.AliasIsCustom.Should().BeTrue();
    }

    [Fact]
    public async Task GetByMac_ReturnsRow_WhenPresent()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), DateTimeOffset.UtcNow, CancellationToken.None);

        var plug = await repo.GetByMacAsync("c006c3702bfc", CancellationToken.None);
        plug.Should().NotBeNull();
        plug!.Alias.Should().Be("Birou");
    }

    [Fact]
    public async Task GetByMac_ReturnsNull_WhenAbsent()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);

        var plug = await repo.GetByMacAsync("ffffffffffff", CancellationToken.None);
        plug.Should().BeNull();
    }

    [Fact]
    public async Task SetArchived_FlipsFlag()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), DateTimeOffset.UtcNow, CancellationToken.None);

        await repo.SetArchivedAsync(id, true, CancellationToken.None);
        (await repo.GetByMacAsync("c006c3702bfc", CancellationToken.None))!.IsArchived.Should().BeTrue();

        await repo.SetArchivedAsync(id, false, CancellationToken.None);
        (await repo.GetByMacAsync("c006c3702bfc", CancellationToken.None))!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateLastSeen_TouchesTimestampOnly()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), t0, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-08T11:00:00Z");
        await repo.UpdateLastSeenAsync(id, t1, CancellationToken.None);

        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.LastSeenUtc.Should().Be(t1);
        plug.FirstSeenUtc.Should().Be(t0);
    }
}
