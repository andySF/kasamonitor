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
    public async Task DurationBand_RoundTrips_AndMoveRuns_TakesRunAndEventsToTheTarget_ButNeverAUsersLabel()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        var kettle = await repo.AddSignatureAsync(new ApplianceSignature(0, "Fierbător", "i", "cooking", 1430, 90, 1, 0, null, SignatureLabelSource.User, null, false,
            MinRunSeconds: 40), CancellationToken.None);
        var espresso = await repo.AddSignatureAsync(new ApplianceSignature(0, "Espressor", "i", "cooking", 1430, 90, 1, 0, null, SignatureLabelSource.User, null, false,
            MaxRunSeconds: 40, SplitFromId: kettle.Id), CancellationToken.None);

        var back = await new NilmRepository(db.CreateContext()).GetSignatureAsync(espresso.Id, CancellationToken.None);
        back.Should().BeEquivalentTo(espresso);
        back!.MaxRunSeconds.Should().Be(40);
        back.MinRunSeconds.Should().BeNull();
        back.SplitFromId.Should().Be(kettle.Id);
        await repo.UpdateSignatureAsync(back with { MinRunSeconds = 5, MaxRunSeconds = 35 }, CancellationToken.None);
        (await repo.ListSignaturesAsync(CancellationToken.None)).Single(s => s.Id == espresso.Id).Should().Match<ApplianceSignature>(s => s.MinRunSeconds == 5 && s.MaxRunSeconds == 35);

        var stored = await repo.AddEventsAsync(new[] { E(0, 1430, kettle.Id), E(15, -1430, kettle.Id), E(100, 1430, kettle.Id), E(115, -1430, kettle.Id) }, CancellationToken.None);
        await repo.UpdateEventAsync(stored[1] with { Label = "Bucatarie", Source = EventLabelSource.AutoPlug, LabelPlugId = Main }, CancellationToken.None);
        await repo.UpdateEventAsync(stored[2] with { Label = "Fierbător", Source = EventLabelSource.User }, CancellationToken.None);
        var runs = await repo.SaveRunsAsync(new[]
        {
            new ApplianceRun(0, kettle.Id, stored[0].TsUtc, stored[1].TsUtc, 1430, 6, stored[0].Id, stored[1].Id),
            new ApplianceRun(0, kettle.Id, stored[2].TsUtc, stored[3].TsUtc, 1430, 6, stored[2].Id, stored[3].Id),
        }, CancellationToken.None);

        await repo.MoveRunsAsync(Array.Empty<RunMove>(), CancellationToken.None);
        await repo.MoveRunsAsync(new[]
        {
            new RunMove(runs[0].Id, stored[0].Id, stored[1].Id, kettle.Id, espresso.Id, "Espressor"),
            new RunMove(0, stored[2].Id, stored[3].Id, kettle.Id, espresso.Id, "Espressor"),
        }, CancellationToken.None);

        var fresh = new NilmRepository(db.CreateContext());
        (await fresh.GetEventAsync(stored[0].Id, CancellationToken.None)).Should().Match<MeterEvent>(e => e.SignatureId == espresso.Id && e.Source == EventLabelSource.DurationRule && e.Label == "Espressor");
        (await fresh.GetEventAsync(stored[1].Id, CancellationToken.None)).Should().Match<MeterEvent>(e => e.SignatureId == espresso.Id && e.Source == EventLabelSource.DurationRule && e.LabelPlugId == Main,
            "the plug that saw it stays known");
        (await fresh.GetEventAsync(stored[2].Id, CancellationToken.None)).Should().Match<MeterEvent>(e => e.SignatureId == kettle.Id && e.Source == EventLabelSource.User && e.Label == "Fierbător");
        (await fresh.GetEventAsync(stored[3].Id, CancellationToken.None))!.SignatureId.Should().Be(espresso.Id, "a move without a stored run still moves its events");
        (await fresh.ListRunsBySignatureAsync(espresso.Id, T, CancellationToken.None)).Select(r => r.Id).Should().Equal(runs[0].Id);
        (await fresh.ListRunsBySignatureAsync(kettle.Id, T, CancellationToken.None)).Select(r => r.Id).Should().Equal(new[] { runs[1].Id }, "run id 0 touches no stored run");

        (await fresh.ListLabeledEventsAsync(Array.Empty<Guid>(), 100, CancellationToken.None)).Select(e => e.Id).Should().Equal(new[] { stored[2].Id }, "a rule's move teaches nothing");
        (await fresh.RecomputeSignaturesAsync(new[] { espresso.Id }, T, CancellationToken.None)).Single().Count.Should().Be(3, "the centre includes them");
    }

    [Fact]
    public async Task ApplyDurationSplit_StoresBothSignatures_CreatesTheNewOne_AndMovesRunsAndEvents()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        var kettle = await repo.AddSignatureAsync(new ApplianceSignature(0, "Fierbător", "i", "cooking", 1430, 90, 1, 0, null, SignatureLabelSource.User, null, false), CancellationToken.None);
        var stored = await repo.AddEventsAsync(new[] { E(0, 1430, kettle.Id), E(15, -1430, kettle.Id) }, CancellationToken.None);
        var runs = await repo.SaveRunsAsync(new[] { new ApplianceRun(0, kettle.Id, stored[0].TsUtc, stored[1].TsUtc, 1430, 6, stored[0].Id, stored[1].Id) }, CancellationToken.None);

        var t = await repo.ApplyDurationSplitAsync(kettle with { MinRunSeconds = 40 },
            new ApplianceSignature(0, "Espressor", "i", "cooking", 1430, 90, 1, 0, null, SignatureLabelSource.User, null, false, MaxRunSeconds: 40, SplitFromId: kettle.Id),
            runs, CancellationToken.None);

        t.Id.Should().BeGreaterThan(0);
        var fresh = new NilmRepository(db.CreateContext());
        (await fresh.GetSignatureAsync(kettle.Id, CancellationToken.None))!.MinRunSeconds.Should().Be(40);
        (await fresh.GetSignatureAsync(t.Id, CancellationToken.None)).Should().BeEquivalentTo(t);
        (await fresh.ListRunsBySignatureAsync(t.Id, T, CancellationToken.None)).Select(r => r.Id).Should().Equal(runs[0].Id);
        (await fresh.ListEventsBySignaturesAsync(new[] { t.Id }, T, CancellationToken.None)).Should().HaveCount(2)
            .And.OnlyContain(e => e.Source == EventLabelSource.DurationRule && e.Label == "Espressor");

        // An existing target is updated, not added.
        var again = await repo.ApplyDurationSplitAsync(kettle with { MinRunSeconds = 50 }, t with { MaxRunSeconds = 50 }, Array.Empty<ApplianceRun>(), CancellationToken.None);
        again.Id.Should().Be(t.Id);
        (await fresh.ListSignaturesAsync(CancellationToken.None)).Should().HaveCount(2);
        (await fresh.GetSignatureAsync(t.Id, CancellationToken.None))!.MaxRunSeconds.Should().Be(50);
    }

    [Fact]
    public async Task SplitOrigin_FollowsAMerge_AndEndsWithADeletion()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        ApplianceSignature New(string name, long? from = null) => new(0, name, "i", "other", 100, 0, 1, 0, null, SignatureLabelSource.User, null, false, SplitFromId: from);
        var a = await repo.AddSignatureAsync(New("a"), CancellationToken.None);
        var b = await repo.AddSignatureAsync(New("b"), CancellationToken.None);
        var child = await repo.AddSignatureAsync(New("c", a.Id), CancellationToken.None);

        await repo.MoveSignatureAsync(a.Id, b.Id, CancellationToken.None);
        (await repo.GetSignatureAsync(child.Id, CancellationToken.None))!.SplitFromId.Should().Be(b.Id);

        await repo.DeleteSignatureAsync(b.Id, CancellationToken.None);
        (await repo.GetSignatureAsync(child.Id, CancellationToken.None))!.SplitFromId.Should().BeNull();
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
        var on = await AddOne(repo, E(0, 120, sig.Id) with { RiseSeconds = 0, Label = "Frigider", Source = EventLabelSource.AutoPlug, LabelPlugId = plug });
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
    public async Task Recompute_MeanTransientOnlyFromSwitchOnsWithAKnownTransient()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, "", "", "other", 0, 0, 0, 0, null, SignatureLabelSource.Cluster, null, false),
            CancellationToken.None);
        await repo.AddEventsAsync(new[]
        {
            E(0, 2000, sig.Id) with { TransientRatio = 1.0, RiseSeconds = 0 },
            E(60, 1990, sig.Id) with { TransientRatio = 0.02, RiseSeconds = 1 },    // rise ≥ 1 s, ratio ≤ 1.5: unknown
            E(120, 1980, sig.Id) with { TransientRatio = 0.34, RiseSeconds = 2 },   // spread over samples: unknown
            E(180, -1990, sig.Id) with { TransientRatio = 1.0, RiseSeconds = 0 },   // switch-off
        }, CancellationToken.None);
        var s = (await repo.RecomputeSignaturesAsync(new[] { sig.Id }, T.AddDays(-1), CancellationToken.None)).Single();
        s.Count.Should().Be(4);
        s.MeanTransient.Should().Be(1.0);
        await repo.AddEventsAsync(new[] { E(240, 1995, sig.Id) with { TransientRatio = 4.0, RiseSeconds = 2 } }, CancellationToken.None);   // inrush: known
        (await repo.RecomputeSignaturesAsync(new[] { sig.Id }, T.AddDays(-1), CancellationToken.None)).Single().MeanTransient.Should().Be(2.5);
        (await repo.ListEventsAsync(T, T.AddHours(1), CancellationToken.None)).Select(e => e.TransientRatio).Should().Equal(1.0, 0.02, 0.34, 1.0, 4.0);
    }

    [Fact]
    public async Task MoveSignature_EventsAndRunsGoToTheSurvivor_TheAbsorbedOneIsDeleted()
    {
        using var db = new TestDbFactory();
        var repo = new NilmRepository(db.CreateContext());
        ApplianceSignature New() => new(0, "", "", "other", 0, 0, 0, 0, null, SignatureLabelSource.Cluster, null, false);
        var a = await repo.AddSignatureAsync(New(), CancellationToken.None);
        var b = await repo.AddSignatureAsync(New(), CancellationToken.None);
        var e1 = await AddOne(repo, E(0, 120, a.Id));
        var e2 = await AddOne(repo, E(60, -120, a.Id));
        var e3 = await AddOne(repo, E(120, 121, b.Id));
        await repo.SaveRunsAsync(new[] { new ApplianceRun(0, a.Id, T, T.AddSeconds(60), 120, 2, e1.Id, e2.Id) }, CancellationToken.None);

        (await repo.MoveSignatureAsync(a.Id, b.Id, CancellationToken.None)).Should().Be(2);

        (await repo.GetSignatureAsync(a.Id, CancellationToken.None)).Should().BeNull();
        (await repo.ListEventsBySignaturesAsync(new[] { b.Id }, T.AddDays(-1), CancellationToken.None)).Select(e => e.Id).Should().Equal(e1.Id, e2.Id, e3.Id);
        (await repo.ListRunsAsync(T.AddDays(-1), T.AddDays(1), CancellationToken.None)).Should().ContainSingle().Which.SignatureId.Should().Be(b.Id);
        (await repo.MoveSignatureAsync(b.Id, b.Id, CancellationToken.None)).Should().Be(0);
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
