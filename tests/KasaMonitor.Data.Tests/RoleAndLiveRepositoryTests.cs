using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using System.Net;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class RoleAndLiveRepositoryTests
{
    private static DiscoveredPlug Shelly(int channel, PlugRole role) =>
        new(IPAddress.Parse("192.168.254.141"),
            new PlugInfo("d885ac0b743c", $"EM ch{channel}", "S3EM-002CXCEU", "EMG3", "2.1.1", "gen3", true, -50, TimeSpan.Zero),
            PlugProtocols.ShellyRpc, null, channel, role);

    [Fact]
    public async Task NewPlug_TakesTheDiscoveredRole_ExistingPlugKeepsTheChosenRole()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);

        var id = await repo.UpsertFromDiscoveryAsync(Shelly(0, PlugRole.SubMeter), DateTimeOffset.UtcNow, CancellationToken.None);
        (await repo.ListAllAsync(CancellationToken.None)).Single().Role.Should().Be(PlugRole.SubMeter);

        await repo.SetRoleAsync(id, PlugRole.MainMeter, CancellationToken.None);
        await repo.UpsertFromDiscoveryAsync(Shelly(0, PlugRole.SubMeter), DateTimeOffset.UtcNow, CancellationToken.None);   // next discovery

        (await repo.ListAllAsync(CancellationToken.None)).Single().Role.Should().Be(PlugRole.MainMeter);
    }

    [Fact]
    public async Task MainMeter_IsUnique_ThePreviousOneBecomesASubMeter()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var ch0 = await repo.UpsertFromDiscoveryAsync(Shelly(0, PlugRole.SubMeter), DateTimeOffset.UtcNow, CancellationToken.None);
        var ch1 = await repo.UpsertFromDiscoveryAsync(Shelly(1, PlugRole.SubMeter), DateTimeOffset.UtcNow, CancellationToken.None);

        await repo.SetRoleAsync(ch0, PlugRole.MainMeter, CancellationToken.None);
        await repo.SetRoleAsync(ch1, PlugRole.MainMeter, CancellationToken.None);

        using var check = f.CreateContext();
        var plugs = (await new PlugRepository(check).ListAllAsync(CancellationToken.None)).ToDictionary(p => p.Id);
        plugs[ch1].Role.Should().Be(PlugRole.MainMeter);
        plugs[ch0].Role.Should().Be(PlugRole.SubMeter);
        plugs.Values.Count(p => p.Role == PlugRole.MainMeter).Should().Be(1);

        plugs[ch0].StreamEnabled.Should().BeFalse();   // demoted to sub-meter: its default stream setting (off)
        plugs[ch1].StreamEnabled.Should().BeTrue();

        await repo.SetRoleAsync(ch1, PlugRole.Plug, CancellationToken.None);   // no main meter at all is allowed
        (await new PlugRepository(f.CreateContext()).ListAllAsync(CancellationToken.None)).Should().NotContain(p => p.Role == PlugRole.MainMeter);
    }

    [Fact]
    public async Task LiveBatch_StoresSamples_AndMergesASecondPartOfTheSameMinute()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new LiveRepository(ctx);
        var plug = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

        await repo.SaveBatchAsync(new[] { new LiveSample(plug, t, 500, 600, 230.1, 2.5, 0.9, 50), new LiveSample(plug, t.AddSeconds(1), 510) },
            new[] { new LiveMinuteAggregate(plug, t, 500, 505, 510, 230.1, 0.28, 2, 2) }, CancellationToken.None);
        // A late sample after the minute was closed (or a restart mid-minute): a second partial aggregate of 18:00.
        await repo.SaveBatchAsync(Array.Empty<LiveSample>(),
            new[] { new LiveMinuteAggregate(plug, t, 400, 400, 400, 229.9, 0.111, 1, 1) }, CancellationToken.None);

        var samples = await repo.ListSamplesAsync(plug, t, t.AddMinutes(1), CancellationToken.None);
        samples.Should().HaveCount(2);
        samples[0].Should().Be(new LiveSample(plug, t, 500, 600, 230.1, 2.5, 0.9, null));   // frequency is not stored
        var minutes = await repo.ListMinutesAsync(plug, t, t.AddMinutes(1), CancellationToken.None);
        var m = minutes.Should().ContainSingle().Subject;
        m.SampleCount.Should().Be(3);                       // merged, not overwritten
        m.CoveredSeconds.Should().Be(3);
        m.MinW.Should().Be(400);
        m.MaxW.Should().Be(510);
        m.AvgW.Should().BeApproximately((505 * 2 + 400) / 3.0, 1e-9);
        m.EnergyWh.Should().BeApproximately(0.391, 1e-9);
        m.AvgVoltageV!.Value.Should().BeApproximately((230.1 * 2 + 229.9) / 3, 1e-9);
    }

    [Fact]
    public async Task RelayCommands_NewestFirst_LimitedAndPerPlug()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new LiveRepository(ctx);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 25; i++)
            await repo.AddRelayCommandAsync(new RelayCommand(0, t.AddSeconds(i), i % 5 == 0 ? b : a, i % 2 == 0, RelayCommandSources.Ui, "ok"), CancellationToken.None);

        var last20 = await repo.ListRelayCommandsAsync(null, 20, CancellationToken.None);
        last20.Should().HaveCount(20);
        last20[0].TsUtc.Should().Be(t.AddSeconds(24));
        last20.Should().BeInDescendingOrder(c => c.TsUtc);
        (await repo.ListRelayCommandsAsync(b, 20, CancellationToken.None)).Should().HaveCount(5).And.OnlyContain(c => c.PlugId == b);
    }
}
