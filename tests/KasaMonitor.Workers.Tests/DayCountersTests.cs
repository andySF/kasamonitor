using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>Today/month Wh on the live reading: legacy from emeter.get_daystat (cached 5 min), tariff from settings.</summary>
public class DayCountersTests
{
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    private static async Task<Guid> SeedLegacyAsync(TestDbFactory f)
    {
        using var ctx = f.CreateContext();
        var dp = new DiscoveredPlug(IPAddress.Parse("192.168.1.20"),
            new PlugInfo("aa", "Birou", "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));
        return await new PlugRepository(ctx).UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    [Fact]
    public async Task Legacy_TodayAndMonthFromDayStat_CachedFiveMinutes()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedLegacyAsync(f);
        var now = DateTimeOffset.Parse("2026-11-03T22:30:00Z");   // local (+2): 2026-11-04 00:30
        var dayStatCalls = new List<(int Year, int Month)>();
        var kasa = new FakeKasaClient
        {
            OnGetRealtime = _ => Task.FromResult(new EmeterReading(50_000, 230_000, 200, 100_000)),
            OnGetDayStat = (_, y, m) =>
            {
                dayStatCalls.Add((y, m));
                return Task.FromResult<IReadOnlyList<DailyKwh>>(new[]
                {
                    new DailyKwh(y, m, 1, 1000), new DailyKwh(y, m, 2, 2000), new DailyKwh(y, m, 3, 3000), new DailyKwh(y, m, 4, 120),
                });
            }
        };
        var bc = new PlugStateBroadcaster();
        var scopes = new TestScopeFactory(f.CreateContext);
        var worker = new PollingWorker(kasa, new FakeTapoClient(), scopes, bc, TestSettings.Create(scopes),
            TimeSpan.FromSeconds(10), NullLogger<PollingWorker>.Instance, () => now, Plus2);

        await worker.RunOnceAsync(CancellationToken.None);
        var r = bc.Snapshot()[plugId].LastReading!;
        r.TodayWh.Should().Be(120);              // local day 4
        r.MonthWh.Should().Be(6_120);
        dayStatCalls.Should().Equal((2026, 11));

        now = now.AddMinutes(4);
        await worker.RunOnceAsync(CancellationToken.None);
        dayStatCalls.Should().HaveCount(1);      // cached

        now = now.AddMinutes(2);
        await worker.RunOnceAsync(CancellationToken.None);
        dayStatCalls.Should().HaveCount(2);      // refreshed after 5 min
    }

    [Fact]
    public async Task Legacy_DayStatFailure_DoesNotFailThePoll()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedLegacyAsync(f);
        var kasa = new FakeKasaClient
        {
            OnGetRealtime = _ => Task.FromResult(new EmeterReading(50_000, 230_000, 200, 100_000)),
            OnGetDayStat = (_, _, _) => Task.FromException<IReadOnlyList<DailyKwh>>(new TimeoutException("daystat timeout")),
        };
        var bc = new PlugStateBroadcaster();
        var scopes = new TestScopeFactory(f.CreateContext);
        var worker = new PollingWorker(kasa, new FakeTapoClient(), scopes, bc, TestSettings.Create(scopes),
            TimeSpan.FromSeconds(10), NullLogger<PollingWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        var state = bc.Snapshot()[plugId];
        state.IsOnline.Should().BeTrue();
        state.LastReading!.PowerMw.Should().Be(50_000);
        state.LastReading.TodayWh.Should().BeNull();
    }

    [Fact]
    public async Task Tariff_FromSettings_FallsBackToDefault()
    {
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);
        var svc = TestSettings.Create(scopes, new KasaSettingsDefaults(TariffRonPerKwh: 1.30m));

        (await svc.GetAsync(CancellationToken.None)).TariffRonPerKwh.Should().Be(1.30m);

        using (var ctx = f.CreateContext())
            await new SettingsRepository(ctx).SetAsync(SettingKeys.TariffRonPerKwh, "1.11", CancellationToken.None);   // what /settings writes
        (await svc.GetAsync(CancellationToken.None)).TariffRonPerKwh.Should().Be(1.11m);

        using (var ctx = f.CreateContext())
            await new SettingsRepository(ctx).SetAsync(SettingKeys.TariffRonPerKwh, "abc", CancellationToken.None);
        (await svc.GetAsync(CancellationToken.None)).TariffRonPerKwh.Should().Be(1.30m);
    }
}
