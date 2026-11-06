using System.Diagnostics;
using FluentAssertions;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data.Entities;
using KasaMonitor.Data.Repositories;
using Xunit;
using Xunit.Abstractions;

namespace KasaMonitor.Data.Tests;

/// <summary>The minute and hour power aggregates (0.14.0): read by plug and range, hours written without ever shrinking.</summary>
public class PowerAggregateRepositoryTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private readonly ITestOutputHelper _out;

    public PowerAggregateRepositoryTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Hours_RoundTrip_ByPlugAndRange_OldestFirst()
    {
        using var f = new TestDbFactory();
        var plug = Guid.NewGuid();
        var other = Guid.NewGuid();
        using (var ctx = f.CreateContext())
        {
            var repo = new PowerAggregateRepository(ctx);
            (await repo.UpsertHoursAsync(plug, new[]
            {
                new PowerHour(T0.AddHours(1), 2_000_000, 3_000_000, 60, 2000),
                new PowerHour(T0, 1_000_000, 1_500_000, 45, 750),
            }, Ct)).Should().Be(2);
            await repo.UpsertHoursAsync(other, new[] { new PowerHour(T0, 9_000_000, 9_000_000, 60, 9000) }, Ct);
        }

        using var read = f.CreateContext();
        var hours = await new PowerAggregateRepository(read).ListHoursAsync(plug, T0, T0.AddHours(2), Ct);
        hours.Should().Equal(new PowerAggregate(T0, 1_000_000, 1_500_000, 45), new PowerAggregate(T0.AddHours(1), 2_000_000, 3_000_000, 60));
        (await new PowerAggregateRepository(read).ListHoursAsync(plug, T0, T0.AddHours(1), Ct)).Should().ContainSingle("the end is exclusive");
        read.OneHourAggregates.Single(a => a.PlugId == plug && a.BucketStartUtc == T0).EnergyWh.Should().Be(750);
    }

    [Fact]
    public async Task UpsertHours_ReplacesWithAsManyMinutes_NeverWithFewer()
    {
        using var f = new TestDbFactory();
        var plug = Guid.NewGuid();
        using (var ctx = f.CreateContext())
            await new PowerAggregateRepository(ctx).UpsertHoursAsync(plug, new[] { new PowerHour(T0, 1_000_000, 2_000_000, 60, 1000) }, Ct);

        using (var ctx = f.CreateContext())
        {
            var repo = new PowerAggregateRepository(ctx);
            (await repo.UpsertHoursAsync(plug, new[] { new PowerHour(T0, 50_000, 60_000, 5, 4) }, Ct)).Should().Be(0, "5 minutes left after the retention");
            (await repo.UpsertHoursAsync(plug, new[] { new PowerHour(T0, 1_000_000, 2_000_000, 60, 1000) }, Ct)).Should().Be(0, "the same hour again: nothing to write");
            (await repo.UpsertHoursAsync(plug, new[] { new PowerHour(T0, 1_100_000, 2_500_000, 60, 1100) }, Ct)).Should().Be(1);
        }

        using var read = f.CreateContext();
        (await new PowerAggregateRepository(read).ListHoursAsync(plug, T0, T0.AddHours(1), Ct)).Single()
            .Should().Be(new PowerAggregate(T0, 1_100_000, 2_500_000, 60));
    }

    [Fact]
    public async Task Minutes_ByRange_SpanAndHourDays()
    {
        using var f = new TestDbFactory();
        var plug = Guid.NewGuid();
        using (var ctx = f.CreateContext())
        {
            for (var i = 0; i < 3; i++)
                ctx.OneMinuteAggregates.Add(new OneMinuteAggregateEntity
                    { PlugId = plug, BucketStartUtc = T0.AddDays(-i).AddMinutes(i), PAvgMw = 1000 * (i + 1), PMaxMw = 2000 * (i + 1), PMinMw = 0, SampleCount = 6, OnlineCount = 6 });
            await ctx.SaveChangesAsync(Ct);
            await new PowerAggregateRepository(ctx).UpsertHoursAsync(plug, new[] { new PowerHour(T0.AddDays(-1), 1, 1, 1, 0) }, Ct);
        }

        using var read = f.CreateContext();
        var repo = new PowerAggregateRepository(read);
        (await repo.ListMinutesAsync(plug, T0.AddDays(-1), T0.AddDays(1), Ct))
            .Should().Equal(new PowerAggregate(T0.AddDays(-1).AddMinutes(1), 2000, 4000, 1), new PowerAggregate(T0, 1000, 2000, 1));
        (await repo.MinuteSpanAsync(plug, Ct)).Should().Be((T0.AddDays(-2).AddMinutes(2), T0));
        (await repo.MinuteSpanAsync(Guid.NewGuid(), Ct)).Should().BeNull();
        (await repo.HourDaysAsync(plug, Ct)).Should().BeEquivalentTo(new[] { new DateOnly(2026, 11, 11) });
    }

    [Fact]
    public async Task AYearOfHours_ForThreePlugs_IsReadWellUnderTwoSeconds()
    {
        using var f = new TestDbFactory();
        var plugs = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var start = new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero);
        var hours = Enumerable.Range(0, 365 * 24).Select(h => new PowerHour(start.AddHours(h), 400_000 + h % 24 * 50_000, 3_000_000, 60, 400)).ToList();
        foreach (var plug in plugs)
        {
            using var ctx = f.CreateContext();
            await new PowerAggregateRepository(ctx).UpsertHoursAsync(plug, hours, Ct);
        }

        var sw = Stopwatch.StartNew();
        var rows = 0;
        foreach (var plug in plugs)
        {
            using var ctx = f.CreateContext();
            rows += (await new PowerAggregateRepository(ctx).ListHoursAsync(plug, start, start.AddDays(365), Ct)).Count;
        }
        sw.Stop();
        _out.WriteLine($"A year of hour rows for 3 plugs ({rows} rows) read in {sw.ElapsedMilliseconds} ms");
        rows.Should().Be(3 * 365 * 24);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }
}
