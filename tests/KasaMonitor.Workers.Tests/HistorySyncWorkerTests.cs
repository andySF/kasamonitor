using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class HistorySyncWorkerTests
{
    [Fact]
    public async Task SyncOneAsync_FetchesAndUpsertsDailyAndMonthly()
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

        var fake = new FakeKasaClient
        {
            OnGetMonthStat = (_, year) => Task.FromResult<IReadOnlyList<MonthlyKwh>>(new[]
            {
                new MonthlyKwh(year, 1, 100), new MonthlyKwh(year, 2, 200)
            }),
            OnGetDayStat = (_, year, month) => Task.FromResult<IReadOnlyList<DailyKwh>>(new[]
            {
                new DailyKwh(year, month, 1, 50), new DailyKwh(year, month, 2, 60)
            })
        };

        var now = DateTime.UtcNow;
        var scopeFactory = new TestScopeFactory(f.CreateContext);
        var worker = new HistorySyncWorker(fake, scopeFactory, () => now,
            NullLogger<HistorySyncWorker>.Instance);

        await worker.SyncOneAsync(plugId, CancellationToken.None);

        using var ctxRead = f.CreateContext();
        var historyRepo = new HistoryRepository(ctxRead);

        var thisYear = now.Year;
        var monthly = await historyRepo.GetMonthlyRangeAsync(plugId, thisYear - 1, thisYear, CancellationToken.None);
        monthly.Should().NotBeEmpty();

        var daily = await historyRepo.GetDailyRangeAsync(plugId,
            new DateOnly(thisYear, 1, 1), new DateOnly(thisYear, 12, 31), CancellationToken.None);
        daily.Should().NotBeEmpty();
    }
}
