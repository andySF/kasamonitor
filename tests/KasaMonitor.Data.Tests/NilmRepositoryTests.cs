using FluentAssertions;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class NilmRepositoryTests
{
    private static readonly Guid Main = Guid.NewGuid();
    private static readonly DateTimeOffset T = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private static MeterEvent E(int second, double p, long? sig = null) => new(0, Main, T.AddSeconds(second), p, 10, 0.01, 4, 2, 300, sig);

    private static async Task<MeterEvent> AddOne(NilmRepository repo, MeterEvent e) => (await repo.AddEventsAsync(new[] { e }, CancellationToken.None)).Single();

    [Fact]
    public async Task Events_DuplicatesWithinTwoSecondsSkipped_LabelsRoundTrip()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        var first = await AddOne(repo, E(0, 120));
        first.Id.Should().BeGreaterThan(0);
        (await repo.AddEventsAsync(new[] { E(0, 125) }, CancellationToken.None)).Should().BeEmpty("same second");
        (await repo.AddEventsAsync(new[] { E(1, 118) }, CancellationToken.None)).Should().BeEmpty("re-detected one second later after a restart");
        (await repo.AddEventsAsync(new[] { E(1, -120), E(2, 400), E(30, 120) }, CancellationToken.None)).Should().HaveCount(3,
            "another direction, another size, or later: not duplicates");

        var plug = Guid.NewGuid();
        await repo.UpdateEventAsync(first with { Label = "Frigider", Source = EventLabelSource.AutoPlug, LabelPlugId = plug }, CancellationToken.None);
        var back = await new NilmRepository(db.CreateContext()).GetEventAsync(first.Id, CancellationToken.None);
        back.Should().BeEquivalentTo(first with { Label = "Frigider", Source = EventLabelSource.AutoPlug, LabelPlugId = plug });
        (await repo.ListLabeledEventsAsync(Array.Empty<Guid>(), 100, CancellationToken.None)).Should().ContainSingle();
        (await repo.ListLabeledEventsAsync(new[] { plug }, 100, CancellationToken.None)).Should().BeEmpty("labels of an excluded plug do not teach");
        (await repo.CountLabeledAsync(Array.Empty<Guid>(), CancellationToken.None)).Should().Be((1, 1));
        (await repo.CountEventsAsync(CancellationToken.None)).Should().Be(4);
        (await repo.CountEventsAsync(T, T.AddSeconds(2), CancellationToken.None)).Should().Be(3);
        (await repo.ListEventsPageAsync(T, T.AddMinutes(1), 0, 2, CancellationToken.None)).Select(e => e.TsUtc)
            .Should().Equal(T.AddSeconds(30), T.AddSeconds(2));
        (await repo.ListNewestEventsAsync(T, T.AddMinutes(1), 2, CancellationToken.None)).Select(e => e.TsUtc)
            .Should().Equal(T.AddSeconds(2), T.AddSeconds(30));
    }

    [Fact]
    public async Task Signatures_DefaultName_SqlRecompute_PlugShare_AndDeleteClearsEventsAndRuns()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, "", "bi-question-circle", "other", 0, 0, 0, 0, null,
            SignatureLabelSource.Cluster, null, false), CancellationToken.None);
        sig.Name.Should().Be($"Necunoscut #{sig.Id}");

        var plug = Guid.NewGuid();
        var on = await AddOne(repo, E(0, 120, sig.Id) with { Label = "Frigider", Source = EventLabelSource.AutoPlug, LabelPlugId = plug });
        var off = await AddOne(repo, E(600, -110, sig.Id) with { DeltaQ = -20 });
        var updated = (await repo.RecomputeSignaturesAsync(new[] { sig.Id }, T.AddDays(-30), CancellationToken.None)).Single();
        updated.Count.Should().Be(2);
        updated.MeanDeltaP.Should().Be(115);
        updated.MeanDeltaQ.Should().Be(15);
        updated.MeanTransient.Should().Be(4);
        updated.LastSeenUtc.Should().Be(T.AddSeconds(600));
        (await repo.RecomputeSignaturesAsync(new[] { sig.Id }, T.AddSeconds(1), CancellationToken.None)).Single().Count.Should().Be(1, "window");

        var share = (await repo.PlugSharesAsync(new[] { sig.Id }, T.AddDays(-30), Array.Empty<Guid>(), CancellationToken.None)).Single();
        share.Should().Be(new KasaMonitor.Core.Abstractions.SignaturePlugShare(sig.Id, 2, plug, "Frigider", 1));
        (await repo.PlugSharesAsync(new[] { sig.Id }, T.AddDays(-30), new[] { plug }, CancellationToken.None)).Single().TopPlugId.Should().BeNull();

        var runs = await repo.SaveRunsAsync(new[] { new ApplianceRun(0, sig.Id, T, null, 120, null, on.Id, null, 300) }, CancellationToken.None);
        (await repo.ListOpenRunsAsync(CancellationToken.None)).Should().ContainSingle().Which.HouseBeforeW.Should().Be(300);
        await repo.SaveRunsAsync(new[] { runs[0] with { EndUtc = T.AddSeconds(600), EndEventId = off.Id, EstimatedWh = 20, EndReason = RunEndReason.HouseBelowLevel } },
            CancellationToken.None);
        (await repo.ListOpenRunsAsync(CancellationToken.None)).Should().BeEmpty();
        var closed = (await repo.ListRunsAsync(T.AddSeconds(300), T.AddHours(1), CancellationToken.None)).Should().ContainSingle().Subject;
        closed.EstimatedWh.Should().Be(20);
        closed.EndReason.Should().Be(RunEndReason.HouseBelowLevel);
        (await repo.ListRunsAsync(T.AddHours(1), T.AddHours(2), CancellationToken.None)).Should().BeEmpty();

        await repo.DeleteSignatureAsync(sig.Id, CancellationToken.None);
        (await repo.ListSignaturesAsync(CancellationToken.None)).Should().BeEmpty();
        (await repo.ListRunsAsync(T.AddDays(-1), T.AddDays(1), CancellationToken.None)).Should().BeEmpty();
        (await repo.ListEventsAsync(T.AddDays(-1), T.AddDays(1), CancellationToken.None)).Should().OnlyContain(e => e.SignatureId == null);
    }

    [Fact]
    public async Task SetSignatures_EventsBySignature_DeleteRunsFrom_AndRetention()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        var a = await AddOne(repo, E(0, 120));
        var b = await AddOne(repo, E(10, 300));
        await repo.SetSignaturesAsync(new Dictionary<long, long?> { [a.Id] = 5, [b.Id] = 6 }, CancellationToken.None);
        (await repo.ListEventsBySignaturesAsync(new long[] { 5 }, T.AddDays(-1), CancellationToken.None)).Should().ContainSingle().Which.Id.Should().Be(a.Id);
        (await repo.ListEventsBySignaturesAsync(new long[] { 5, 6 }, T.AddSeconds(5), CancellationToken.None)).Should().ContainSingle().Which.Id.Should().Be(b.Id);

        await repo.SaveRunsAsync(new[]
        {
            new ApplianceRun(0, 5, T, null, 120, null, a.Id, null),
            new ApplianceRun(0, 6, T.AddHours(2), null, 300, null, b.Id, null),
        }, CancellationToken.None);
        (await repo.DeleteRunsAsync(new long[] { 5, 6 }, T.AddHours(1), CancellationToken.None)).Should().Be(1, "only the run started after the cutoff");
        (await repo.ListOpenRunsAsync(CancellationToken.None)).Should().ContainSingle().Which.StartUtc.Should().Be(T);

        (await repo.DeleteOlderThanAsync(T.AddSeconds(5), CancellationToken.None)).Should().Be(1, "one old event; the open run is kept");
        (await repo.CountEventsAsync(CancellationToken.None)).Should().Be(1);
    }
}
