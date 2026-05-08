using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class RollupWorkerTests
{
    [Fact]
    public async Task RollupSamplesToMinutesAsync_Aggregates_BySample_PerMinute()
    {
        using var f = new TestDbFactory();
        var plugId = Guid.NewGuid();
        var dayBase = DateTimeOffset.Parse("2026-05-08T00:00:00Z");

        using (var ctx = f.CreateContext())
        {
            var sampleRepo = new SampleRepository(ctx);
            var samples = new[]
            {
                new Sample(0, plugId, dayBase, true, 1000, null, null, null, null, null, null),
                new Sample(0, plugId, dayBase.AddSeconds(10), true, 2000, null, null, null, null, null, null),
                new Sample(0, plugId, dayBase.AddSeconds(20), true, 3000, null, null, null, null, null, null),
                // Different minute
                new Sample(0, plugId, dayBase.AddMinutes(1), true, 4000, null, null, null, null, null, null),
            };
            await sampleRepo.InsertManyAsync(samples, CancellationToken.None);
        }

        var scopeFactory = new TestScopeFactory(f.CreateContext);
        var worker = new RollupWorker(scopeFactory, NullLogger<RollupWorker>.Instance);
        await worker.RollupSamplesToMinutesAsync(new DateOnly(2026, 5, 8), CancellationToken.None);

        using var ctxRead = f.CreateContext();
        var aggs = await ctxRead.OneMinuteAggregates.Where(a => a.PlugId == plugId).OrderBy(a => a.BucketStartUtc).ToListAsync();
        aggs.Should().HaveCount(2);
        aggs[0].PAvgMw.Should().Be(2000); // (1000+2000+3000)/3
        aggs[0].PMaxMw.Should().Be(3000);
        aggs[0].PMinMw.Should().Be(1000);
        aggs[0].SampleCount.Should().Be(3);
    }
}
