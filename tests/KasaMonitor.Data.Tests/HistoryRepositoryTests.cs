using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class HistoryRepositoryTests
{
    [Fact]
    public async Task UpsertDaily_NewEntries_Inserts()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();

        await repo.UpsertDailyAsync(plugId, new[]
        {
            new DailyKwh(2026, 5, 1, 1500),
            new DailyKwh(2026, 5, 2, 1700),
        }, CancellationToken.None);

        var got = await repo.GetDailyRangeAsync(plugId,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), CancellationToken.None);
        got.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpsertDaily_OnDuplicateKey_Replaces()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();

        await repo.UpsertDailyAsync(plugId, new[] { new DailyKwh(2026, 5, 1, 1500) }, CancellationToken.None);
        await repo.UpsertDailyAsync(plugId, new[] { new DailyKwh(2026, 5, 1, 1900) }, CancellationToken.None);

        var got = await repo.GetDailyRangeAsync(plugId,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 1), CancellationToken.None);
        got.Single().EnergyWh.Should().Be(1900);
    }

    [Fact]
    public async Task UpsertMonthly_NewAndUpsert()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();

        await repo.UpsertMonthlyAsync(plugId, new[] { new MonthlyKwh(2026, 5, 50000) }, CancellationToken.None);
        await repo.UpsertMonthlyAsync(plugId, new[] { new MonthlyKwh(2026, 5, 60000), new MonthlyKwh(2026, 6, 1) }, CancellationToken.None);

        var got = await repo.GetMonthlyRangeAsync(plugId, 2026, 2026, CancellationToken.None);
        got.Should().HaveCount(2);
        got.Single(m => m.Month == 5).EnergyWh.Should().Be(60000);
    }

    [Fact]
    public async Task GetDailyRange_RespectsBounds()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();
        await repo.UpsertDailyAsync(plugId, new[]
        {
            new DailyKwh(2026, 4, 30, 100),
            new DailyKwh(2026, 5, 1, 200),
            new DailyKwh(2026, 5, 2, 300),
            new DailyKwh(2026, 5, 3, 400),
        }, CancellationToken.None);

        var got = await repo.GetDailyRangeAsync(plugId,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 2), CancellationToken.None);
        got.Should().HaveCount(2);
        got.Sum(d => d.EnergyWh).Should().Be(500);
    }

    [Fact]
    public async Task GetDailyRangeAll_ReturnsEveryPlugInRange()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await repo.UpsertDailyAsync(a, new[] { new DailyKwh(2026, 5, 1, 100), new DailyKwh(2026, 5, 2, 200), new DailyKwh(2026, 6, 1, 999) }, CancellationToken.None);
        await repo.UpsertDailyAsync(b, new[] { new DailyKwh(2026, 5, 2, 50), new DailyKwh(2026, 4, 30, 999) }, CancellationToken.None);

        var got = await repo.GetDailyRangeAllAsync(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), CancellationToken.None);

        got.Should().HaveCount(3);
        got.Where(r => r.PlugId == a).Select(r => r.Day.EnergyWh).Should().BeEquivalentTo(new[] { 100L, 200L });
        got.Single(r => r.PlugId == b).Day.Should().Be(new DailyKwh(2026, 5, 2, 50));
    }
}
