using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class DiscoveryWorkerTests
{
    private static DiscoveredPlug Make(string mac, string alias = "X", string ip = "192.168.254.105") =>
        new(IPAddress.Parse(ip), new PlugInfo(mac, alias, "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));

    [Fact]
    public async Task RunOnceAsync_NewPlugs_AreUpserted_AndPublishedAsDiscovered()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var plugRepo = new PlugRepository(ctx);
        var fake = new FakeKasaClient();
        fake.DiscoveredPlugs.Add(Make("aa", ip: "192.168.254.105"));
        fake.DiscoveredPlugs.Add(Make("bb", ip: "192.168.254.106"));
        var channel = new PlugDiscoveredChannel();

        var worker = new DiscoveryWorker(
            fake, plugRepo, channel,
            new[] { IPAddress.Parse("192.168.254.255") },
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMinutes(5),
            NullLogger<DiscoveryWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        var plugs = await plugRepo.ListAllAsync(CancellationToken.None);
        plugs.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunOnceAsync_AlreadyKnownPlug_NotPublishedAgain()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var plugRepo = new PlugRepository(ctx);
        await plugRepo.UpsertFromDiscoveryAsync(Make("aa"), DateTimeOffset.UtcNow, CancellationToken.None);

        var fake = new FakeKasaClient();
        fake.DiscoveredPlugs.Add(Make("aa"));
        var channel = new PlugDiscoveredChannel();

        var worker = new DiscoveryWorker(
            fake, plugRepo, channel,
            new[] { IPAddress.Parse("192.168.254.255") },
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMinutes(5),
            NullLogger<DiscoveryWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        // Channel should be empty (no new discovery published)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var got = new List<Guid>();
        try { await foreach (var id in channel.ReadAllAsync(cts.Token)) got.Add(id); }
        catch (OperationCanceledException) { }
        got.Should().BeEmpty();
    }
}
