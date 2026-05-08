using FluentAssertions;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class SettingsRepositoryTests
{
    [Fact]
    public async Task Get_AbsentKey_ReturnsNull()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);
        (await repo.GetAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Set_NewKey_PersistsValue()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("Tariff", "1.30", CancellationToken.None);
        (await repo.GetAsync("Tariff", CancellationToken.None)).Should().Be("1.30");
    }

    [Fact]
    public async Task Set_ExistingKey_OverwritesValue()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("Tariff", "1.30", CancellationToken.None);
        await repo.SetAsync("Tariff", "1.45", CancellationToken.None);
        (await repo.GetAsync("Tariff", CancellationToken.None)).Should().Be("1.45");
    }

    [Fact]
    public async Task GetAll_ReturnsAllKeys()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("a", "1", CancellationToken.None);
        await repo.SetAsync("b", "2", CancellationToken.None);

        var all = await repo.GetAllAsync(CancellationToken.None);
        all.Should().ContainKey("a").WhoseValue.Should().Be("1");
        all.Should().ContainKey("b").WhoseValue.Should().Be("2");
    }
}
