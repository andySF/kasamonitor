using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Data.Repositories;
using KasaMonitor.Workers.Nilm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

/// <summary>
/// The duration band through the service (0.13.0), on a synthetic house: 300 W, an espresso machine's pulses of 1430 W for
/// 15 s and a kettle's 1430 W for 3 minutes. Same step, same ΔQ: only the duration tells them apart.
/// </summary>
public class NilmDurationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
    private const int Pulse = 15;
    private const int Kettle = 180;

    private sealed class Rig : IDisposable
    {
        public TestDbFactory Db { get; } = new();
        public TestScopeFactory Scopes { get; }
        public KasaSettingsService Settings { get; }
        public LiveStreamBroadcaster Live { get; } = new();
        public PlugStateBroadcaster States { get; } = new();
        public DateTimeOffset Now = T0;
        public Guid Main;
        public string ModelPath { get; } = Path.Combine(Path.GetTempPath(), $"nilm-{Guid.NewGuid():N}.zip");

        public Rig()
        {
            Scopes = new TestScopeFactory(Db.CreateContext);
            Settings = TestSettings.Create(Scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", MlEnabled: false));
        }

        public NilmService Service() => new(Scopes, Settings, Live, States, NullLogger<NilmService>.Instance, ModelPath, () => Now, TimeZoneInfo.Utc);

        public async Task<Guid> AddAsync(string mac, string alias, PlugRole role, string protocol)
        {
            using var ctx = Db.CreateContext();
            return await new PlugRepository(ctx).UpsertFromDiscoveryAsync(new DiscoveredPlug(IPAddress.Parse("192.168.254.141"),
                new PlugInfo(mac, alias, protocol == PlugProtocols.ShellyRpc ? "S3EM-002CXCEU" : "KP115(EU)", "1", "1", "x", true, -50, TimeSpan.Zero),
                protocol, null, 0, role), T0.AddDays(-1), CancellationToken.None);
        }

        public async Task<T> Repo<T>(Func<INilmRepository, Task<T>> f)
        {
            using var ctx = Db.CreateContext();
            return await f(new NilmRepository(ctx));
        }

        /// <summary>The seconds of the main meter from <paramref name="from"/>: 300 W, plus 1430 W during each (start, seconds).</summary>
        public async Task HouseAsync(DateTimeOffset from, int totalSeconds, params (int Start, int Seconds)[] on)
        {
            var samples = Enumerable.Range(0, totalSeconds)
                .Select(t => new LiveSample(Main, from.AddSeconds(t), on.Any(x => t >= x.Start && t < x.Start + x.Seconds) ? 1730 : 300)).ToList();
            using var ctx = Db.CreateContext();
            await new LiveRepository(ctx).SaveBatchAsync(samples, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        }

        public Task<ApplianceSignature> AddSignatureAsync(string name, double p = 1430, int? min = null, int? max = null, long? splitFrom = null,
            SignatureLabelSource source = SignatureLabelSource.User, Guid? plug = null) =>
            Repo(r => r.AddSignatureAsync(new ApplianceSignature(0, name, "bi-fire", "cooking", p, 0, 1, 5, T0, source, plug, false, min, max, splitFrom), CancellationToken.None));

        public Task<IReadOnlyList<MeterEvent>> EventsAsync() => Repo(r => r.ListEventsAsync(T0.AddDays(-2), Now, CancellationToken.None));
        public Task<IReadOnlyList<ApplianceRun>> RunsAsync() => Repo(r => r.ListRunsAsync(T0.AddDays(-2), Now, CancellationToken.None));
        public async Task<ApplianceSignature> SigAsync(long id) => (await Repo(r => r.GetSignatureAsync(id, CancellationToken.None)))!;

        public void Dispose()
        {
            Db.Dispose();
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
        }
    }

    private static async Task<Rig> RigAsync()
    {
        var rig = new Rig();
        rig.Main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        return rig;
    }

    private static double Seconds(ApplianceRun r) => (r.EndUtc!.Value - r.StartUtc).TotalSeconds;

    /// <summary>Three pulses and two kettle runs detected into one anonymous signature, which the user then named „Fierbător”.</summary>
    private static async Task<(NilmService Svc, ApplianceSignature S)> MixedAsync(Rig rig)
    {
        await rig.HouseAsync(T0, 1800, (120, Pulse), (240, Pulse), (360, Pulse), (600, Kettle), (1200, Kettle));
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        var s = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().ContainSingle().Subject;
        (await rig.RunsAsync()).Should().HaveCount(5).And.OnlyContain(r => r.SignatureId == s.Id && r.EndReason == RunEndReason.Paired);
        await svc.RenameAsync(s.Id, "Fierbător", CancellationToken.None);
        return (svc, await rig.SigAsync(s.Id));
    }

    [Fact]
    public async Task ARunThatClosesOutOfTheBand_MovesWithBothEvents_AndItsEnergy()
    {
        using var rig = await RigAsync();
        var s = await rig.AddSignatureAsync("Fierbător", min: 45);
        var t = await rig.AddSignatureAsync("Espressor", max: 40, splitFrom: s.Id);
        await rig.HouseAsync(T0, 1800, (120, Pulse), (600, Kettle));
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();

        await svc.RunOnceAsync(CancellationToken.None);

        var runs = await rig.RunsAsync();
        runs.Should().HaveCount(2).And.OnlyContain(r => r.EndReason == RunEndReason.Paired);
        var pulse = runs.Single(r => Seconds(r) == Pulse);
        var kettle = runs.Single(r => Seconds(r) == Kettle);
        pulse.SignatureId.Should().Be(t.Id);
        kettle.SignatureId.Should().Be(s.Id);
        pulse.EstimatedWh.Should().BeApproximately(1430 * Pulse / 3600.0, 0.5);

        var events = (await rig.EventsAsync()).ToDictionary(e => e.Id);
        foreach (var id in new[] { pulse.StartEventId, pulse.EndEventId!.Value })
            events[id].Should().Match<MeterEvent>(e => e.SignatureId == t.Id && e.Source == EventLabelSource.DurationRule && e.Label == "Espressor");
        foreach (var id in new[] { kettle.StartEventId, kettle.EndEventId!.Value })
            events[id].Should().Match<MeterEvent>(e => e.SignatureId == s.Id && e.Source == EventLabelSource.None);
        (await rig.SigAsync(t.Id)).Count.Should().Be(2, "the centres are refreshed");
        (await rig.SigAsync(s.Id)).Count.Should().Be(2);

        svc.Metrics.Appliances.Single(a => a.SignatureId == t.Id).EnergyWh.Should().BeGreaterThan(0, "the counter of the signature it ended in");
        svc.Metrics.Appliances.Single(a => a.SignatureId == s.Id).EnergyWh.Should().BeApproximately(1430 * Kettle / 3600.0, 0.5, "the pulse is not counted here");
    }

    [Fact]
    public async Task EventsNearerTheSplitOffSide_StillPair_BecauseBothWaitInTheParent()
    {
        using var rig = await RigAsync();
        // The child's centre is the exact one, so the nearest anchor would be the child for every event: the kettle would
        // open in it and leave it only by a rule that needs a paired run.
        var s = await rig.AddSignatureAsync("Fierbător", p: 1500, min: 45);
        var t = await rig.AddSignatureAsync("Espressor", p: 1430, max: 40, splitFrom: s.Id);
        await rig.HouseAsync(T0, 1800, (120, Pulse), (600, Kettle));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        var runs = await rig.RunsAsync();
        runs.Should().HaveCount(2).And.OnlyContain(r => r.EndReason == RunEndReason.Paired);
        runs.Single(r => Seconds(r) == Pulse).SignatureId.Should().Be(t.Id);
        runs.Single(r => Seconds(r) == Kettle).SignatureId.Should().Be(s.Id);
    }

    [Fact]
    public async Task TwoSignaturesThatCouldTakeTheRun_LeaveItWhereItIs()
    {
        using var rig = await RigAsync();
        var s = await rig.AddSignatureAsync("Fierbător", min: 45);
        await rig.AddSignatureAsync("Espressor", max: 40);
        await rig.AddSignatureAsync("Alt espressor", max: 30);
        await rig.HouseAsync(T0, 600, (120, Pulse));
        rig.Now = T0.AddMinutes(11);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        (await rig.RunsAsync()).Should().ContainSingle().Which.SignatureId.Should().Be(s.Id);
        (await rig.EventsAsync()).Should().OnlyContain(e => e.SignatureId == s.Id && e.Source == EventLabelSource.None);
    }

    [Fact]
    public async Task ARunNotClosedByItsOwnSwitchOff_IsNotMoved()
    {
        using var rig = await RigAsync();
        var s = await rig.AddSignatureAsync("Fierbător", min: 45);
        await rig.AddSignatureAsync("Espressor", max: 40);
        // 1430 W on, then the house falls by 900 W: another signature's step that brings it down only partly; then a
        // 530 W off brings the house below its level and closes the run by the heuristic, after 20 s.
        var samples = Enumerable.Range(0, 300).Select(t => new LiveSample(rig.Main, T0.AddSeconds(t),
            t < 60 ? 300 : t < 70 ? 1730 : t < 80 ? 830 : 300)).ToList();
        using (var ctx = rig.Db.CreateContext())
            await new LiveRepository(ctx).SaveBatchAsync(samples, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        rig.Now = T0.AddMinutes(6);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        (await rig.RunsAsync()).Should().Contain(r => r.SignatureId == s.Id && r.EndReason == RunEndReason.HouseBelowLevel && Seconds(r) == 20);
    }

    [Fact]
    public async Task ARunAcrossMidnight_MovesWithItsShareOfEachDay()
    {
        using var rig = await RigAsync();
        var s = await rig.AddSignatureAsync("Fierbător", min: 45);
        var t = await rig.AddSignatureAsync("Espressor", max: 40);
        var midnight = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        await rig.HouseAsync(midnight.AddMinutes(-2), 240, (110, 20));   // from 10 s before midnight to 10 s after it
        rig.Now = midnight.AddMinutes(5);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        var run = (await rig.RunsAsync()).Should().ContainSingle().Subject;
        run.SignatureId.Should().Be(t.Id);
        var before = ApplianceEnergy.InRange(run, midnight.AddDays(-1), midnight, rig.Now);
        var after = ApplianceEnergy.InRange(run, midnight, midnight.AddDays(1), rig.Now);
        before.Should().BeApproximately(after, 0.01).And.BeGreaterThan(0);
        (await rig.Repo(r => r.ListRunsBySignatureAsync(s.Id, midnight.AddDays(-1), CancellationToken.None))).Should().BeEmpty();
    }

    [Fact]
    public async Task SplitDryRun_WritesNothing_AndSaysWhatWouldMove()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var signatures = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        var events = await rig.EventsAsync();
        var runs = await rig.RunsAsync();

        var r = await svc.SplitByDurationAsync(s.Id, 40, "Espressor", "cooking", dryRun: true, CancellationToken.None);

        r.Should().NotBeNull();
        r!.DryRun.Should().BeTrue();
        r.Applied.Should().BeFalse();
        r.TargetId.Should().BeNull("a new signature would be created");
        r.TargetName.Should().Be("Espressor");
        r.Runs.Should().Be(3);
        r.Events.Should().Be(6);
        r.SkippedUserRuns.Should().Be(0);
        r.MovedDurations.Should().Be(new DurationSpread(3, Pulse, Pulse, Pulse));
        r.RemainingDurations.Should().Be(new DurationSpread(2, Kettle, Kettle, Kettle));
        r.EnergyWh.Should().BeApproximately(3 * 1430 * Pulse / 3600.0, 0.5);

        (await rig.Repo(x => x.ListSignaturesAsync(CancellationToken.None))).Should().BeEquivalentTo(signatures);
        (await rig.EventsAsync()).Should().BeEquivalentTo(events);
        (await rig.RunsAsync()).Should().BeEquivalentTo(runs);
    }

    [Fact]
    public async Task SplitForReal_MovesRunsAndEvents_SetsBothBands_RecalculatesCentres_ThenHoldsThroughAReClustering()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var dry = await svc.SplitByDurationAsync(s.Id, 40, "Espressor", "cooking", dryRun: true, CancellationToken.None);
        var todayBefore = (await rig.RunsAsync()).Sum(x => x.EstimatedWh ?? 0);

        var r = await svc.SplitByDurationAsync(s.Id, 40, "Espressor", "cooking", dryRun: false, CancellationToken.None);

        r!.Applied.Should().BeTrue();
        r.Runs.Should().Be(dry!.Runs);
        r.EnergyWh.Should().Be(dry.EnergyWh, "the dry run announces what the real one moves");
        var t = await rig.SigAsync(r.TargetId!.Value);
        t.Should().Match<ApplianceSignature>(x => x.Name == "Espressor" && x.Category == "cooking" && x.LabelSource == SignatureLabelSource.User
            && x.MaxRunSeconds == 40 && x.MinRunSeconds == null && x.SplitFromId == s.Id && x.Count == 6);
        t.MeanDeltaP.Should().BeApproximately(1430, 1);
        (await rig.SigAsync(s.Id)).Should().Match<ApplianceSignature>(x => x.MinRunSeconds == 40 && x.MaxRunSeconds == null && x.Count == 4);

        var runs = await rig.RunsAsync();
        runs.Where(x => x.SignatureId == t.Id).Select(Seconds).Should().Equal(Pulse, Pulse, Pulse);
        runs.Where(x => x.SignatureId == s.Id).Select(Seconds).Should().Equal(Kettle, Kettle);
        runs.Sum(x => x.EstimatedWh ?? 0).Should().BeApproximately(todayBefore, 1e-6, "the energy moves, it does not change");
        (await rig.EventsAsync()).Where(e => e.SignatureId == t.Id).Should().HaveCount(6)
            .And.OnlyContain(e => e.Source == EventLabelSource.DurationRule && e.Label == "Espressor");

        // The nightly job: the events stay, the two are not merged though their centres are the same, and the runs come out the same.
        var regroup = await svc.RegroupNowAsync(CancellationToken.None);
        regroup.Merged.Should().Be(0);
        var after = await rig.RunsAsync();
        after.Where(x => x.SignatureId == t.Id).Select(Seconds).Should().Equal(Pulse, Pulse, Pulse);
        after.Where(x => x.SignatureId == s.Id).Select(Seconds).Should().Equal(Kettle, Kettle);
        (await rig.EventsAsync()).Count(e => e.SignatureId == t.Id && e.Source == EventLabelSource.DurationRule).Should().Be(6);
        (await rig.SigAsync(t.Id)).MeanDeltaP.Should().BeApproximately(1430, 1, "the centre includes them");
    }

    [Fact]
    public async Task AfterASplit_NewPulsesAndNewKettleRuns_FindTheirSignature()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var t = (await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None))!.TargetId!.Value;

        // Half an hour later, in a new service as after a restart: a pulse, a kettle.
        await rig.HouseAsync(T0.AddMinutes(30), 1800, (120, Pulse), (600, Kettle));
        rig.Now = T0.AddMinutes(61);
        await rig.Service().RunOnceAsync(CancellationToken.None);

        var fresh = (await rig.RunsAsync()).Where(x => x.StartUtc >= T0.AddMinutes(30)).ToList();
        fresh.Should().HaveCount(2).And.OnlyContain(x => x.EndReason == RunEndReason.Paired);
        fresh.Single(x => Seconds(x) == Pulse).SignatureId.Should().Be(t);
        fresh.Single(x => Seconds(x) == Kettle).SignatureId.Should().Be(s.Id);
        (await rig.SigAsync(t)).Category.Should().Be((await rig.SigAsync(s.Id)).Category, "a new signature takes the category of the one split when none is given");
    }

    [Fact]
    public async Task Split_AnEventTheUserLabelled_HoldsItsRunWhereItIs()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var pulse = (await rig.RunsAsync()).First(x => Seconds(x) == Pulse);
        await svc.LabelEventAsync(pulse.StartEventId, "Fierbător", CancellationToken.None);

        var r = await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None);

        r!.Runs.Should().Be(2);
        r.Events.Should().Be(4);
        r.SkippedUserRuns.Should().Be(1);
        var events = (await rig.EventsAsync()).ToDictionary(e => e.Id);
        events[pulse.StartEventId].Should().Match<MeterEvent>(e => e.SignatureId == s.Id && e.Source == EventLabelSource.User);
        events[pulse.EndEventId!.Value].SignatureId.Should().Be(s.Id);
        (await rig.RunsAsync()).Single(x => x.Id == pulse.Id).SignatureId.Should().Be(s.Id);

        // The rule that runs at every rebuild respects it as well.
        await svc.RegroupNowAsync(CancellationToken.None);
        var held = (await rig.EventsAsync()).Single(e => e.Id == pulse.StartEventId);
        held.Should().Match<MeterEvent>(e => e.SignatureId == s.Id && e.Source == EventLabelSource.User);
    }

    [Fact]
    public async Task Split_JoinsTheExistingSignatureOfThatName_WhenItsPowerIsCompatible()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var pump = await rig.AddSignatureAsync("Espressor", p: 287);
        var existing = await rig.AddSignatureAsync("Espressor", p: 1400);

        var dry = await svc.SplitByDurationAsync(s.Id, 40, "espressor", null, dryRun: true, CancellationToken.None);
        dry!.TargetId.Should().Be(existing.Id);
        dry.TargetName.Should().Be("Espressor");

        await svc.SplitByDurationAsync(s.Id, 40, "espressor", null, dryRun: false, CancellationToken.None);

        (await rig.RunsAsync()).Count(x => x.SignatureId == existing.Id).Should().Be(3);
        (await rig.SigAsync(existing.Id)).MaxRunSeconds.Should().Be(40);
        (await rig.SigAsync(pump.Id)).MaxRunSeconds.Should().BeNull();
    }

    [Fact]
    public async Task Split_AnExistingSignatureWithABandOfItsOwn_KeepsIt_AndANewOneTakesTheRuns()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var other = await rig.AddSignatureAsync("Espressor", p: 1430, min: 50, max: 100);

        var dry = await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: true, CancellationToken.None);
        dry!.TargetId.Should().BeNull("a band of 50–100 s is another component");

        var r = await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None);

        r!.TargetId.Should().NotBe(other.Id);
        (await rig.SigAsync(other.Id)).Should().Match<ApplianceSignature>(x => x.MinRunSeconds == 50 && x.MaxRunSeconds == 100);
        (await rig.RunsAsync()).Should().NotContain(x => x.SignatureId == other.Id);
        (await rig.RunsAsync()).Count(x => x.SignatureId == r.TargetId).Should().Be(3);
    }

    [Fact]
    public async Task UnitingTheTwoSidesByHand_UndoesTheSplit_InEitherDirection()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var t = (await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None))!.TargetId!.Value;

        var survivor = await svc.MergeWithAsync(s.Id, t, CancellationToken.None);

        survivor.Should().NotBeNull();
        (await rig.SigAsync(survivor!.Value)).Should().Match<ApplianceSignature>(x => x.MinRunSeconds == null && x.MaxRunSeconds == null && x.SplitFromId == null);
        (await rig.RunsAsync()).Should().HaveCount(5).And.OnlyContain(x => x.SignatureId == survivor.Value && x.EndReason == RunEndReason.Paired);

        await svc.RegroupNowAsync(CancellationToken.None);
        (await rig.RunsAsync()).Should().HaveCount(5).And.OnlyContain(x => x.SignatureId == survivor.Value);

        using var other = await RigAsync();
        var (svc2, s2) = await MixedAsync(other);
        var t2 = (await svc2.SplitByDurationAsync(s2.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None))!.TargetId!.Value;
        (await svc2.MergeAsync(t2, s2.Id, CancellationToken.None)).Should().BeTrue();
        (await other.SigAsync(s2.Id)).Should().Match<ApplianceSignature>(x => x.MinRunSeconds == null && x.MaxRunSeconds == null && x.SplitFromId == null);
    }

    [Fact]
    public async Task Split_WithNothingToMove_ChangesNothing()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var signatures = await rig.Repo(x => x.ListSignaturesAsync(CancellationToken.None));

        var r = await svc.SplitByDurationAsync(s.Id, 5, "Espressor", null, dryRun: false, CancellationToken.None);

        r!.Applied.Should().BeFalse();
        r.Runs.Should().Be(0);
        (await rig.Repo(x => x.ListSignaturesAsync(CancellationToken.None))).Should().BeEquivalentTo(signatures);
        (await svc.SplitByDurationAsync(987654, 40, "Espressor", null, dryRun: true, CancellationToken.None)).Should().BeNull();
        (await svc.SplitByDurationAsync(987654, 40, "Espressor", null, dryRun: false, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Split_OfASignatureMeasuredByAPlug_KeepsTheBothOnThePlugsSideOfTheBalance()
    {
        using var rig = await RigAsync();
        var plug = await rig.AddAsync("aabbccddee01", "Bucatarie", PlugRole.Plug, PlugProtocols.Legacy);
        var (svc, s) = await MixedAsync(rig);
        // The plug labels the kettle (long enough for its medians), not the pulses.
        foreach (var run in (await rig.RunsAsync()).Where(x => Seconds(x) == Kettle))
            foreach (var id in new[] { run.StartEventId, run.EndEventId!.Value })
            {
                var e = await rig.Repo(r => r.GetEventAsync(id, CancellationToken.None));
                await rig.Repo(async r => { await r.UpdateEventAsync(e! with { Label = "Bucatarie", Source = EventLabelSource.AutoPlug, LabelPlugId = plug }, CancellationToken.None); return true; });
            }
        await rig.Repo(async r => { await r.UpdateSignatureAsync(s with { PlugId = plug }, CancellationToken.None); return true; });

        var t = (await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None))!.TargetId!.Value;

        (await rig.SigAsync(s.Id)).PlugId.Should().Be(plug);
        (await rig.SigAsync(t)).PlugId.Should().Be(plug, "the plug measures the pulses too, though they are too short for it to label them");
        (await svc.RegroupNowAsync(CancellationToken.None)).Merged.Should().Be(0);
        (await rig.SigAsync(t)).PlugId.Should().Be(plug);
        (await svc.GetDetectedRunsAsync(T0, rig.Now, CancellationToken.None)).Runs.Should().BeEmpty("neither is subtracted twice from the rest of the house");
    }

    [Fact]
    public async Task ABandSetAfterTheFact_MovesNothingAtOnce_TheNextRebuildAppliesTheRule()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        var t = await rig.AddSignatureAsync("Espressor", max: 40);
        (await svc.SetDurationBandAsync(s.Id, 45, null, CancellationToken.None)).Should().BeTrue();
        (await svc.SetDurationBandAsync(987654, 45, null, CancellationToken.None)).Should().BeFalse();

        (await rig.RunsAsync()).Should().OnlyContain(x => x.SignatureId == s.Id, "a band only sets the limit");

        await svc.RegroupNowAsync(CancellationToken.None);

        var runs = await rig.RunsAsync();
        runs.Where(x => x.SignatureId == t.Id).Select(Seconds).Should().Equal(Pulse, Pulse, Pulse);
        runs.Where(x => x.SignatureId == s.Id).Select(Seconds).Should().Equal(Kettle, Kettle);
        (await rig.EventsAsync()).Where(e => e.SignatureId == t.Id).Should().HaveCount(6).And.OnlyContain(e => e.Source == EventLabelSource.DurationRule);
        (await rig.SigAsync(t.Id)).Count.Should().Be(6);

        (await svc.SetDurationBandAsync(s.Id, null, null, CancellationToken.None)).Should().BeTrue();
        (await rig.SigAsync(s.Id)).Should().Match<ApplianceSignature>(x => x.MinRunSeconds == null && x.MaxRunSeconds == null);
    }

    [Fact]
    public async Task TheEventsMovedByDuration_DoNotTeachTheClassifier()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);
        await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None);

        (await rig.Repo(r => r.ListLabeledEventsAsync(Array.Empty<Guid>(), 100, CancellationToken.None))).Should().BeEmpty();
        (await rig.Repo(r => r.CountLabeledAsync(Array.Empty<Guid>(), CancellationToken.None))).Should().Be((0, 0));
    }

    [Fact]
    public async Task TheSheet_ShowsTheRepetition_OfTheLast30Days_WhateverTheWindow()
    {
        using var rig = await RigAsync();
        var (svc, s) = await MixedAsync(rig);

        var view = await svc.GetProfileAsync(s.Id, CancellationToken.None);

        var rep = view!.Profile.Repetition!;
        rep.Durations.Count.Should().Be(5);
        rep.Durations.P50.Should().Be(Pulse);
        rep.Intervals.Count.Should().Be(4);
        rep.Sessions.Should().Be(3, "three pulses 105 s apart, then two kettle runs far from them and from each other");
        rep.PulsesPerSessionP50.Should().Be(1);
    }

    [Fact]
    public async Task AMerge_MovesTheOriginToTheSurvivor_ADeletionEndsIt()
    {
        using var rig = await RigAsync();
        var svc = rig.Service();
        var s = await rig.AddSignatureAsync("Fierbător", min: 45);
        var other = await rig.AddSignatureAsync("Fierbător 2");
        var t = await rig.AddSignatureAsync("Espressor", max: 40, splitFrom: other.Id);

        await svc.MergeAsync(other.Id, s.Id, CancellationToken.None);
        (await rig.SigAsync(t.Id)).SplitFromId.Should().Be(s.Id);
        (await rig.SigAsync(s.Id)).MinRunSeconds.Should().Be(45);

        await rig.Repo(async r => { await r.DeleteSignatureAsync(s.Id, CancellationToken.None); return true; });
        (await rig.SigAsync(t.Id)).SplitFromId.Should().BeNull();
    }
}
