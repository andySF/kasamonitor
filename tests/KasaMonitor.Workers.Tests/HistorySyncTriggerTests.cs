using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class HistorySyncTriggerTests
{
    private static DiscoveredPlug Legacy(string mac) =>
        new(IPAddress.Parse("192.168.1.40"), new PlugInfo(mac, "Nou", "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));

    private static FakeKasaClient HistoryKasa(Action onSync) => new()
    {
        OnGetMonthStat = (_, year) => { onSync(); return Task.FromResult<IReadOnlyList<MonthlyKwh>>(new[] { new MonthlyKwh(year, 1, 100) }); },
        OnGetDayStat = (_, year, month) => Task.FromResult<IReadOnlyList<DailyKwh>>(new[] { new DailyKwh(year, month, 1, 50) }),
    };

    [Fact]
    public void NextCycleDelay_IsOneMinuteWhileEmpty_SixHoursOtherwise()
    {
        HistorySyncWorker.NextCycleDelay(0).Should().Be(TimeSpan.FromMinutes(1));
        HistorySyncWorker.NextCycleDelay(3).Should().Be(TimeSpan.FromHours(6));
    }

    [Fact]
    public async Task NewlyDiscoveredPlug_IsSyncedImmediately_EvenWhenStartupCycleFoundNothing()
    {
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);
        var channel = new PlugDiscoveredChannel();
        var syncs = 0;
        var kasa = HistoryKasa(() => Interlocked.Increment(ref syncs));
        kasa.DiscoveredPlugs.Add(Legacy("aa"));
        var settings = TestSettings.Create(scopes);
        var discovery = new PlugDiscoveryService(kasa, new FakeTapoClient(), scopes, settings, channel,
            TimeSpan.FromMilliseconds(100), NullLogger<PlugDiscoveryService>.Instance);
        var worker = new HistorySyncWorker(kasa, scopes, () => DateTime.UtcNow, NullLogger<HistorySyncWorker>.Instance,
            new FakeTapoClient(), settings, discovered: channel);

        await worker.StartAsync(CancellationToken.None);   // startup cycle: no plugs yet
        try
        {
            // Let the startup cycle finish on the empty database (it then sleeps 1 min), so the sync below can only
            // come from the discovery channel.
            await Task.Delay(300);
            await discovery.ScanAsync(CancellationToken.None);   // auto-add publishes the new plug

            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref syncs) == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        syncs.Should().BeGreaterThan(0);
        using var ctx = f.CreateContext();
        var plug = (await new PlugRepository(ctx).ListAllAsync(CancellationToken.None)).Single();
        (await new HistoryRepository(ctx).GetDailyRangeAsync(plug.Id, new DateOnly(2000, 1, 1), new DateOnly(2100, 1, 1), CancellationToken.None))
            .Should().NotBeEmpty();
    }

    [Fact]
    public async Task PlugThatBecomesSupported_IsPublishedForHistorySync()
    {
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);
        var channel = new PlugDiscoveredChannel();
        var tapo = new FakeTapoClient();
        tapo.Responses.Add(new TapoDiscoveryResponse(IPAddress.Parse("192.168.1.41"), "b01921c4c028", "SMART.TAPOPLUG", "P115(EU)", "KLAP", 80, 2, false));
        var settings = TestSettings.Create(scopes);
        var discovery = new PlugDiscoveryService(new FakeKasaClient(), tapo, scopes, settings, channel,
            TimeSpan.FromMilliseconds(100), NullLogger<PlugDiscoveryService>.Instance);

        await discovery.ScanAsync(CancellationToken.None);   // no credentials: added as unsupported (published as new)
        await Drain(channel);

        await settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "user@example.com", "pw", false), CancellationToken.None);
        tapo.OnGetDeviceInfo = (_, _) => Task.FromResult(new PlugInfo("b01921c4c028", "Frigider", "P115", "1.0", "1.4.3", "X", true, -40, TimeSpan.Zero));
        await discovery.ScanAsync(CancellationToken.None);   // now supported

        (await Drain(channel)).Should().ContainSingle();
    }

    private static async Task<List<Guid>> Drain(PlugDiscoveredChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var got = new List<Guid>();
        try { await foreach (var id in channel.ReadAllAsync(cts.Token)) got.Add(id); }
        catch (OperationCanceledException) { }
        return got;
    }
}
