using System.Data.Common;
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using KasaMonitor.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace KasaMonitor.SqlServer.Tests;

/// <summary>
/// The retrying execution strategy on SQL Server, with the failures simulated by interceptors (a TimeoutException is
/// transient for EF's SQL Server strategy): an ambiguous commit of the live batch, a failure in the middle of SetRoleAsync.
/// </summary>
public class RetryTests
{
    /// <summary>Throws once, after the first commit went through: the answer of the commit is lost.</summary>
    private sealed class LostCommitAnswer : DbTransactionInterceptor
    {
        public int Thrown { get; private set; }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Thrown++ == 0) throw new TimeoutException("simulated: the answer of the commit was lost");
            return Task.CompletedTask;
        }
    }

    /// <summary>Throws once, before the n-th UPDATE of Plugs reaches the server.</summary>
    private sealed class FailingPlugUpdate(int nth) : DbCommandInterceptor
    {
        private int _seen;
        public bool Thrown { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!Thrown && command.CommandText.Contains("UPDATE [Plugs]", StringComparison.Ordinal) && ++_seen == nth)
            {
                Thrown = true;
                throw new TimeoutException("simulated: transient failure in the middle of the transaction");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static KasaDbContext Context(string cs, IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<KasaDbContext>().UseKasaSqlServer(cs).AddInterceptors(interceptor).Options);

    [SqlServerFact]
    public async Task LiveBatch_TheCommitWentThroughButItsAnswerWasLost_TheRetryStoresNothingTwice()
    {
        var cs = await MsSql.AppConnectionStringAsync();
        await MsSql.ResetAsync();
        var plug = Guid.NewGuid();
        var t = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        var lost = new LostCommitAnswer();

        await using (var ctx = Context(cs, lost))
            await new LiveRepository(ctx).SaveBatchAsync(
                [new LiveSample(plug, t, 500), new LiveSample(plug, t.AddSeconds(1), 510)],
                [new LiveMinuteAggregate(plug, t, 500, 505, 510, 230, 0.28, 2, 2)], CancellationToken.None);
        // Only minutes in the batch: the witness is the stored minute with its merged values.
        await using (var ctx = Context(cs, new LostCommitAnswer()))
            await new LiveRepository(ctx).SaveBatchAsync([], [new LiveMinuteAggregate(plug, t.AddMinutes(1), 400, 400, 400, 230, 0.11, 1, 1)],
                CancellationToken.None);

        lost.Thrown.Should().Be(1, "the strategy asked whether the batch was stored instead of storing it again");
        await using var check = MsSql.Context(cs);
        (await check.LiveSamples.CountAsync()).Should().Be(2);
        (await check.LiveMinuteAggregates.OrderBy(m => m.MinuteTs).Select(m => m.EnergyWh).ToListAsync()).Should().Equal(0.28, 0.11);
    }

    [SqlServerFact]
    public async Task SetRole_ATransientFailureAfterTheDemotion_TheRetryDemotesAgain_AndOneMainMeterRemains()
    {
        var cs = await MsSql.AppConnectionStringAsync();
        await MsSql.ResetAsync();
        var now = DateTimeOffset.UtcNow;
        PlugEntity Plug(string mac, int role) => new()
        {
            Id = Guid.NewGuid(), Mac = mac, IpAddress = "192.168.254.1", Alias = mac, Model = "S3EM-002CXCEU", HwVer = "EMG3", SwVer = "1", OemId = "X",
            FirstSeenUtc = now, LastSeenUtc = now, Role = role,
        };
        var a = Plug("aabbccddee00", (int)PlugRole.MainMeter);
        var b = Plug("aabbccddee01", (int)PlugRole.SubMeter);
        await using (var ctx = MsSql.Context(cs))
        {
            ctx.Plugs.AddRange(a, b);
            await ctx.SaveChangesAsync();
        }

        // The demotion of A is the first UPDATE, the promotion of B the second: it fails once, the transaction rolls back.
        var failing = new FailingPlugUpdate(nth: 2);
        await using (var ctx = Context(cs, failing))
            await new PlugRepository(ctx).SetRoleAsync(b.Id, PlugRole.MainMeter, CancellationToken.None);

        failing.Thrown.Should().BeTrue();
        await using var check = MsSql.Context(cs);
        (await check.Plugs.OrderBy(p => p.Mac).Select(p => p.Role).ToListAsync()).Should().Equal((int)PlugRole.SubMeter, (int)PlugRole.MainMeter);
    }
}
