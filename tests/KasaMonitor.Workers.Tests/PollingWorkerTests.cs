using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PollingWorkerTests
{
    [Fact]
    public async Task RunOnceAsync_SuccessfulPoll_InsertsOnlineSample_AndBroadcasts()
    {
        using var f = new TestDbFactory();
        Guid plugId;
        using (var ctx = f.CreateContext())
        {
            var plugRepo = new PlugRepository(ctx);
            var dp = new DiscoveredPlug(IPAddress.Parse("192.168.254.105"),
                new PlugInfo("aa", "X", "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));
            plugId = await plugRepo.UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        using var ctxRead = f.CreateContext();
        var sampleRepo = new SampleRepository(ctxRead);
        var fake = new FakeKasaClient
        {
            OnGetRealtime = ip => Task.FromResult(new EmeterReading(50_000, 230_000, 200, 100))
        };
        var bc = new PlugStateBroadcaster();

        var worker = new PollingWorker(
            fake, () => f.CreateContext(),
            async ct =>
            {
                using var c = f.CreateContext();
                return await new PlugRepository(c).ListAllAsync(ct);
            },
            sampleRepo, bc,
            TimeSpan.FromSeconds(10),
            NullLogger<PollingWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        var samples = await sampleRepo.ListByPlugAsync(plugId,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        samples.Should().HaveCount(1);
        samples[0].Online.Should().BeTrue();
        samples[0].PowerMw.Should().Be(50_000);

        bc.Snapshot().Should().ContainKey(plugId);
    }

    [Fact]
    public async Task RunOnceAsync_PollFails_InsertsOfflineSampleWithErrorMessage()
    {
        using var f = new TestDbFactory();
        Guid plugId;
        using (var ctx = f.CreateContext())
        {
            var plugRepo = new PlugRepository(ctx);
            var dp = new DiscoveredPlug(IPAddress.Parse("192.168.254.105"),
                new PlugInfo("bb", "X", "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));
            plugId = await plugRepo.UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, CancellationToken.None);
        }

        using var ctxRead = f.CreateContext();
        var sampleRepo = new SampleRepository(ctxRead);
        var fake = new FakeKasaClient
        {
            OnGetRealtime = _ => Task.FromException<EmeterReading>(new TimeoutException("simulated"))
        };
        var bc = new PlugStateBroadcaster();

        var worker = new PollingWorker(
            fake, () => f.CreateContext(),
            async ct =>
            {
                using var c = f.CreateContext();
                return await new PlugRepository(c).ListAllAsync(ct);
            },
            sampleRepo, bc,
            TimeSpan.FromSeconds(10),
            NullLogger<PollingWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        var samples = await sampleRepo.ListByPlugAsync(plugId,
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1),
            CancellationToken.None);
        samples.Single().Online.Should().BeFalse();
        samples.Single().ErrorMessage.Should().Contain("simulated");
    }
}
