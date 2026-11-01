using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class TapoHistorySyncTests
{
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");
    private static readonly DateTime NowUtc = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);   // local 2026-11-01 12:00

    private static async Task<Guid> SeedAsync(TestDbFactory f, string protocol)
    {
        using var ctx = f.CreateContext();
        var dp = new DiscoveredPlug(IPAddress.Parse("192.168.1.30"),
            new PlugInfo("b0" + protocol, "Frigider", "P115", "1.0", "1.4.3", "X", true, -50, TimeSpan.Zero), protocol);
        return await new PlugRepository(ctx).UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static (HistorySyncWorker Worker, FakeKasaClient Kasa) Create(TestDbFactory f, FakeTapoClient tapo, bool withCredentials = true)
    {
        var scopes = new TestScopeFactory(f.CreateContext);
        var settings = TestSettings.Create(scopes, withCredentials
            ? new KasaSettingsDefaults(TapoUsername: "user@example.com", TapoPassword: "pw")
            : new KasaSettingsDefaults());
        var kasa = new FakeKasaClient();
        return (new HistorySyncWorker(kasa, scopes, () => NowUtc, NullLogger<HistorySyncWorker>.Instance, tapo, settings, Plus2), kasa);
    }

    /// <summary>Device that answers every day of the requested range with Wh = 100 * month + day.</summary>
    private static Task<IReadOnlyList<DailyKwh>> Answer(DateOnly first, DateOnly last)
    {
        var list = new List<DailyKwh>();
        for (var d = first; d <= last; d = d.AddDays(1)) list.Add(new DailyKwh(d.Year, d.Month, d.Day, 100 * d.Month + d.Day));
        return Task.FromResult<IReadOnlyList<DailyKwh>>(list);
    }

    private static async Task<IReadOnlyList<DailyKwh>> DailyAsync(TestDbFactory f, Guid plugId)
    {
        using var ctx = f.CreateContext();
        return await new HistoryRepository(ctx).GetDailyRangeAsync(plugId, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), CancellationToken.None);
    }

    [Theory]
    [InlineData(PlugProtocols.Klap)]
    [InlineData(PlugProtocols.Aes)]
    public async Task TapoPlug_SyncsLast60Days_InMonthlyRequests(string protocol)
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, protocol);
        var tapo = new FakeTapoClient { OnGetDailyEnergy = Answer };
        var (worker, _) = Create(f, tapo);

        await worker.SyncOneAsync(plugId, CancellationToken.None);

        // today = 2026-11-01 (local), window = 60 days = 2026-09-03 .. 2026-11-01
        tapo.DailyEnergyRequests.Should().Equal(
            (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            (new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
            (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1)));
        var days = await DailyAsync(f, plugId);
        days.Should().HaveCount(HistorySyncWorker.TapoHistoryDays);
        days.First().Should().Be(new DailyKwh(2026, 9, 3, 903));
        days.Last().Should().Be(new DailyKwh(2026, 11, 1, 1101));
    }

    [Fact]
    public async Task Sync_IsIdempotent_AndUpdatesValues()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, PlugProtocols.Klap);
        var factor = 1;
        var tapo = new FakeTapoClient
        {
            OnGetDailyEnergy = async (a, b) => (await Answer(a, b)).Select(d => d with { EnergyWh = d.EnergyWh * factor }).ToList()
        };
        var (worker, _) = Create(f, tapo);

        await worker.SyncOneAsync(plugId, CancellationToken.None);
        factor = 2;
        await worker.SyncOneAsync(plugId, CancellationToken.None);

        var days = await DailyAsync(f, plugId);
        days.Should().HaveCount(HistorySyncWorker.TapoHistoryDays);   // no duplicates
        days.Last().EnergyWh.Should().Be(2 * 1101);                     // upserted
    }

    [Fact]
    public async Task MonthRequestRejected_FallsBackToQuarterStart()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, PlugProtocols.Klap);
        var tapo = new FakeTapoClient
        {
            OnGetDailyEnergy = (first, last) => first.Day == 1 && (first.Month - 1) % 3 == 0
                ? Answer(first, last)                                                      // quarter starts accepted
                : Task.FromException<IReadOnlyList<DailyKwh>>(new KasaProtocolException("bad params", -1008, "get_energy_data"))
        };
        var (worker, _) = Create(f, tapo);

        await worker.SyncOneAsync(plugId, CancellationToken.None);

        tapo.DailyEnergyRequests.Should().Equal(
            (new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),     // rejected
            (new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30)),     // Q3
            (new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),   // accepted (October 1 is a quarter start)
            (new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1)),    // rejected
            (new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1)));   // Q4 up to today

        var days = await DailyAsync(f, plugId);
        days.Should().HaveCount(HistorySyncWorker.TapoHistoryDays);    // window still complete, no duplicates
        days.Last().Should().Be(new DailyKwh(2026, 11, 1, 1101));
    }

    [Fact]
    public async Task WithoutCredentials_NothingIsRequested()
    {
        using var f = new TestDbFactory();
        var plugId = await SeedAsync(f, PlugProtocols.Klap);
        var tapo = new FakeTapoClient { OnGetDailyEnergy = Answer };
        var (worker, _) = Create(f, tapo, withCredentials: false);

        await worker.SyncOneAsync(plugId, CancellationToken.None);

        tapo.DailyEnergyRequests.Should().BeEmpty();
        (await DailyAsync(f, plugId)).Should().BeEmpty();
    }

    [Fact]
    public void HasHistory_LegacyAlways_TapoOnlyWithClient()
    {
        var legacy = new Plug(Guid.NewGuid(), "a", IPAddress.Loopback, "A", false, "KP115", "", "", "",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null);
        var klap = legacy with { Protocol = PlugProtocols.Klap };
        var withdrawn = klap with { IsArchived = true };
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);

        var legacyOnly = new HistorySyncWorker(new FakeKasaClient(), scopes, () => NowUtc, NullLogger<HistorySyncWorker>.Instance);
        var (full, _) = Create(f, new FakeTapoClient());

        legacyOnly.HasHistory(legacy).Should().BeTrue();
        legacyOnly.HasHistory(klap).Should().BeFalse();
        full.HasHistory(klap).Should().BeTrue();
        full.HasHistory(withdrawn).Should().BeFalse();
    }
}
