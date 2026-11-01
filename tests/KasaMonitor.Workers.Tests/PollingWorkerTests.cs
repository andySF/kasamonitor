using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PollingWorkerTests
{
    private static async Task<Guid> SeedAsync(TestDbFactory f, string mac, string protocol = PlugProtocols.Legacy,
        string? unsupported = null, string model = "KP115(EU)")
    {
        using var ctx = f.CreateContext();
        var dp = new DiscoveredPlug(IPAddress.Parse("192.168.254.105"),
            new PlugInfo(mac, "X-" + mac, model, "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero), protocol, unsupported);
        return await new PlugRepository(ctx).UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static PollingWorker CreateWorker(TestDbFactory f, FakeKasaClient kasa, FakeTapoClient tapo, PlugStateBroadcaster bc,
        KasaSettingsDefaults? defaults = null)
    {
        var scopes = new TestScopeFactory(f.CreateContext);
        return new PollingWorker(kasa, tapo, scopes, bc, TestSettings.Create(scopes, defaults),
            TimeSpan.FromSeconds(10), NullLogger<PollingWorker>.Instance);
    }

    private static async Task<IReadOnlyList<Sample>> SamplesAsync(TestDbFactory f, Guid plugId)
    {
        using var ctx = f.CreateContext();
        return await new SampleRepository(ctx).ListByPlugAsync(plugId,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);
    }

    [Fact]
    public async Task RunOnceAsync_SuccessfulPoll_InsertsOnlineSample_AndBroadcasts()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, "aa");
        var fake = new FakeKasaClient
        {
            OnGetRealtime = ip => Task.FromResult(new EmeterReading(50_000, 230_000, 200, 100))
        };
        var bc = new PlugStateBroadcaster();

        await CreateWorker(f, fake, new FakeTapoClient(), bc).RunOnceAsync(CancellationToken.None);

        var samples = await SamplesAsync(f, plugId);
        samples.Should().HaveCount(1);
        samples[0].Online.Should().BeTrue();
        samples[0].PowerMw.Should().Be(50_000);

        bc.Snapshot().Should().ContainKey(plugId);
        bc.Snapshot()[plugId].Model.Should().Be("KP115");
        bc.Snapshot()[plugId].Protocol.Should().Be(PlugProtocols.Legacy);
    }

    [Fact]
    public async Task RunOnceAsync_PollFails_InsertsOfflineSampleWithErrorMessage()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, "bb");
        var fake = new FakeKasaClient
        {
            OnGetRealtime = _ => Task.FromException<EmeterReading>(new TimeoutException("simulated"))
        };

        await CreateWorker(f, fake, new FakeTapoClient(), new PlugStateBroadcaster()).RunOnceAsync(CancellationToken.None);

        var samples = await SamplesAsync(f, plugId);
        samples.Single().Online.Should().BeFalse();
        samples.Single().ErrorMessage.Should().Contain("simulated");
    }

    [Fact]
    public async Task ArchivedPlug_IsNotPolled_AndIsRemovedFromLiveState()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, "cc");
        var polls = 0;
        var fake = new FakeKasaClient
        {
            OnGetRealtime = _ => { Interlocked.Increment(ref polls); return Task.FromResult(new EmeterReading(1000, 230_000, 10, 5)); }
        };
        var bc = new PlugStateBroadcaster();
        var removed = new List<Guid>();
        bc.StateRemoved += removed.Add;
        var worker = CreateWorker(f, fake, new FakeTapoClient(), bc);

        await worker.RunOnceAsync(CancellationToken.None);
        bc.Snapshot().Should().ContainKey(plugId);

        using (var ctx = f.CreateContext())
            await new PlugRepository(ctx).SetArchivedAsync(plugId, true, CancellationToken.None);
        await worker.RunOnceAsync(CancellationToken.None);

        polls.Should().Be(1);
        bc.Snapshot().Should().NotContainKey(plugId);
        removed.Should().Equal(plugId);
        (await SamplesAsync(f, plugId)).Should().HaveCount(1);
    }

    [Fact]
    public async Task UnsupportedPlug_IsNeverPolled()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, "dd", PlugProtocols.Unsupported, "protocol TPAP");
        var tapo = new FakeTapoClient();
        var bc = new PlugStateBroadcaster();

        await CreateWorker(f, new FakeKasaClient(), tapo, bc).RunOnceAsync(CancellationToken.None);

        tapo.EnergyCalls.Should().Be(0);
        bc.Snapshot().Should().NotContainKey(plugId);
        (await SamplesAsync(f, plugId)).Should().BeEmpty();
    }

    [Fact]
    public async Task KlapPlug_IsPolledThroughTapoClientWithStoredCredentials()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, "b01921c4c028", PlugProtocols.Klap, model: "P115");
        TapoCredentials? used = null;
        var tapo = new FakeTapoClient
        {
            OnGetEnergyUsage = (_, c) => { used = c; return Task.FromResult(new EmeterReading(109_269, 231_150, 754, 458)); }
        };
        var bc = new PlugStateBroadcaster();

        await CreateWorker(f, new FakeKasaClient(), tapo, bc,
            new KasaSettingsDefaults(TapoUsername: "user@example.com", TapoPassword: "pw")).RunOnceAsync(CancellationToken.None);

        used.Should().Be(new TapoCredentials("user@example.com", "pw"));
        var state = bc.Snapshot()[plugId];
        state.IsOnline.Should().BeTrue();
        state.Protocol.Should().Be(PlugProtocols.Klap);
        state.Model.Should().Be("P115");
        (await SamplesAsync(f, plugId)).Single().VoltageMv.Should().Be(231_150);
    }

    [Fact]
    public async Task KlapPlug_WithoutCredentials_IsOffline()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, "b01921c4c028", PlugProtocols.Klap, model: "P115");
        var tapo = new FakeTapoClient();

        await CreateWorker(f, new FakeKasaClient(), tapo, new PlugStateBroadcaster()).RunOnceAsync(CancellationToken.None);

        tapo.EnergyCalls.Should().Be(0);
        (await SamplesAsync(f, plugId)).Single().ErrorMessage.Should().Contain("credențialele");
    }
}
