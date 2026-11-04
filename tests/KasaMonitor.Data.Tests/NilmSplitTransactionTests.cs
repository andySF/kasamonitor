using System.Data.Common;
using FluentAssertions;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Data.Repositories;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace KasaMonitor.Data.Tests;

/// <summary>
/// A failure in the middle of a split by duration leaves nothing behind (0.13.0). The name keeps this file out of the
/// SQL Server suite's linked repository tests: it needs the interceptor; the same check on SQL Server is in RetryTests.
/// </summary>
public class NilmSplitTransactionTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    /// <summary>Throws before the UPDATE of ApplianceRuns: signatures and events are already written in the transaction.</summary>
    private sealed class FailingRunUpdate : DbCommandInterceptor
    {
        public bool Thrown { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE", StringComparison.Ordinal) && command.CommandText.Contains("ApplianceRuns", StringComparison.Ordinal))
            {
                Thrown = true;
                throw new InvalidOperationException("simulated: the server went away");
            }
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static ApplianceSignature New(string name) =>
        new(0, name, "i", "cooking", 1430, 90, 1, 0, null, SignatureLabelSource.User, null, false);

    private static async Task<(ApplianceSignature Source, IReadOnlyList<MeterEvent> Events, IReadOnlyList<ApplianceRun> Runs)> SeedAsync(TestDbFactory db)
    {
        var repo = new NilmRepository(db.CreateContext());
        var s = await repo.AddSignatureAsync(New("Fierbător"), CancellationToken.None);
        var events = await repo.AddEventsAsync(new[]
        {
            new MeterEvent(0, Guid.Empty, T, 1430, 90, 0, 1, 0, 300, s.Id),
            new MeterEvent(0, Guid.Empty, T.AddSeconds(15), -1430, -90, 0, 1, 0, 1730, s.Id),
        }, CancellationToken.None);
        var runs = await repo.SaveRunsAsync(new[] { new ApplianceRun(0, s.Id, events[0].TsUtc, events[1].TsUtc, 1430, 6, events[0].Id, events[1].Id) }, CancellationToken.None);
        return (s, events, runs);
    }

    [Fact]
    public async Task AFailureAfterTheSignaturesAndTheEvents_NoNewSignature_NoBand_NothingMoved_AndASecondAttemptWorks()
    {
        using var db = new TestDbFactory();
        var (s, events, runs) = await SeedAsync(db);
        var (source, target) = DurationBands.AfterSplit(s, New("Espressor"), 40);
        var failing = new FailingRunUpdate();

        await new NilmRepository(db.CreateContext(failing)).Invoking(r => r.ApplyDurationSplitAsync(source, target, runs, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        failing.Thrown.Should().BeTrue("the failure came in the middle, after the signatures and the events");
        var fresh = new NilmRepository(db.CreateContext());
        (await fresh.ListSignaturesAsync(CancellationToken.None)).Should().ContainSingle().Which.Should().BeEquivalentTo(s);
        (await fresh.ListEventsAsync(T, T.AddMinutes(1), CancellationToken.None)).Should().BeEquivalentTo(events);
        (await fresh.ListRunsAsync(T, T.AddMinutes(1), CancellationToken.None)).Should().BeEquivalentTo(runs);

        var stored = await fresh.ApplyDurationSplitAsync(source, target, runs, CancellationToken.None);
        (await fresh.ListRunsBySignatureAsync(stored.Id, T, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task AFailureWithAnExistingTarget_LeavesItsNameBandAndEventsAsTheyWere()
    {
        using var db = new TestDbFactory();
        var (s, _, runs) = await SeedAsync(db);
        var existing = await new NilmRepository(db.CreateContext()).AddSignatureAsync(New("Espressor"), CancellationToken.None);
        var (source, target) = DurationBands.AfterSplit(s, existing with { Category = "other" }, 40);

        await new NilmRepository(db.CreateContext(new FailingRunUpdate())).Invoking(r => r.ApplyDurationSplitAsync(source, target, runs, CancellationToken.None))
            .Should().ThrowAsync<InvalidOperationException>();

        var fresh = new NilmRepository(db.CreateContext());
        (await fresh.GetSignatureAsync(existing.Id, CancellationToken.None)).Should().BeEquivalentTo(existing);
        (await fresh.GetSignatureAsync(s.Id, CancellationToken.None)).Should().BeEquivalentTo(s);
        (await fresh.ListEventsBySignaturesAsync(new[] { existing.Id }, T, CancellationToken.None)).Should().BeEmpty();
    }
}
