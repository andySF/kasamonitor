using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class ReadingsRetentionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private static async Task SeedAsync(TestDbFactory f, Guid plugId, params DateTimeOffset[] times)
    {
        using var ctx = f.CreateContext();
        await new SampleRepository(ctx).InsertManyAsync(times.Select(t =>
            new Sample(0, plugId, t, true, 1000, 230_000, 10, 1, null, null, null)).ToList(), CancellationToken.None);
    }

    private static async Task<int> CountSamplesAsync(TestDbFactory f)
    {
        using var ctx = f.CreateContext();
        return ctx.Samples.Count();
    }

    private static ReadingsRetentionService Create(TestDbFactory f) =>
        new(new TestScopeFactory(f.CreateContext), NullLogger<ReadingsRetentionService>.Instance, () => Now);

    [Fact]
    public void Cutoff_IsNowMinusMonths_NullForUnlimited()
    {
        ReadingsRetentionService.CutoffFor(0, Now).Should().BeNull();
        ReadingsRetentionService.CutoffFor(-1, Now).Should().BeNull();
        ReadingsRetentionService.CutoffFor(3, Now).Should().Be(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        ReadingsRetentionService.CutoffFor(12, Now).Should().Be(new DateTimeOffset(2025, 11, 1, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Plan_CountsRowsOlderThanCutoff_AndAsksOnlyThen()
    {
        using var f = new TestDbFactory();
        var plug = Guid.NewGuid();
        await SeedAsync(f, plug, Now.AddMonths(-5), Now.AddMonths(-4), Now.AddMonths(-3).AddSeconds(-1), Now.AddMonths(-3), Now.AddDays(-1));
        var svc = Create(f);

        var three = await svc.PlanAsync(3, CancellationToken.None);
        three.CutoffUtc.Should().Be(Now.AddMonths(-3));
        three.RowsToDelete.Should().Be(3);           // strictly older than the cutoff
        three.NeedsConfirmation.Should().BeTrue();

        var six = await svc.PlanAsync(6, CancellationToken.None);
        six.RowsToDelete.Should().Be(0);
        six.NeedsConfirmation.Should().BeFalse();

        var unlimited = await svc.PlanAsync(0, CancellationToken.None);
        unlimited.CutoffUtc.Should().BeNull();
        unlimited.NeedsConfirmation.Should().BeFalse();
        (await CountSamplesAsync(f)).Should().Be(5);  // planning never deletes
    }

    [Fact]
    public async Task Apply_DeletesOnlyOldReadings_AndNeverTheDailyHistory()
    {
        using var f = new TestDbFactory();
        var plug = Guid.NewGuid();
        await SeedAsync(f, plug, Now.AddMonths(-5), Now.AddMonths(-4), Now.AddDays(-1));
        using (var ctx = f.CreateContext())
            await new HistoryRepository(ctx).UpsertDailyAsync(plug, new[] { new DailyKwh(2026, 5, 1, 500) }, CancellationToken.None);
        var svc = Create(f);

        (await svc.ApplyAsync(0, CancellationToken.None)).Should().Be(0);
        (await CountSamplesAsync(f)).Should().Be(3);

        (await svc.ApplyAsync(3, CancellationToken.None)).Should().Be(2);
        (await CountSamplesAsync(f)).Should().Be(1);
        using var read = f.CreateContext();
        (await new HistoryRepository(read).GetDailyRangeAsync(plug, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), CancellationToken.None))
            .Should().ContainSingle();
    }

    [Fact]
    public async Task RollupWorker_DefaultRetention_KeepsEverything()
    {
        using var f = new TestDbFactory();
        await SeedAsync(f, Guid.NewGuid(), DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-40));
        var scopes = new TestScopeFactory(f.CreateContext);
        var worker = new RollupWorker(scopes, NullLogger<RollupWorker>.Instance,
            new ReadingsRetentionService(scopes, NullLogger<ReadingsRetentionService>.Instance), TestSettings.Create(scopes));

        (await worker.ApplyRetentionAsync(CancellationToken.None)).Should().Be(0);
        (await CountSamplesAsync(f)).Should().Be(2);   // the old hard-coded 30-day purge is gone
    }

    [Fact]
    public async Task RollupWorker_UsesSavedRetentionMonths()
    {
        using var f = new TestDbFactory();
        await SeedAsync(f, Guid.NewGuid(), DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddDays(-1));
        var scopes = new TestScopeFactory(f.CreateContext);
        var settings = TestSettings.Create(scopes);
        await settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false, ReadingsRetentionMonths: 12), CancellationToken.None);
        var worker = new RollupWorker(scopes, NullLogger<RollupWorker>.Instance,
            new ReadingsRetentionService(scopes, NullLogger<ReadingsRetentionService>.Instance), settings);

        (await worker.ApplyRetentionAsync(CancellationToken.None)).Should().Be(1);
        (await CountSamplesAsync(f)).Should().Be(1);
    }

    [Fact]
    public async Task Settings_RetentionDefaultsToUnlimited_AndNullKeepsStoredValue()
    {
        using var f = new TestDbFactory();
        var scopes = new TestScopeFactory(f.CreateContext);
        var settings = TestSettings.Create(scopes);

        (await settings.GetAsync(CancellationToken.None)).ReadingsRetentionMonths.Should().Be(0);
        await settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false, ReadingsRetentionMonths: 6), CancellationToken.None);
        await settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false), CancellationToken.None);
        (await settings.GetAsync(CancellationToken.None)).ReadingsRetentionMonths.Should().Be(6);
    }
}
