using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Net;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class PlugChannelRepositoryTests
{
    private static DiscoveredPlug Channel(int channel, string alias) =>
        new(IPAddress.Parse("192.168.254.141"),
            new PlugInfo("d885ac0b743c", alias, "S3EM-002CXCEU", "EMG3", "1.6.99", "gen3", true, -58, TimeSpan.Zero),
            PlugProtocols.ShellyRpc, null, channel);

    [Fact]
    public async Task SameMac_TwoChannels_AreTwoPlugs_UpsertedByMacAndChannel()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var t0 = DateTimeOffset.Parse("2026-11-01T10:00:00Z");

        var id0 = await repo.UpsertFromDiscoveryAsync(Channel(0, "EM ch0"), t0, CancellationToken.None);
        var id1 = await repo.UpsertFromDiscoveryAsync(Channel(1, "EM ch1"), t0, CancellationToken.None);
        var again1 = await repo.UpsertFromDiscoveryAsync(Channel(1, "EM ch1 renamed on device"), t0.AddMinutes(5), CancellationToken.None);

        id0.Should().NotBe(id1);
        again1.Should().Be(id1);
        var all = await repo.ListAllAsync(CancellationToken.None);
        all.Should().HaveCount(2);
        all.Single(p => p.Channel == 1).Alias.Should().Be("EM ch1 renamed on device");
        all.Single(p => p.Channel == 1).MacLabel.Should().Be("d885ac0b743c-1");
        (await repo.GetByMacAsync("d885ac0b743c", 1, CancellationToken.None))!.Id.Should().Be(id1);
        (await repo.GetByMacAsync("d885ac0b743c", CancellationToken.None))!.Id.Should().Be(id0);
    }

    [Fact]
    public async Task DuplicateMacAndChannel_IsRejectedByTheUniqueIndex()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        ctx.Plugs.Add(new Entities.PlugEntity { Id = Guid.NewGuid(), Mac = "aa", Channel = 1 });
        ctx.Plugs.Add(new Entities.PlugEntity { Id = Guid.NewGuid(), Mac = "aa", Channel = 1 });

        var act = () => ctx.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
    }
}
