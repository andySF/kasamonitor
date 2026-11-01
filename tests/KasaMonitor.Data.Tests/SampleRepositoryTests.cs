using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class SampleRepositoryTests
{
    private static Sample MakeSample(Guid plugId, DateTimeOffset ts, long? power = 50_000) =>
        new(Id: 0, PlugId: plugId, TimestampUtc: ts, Online: power.HasValue,
            PowerMw: power, VoltageMv: 230_000, CurrentMa: 200, TotalWh: 100, Rssi: -50, RelayOn: true, ErrorMessage: null);

    [Fact]
    public async Task InsertAsync_RoundTripsAllFields()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;

        await repo.InsertAsync(MakeSample(plugId, ts), CancellationToken.None);

        var read = (await repo.ListByPlugAsync(plugId, ts.AddMinutes(-1), ts.AddMinutes(1), CancellationToken.None)).Single();
        read.PlugId.Should().Be(plugId);
        read.PowerMw.Should().Be(50_000);
    }

    [Fact]
    public async Task InsertManyAsync_BulkInsertsRows()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 5)
            .Select(i => MakeSample(plugId, t0.AddSeconds(i * 10)))
            .ToList();

        await repo.InsertManyAsync(samples, CancellationToken.None);

        var read = await repo.ListByPlugAsync(plugId, t0.AddSeconds(-1), t0.AddMinutes(1), CancellationToken.None);
        read.Should().HaveCount(5);
    }

    [Fact]
    public async Task ListByPlug_ScopesToTimeRange()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");

        await repo.InsertManyAsync(new[]
        {
            MakeSample(plugId, t0.AddMinutes(-10)),
            MakeSample(plugId, t0.AddMinutes(0)),
            MakeSample(plugId, t0.AddMinutes(10)),
            MakeSample(plugId, t0.AddMinutes(20)),
        }, CancellationToken.None);

        var read = await repo.ListByPlugAsync(plugId, t0, t0.AddMinutes(15), CancellationToken.None);
        read.Should().HaveCount(2); // t0 and t0+10
    }

    [Fact]
    public async Task DeleteOlderThan_RemovesOldRows_OnlyOldOnes()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var cutoff = DateTimeOffset.Parse("2026-05-08T00:00:00Z");

        await repo.InsertManyAsync(new[]
        {
            MakeSample(plugId, cutoff.AddDays(-10)),
            MakeSample(plugId, cutoff.AddDays(-1)),
            MakeSample(plugId, cutoff.AddDays(1)),
        }, CancellationToken.None);

        var deleted = await repo.DeleteOlderThanAsync(cutoff, CancellationToken.None);
        deleted.Should().Be(2);

        var remaining = await repo.ListByPlugAsync(plugId, cutoff.AddDays(-30), cutoff.AddDays(30), CancellationToken.None);
        remaining.Should().HaveCount(1);
    }

    [Fact]
    public async Task CountAndBatchedDelete_OlderThanCutoff()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var cutoff = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        await repo.InsertManyAsync(Enumerable.Range(1, 7).Select(i => MakeSample(plugId, cutoff.AddHours(-i)))
            .Append(MakeSample(plugId, cutoff)).Append(MakeSample(plugId, cutoff.AddHours(1))).ToList(), CancellationToken.None);

        (await repo.CountOlderThanAsync(cutoff, CancellationToken.None)).Should().Be(7);
        (await repo.DeleteOlderThanAsync(cutoff, CancellationToken.None, batchSize: 3)).Should().Be(7);   // 3 + 3 + 1
        (await repo.CountOlderThanAsync(cutoff, CancellationToken.None)).Should().Be(0);
        (await repo.ListByPlugAsync(plugId, cutoff.AddDays(-1), cutoff.AddDays(1), CancellationToken.None)).Should().HaveCount(2);
    }
}
