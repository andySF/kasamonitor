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

public class NilmClassifierTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Labelled switch-ons and switch-offs of three appliances, with noise, at random times.</summary>
    internal static List<MeterEvent> Labeled(int perClass, int seed = 5)
    {
        var rnd = new Random(seed);
        double N(double x, double rel) => x * (1 + (rnd.NextDouble() - 0.5) * 2 * rel);
        var list = new List<MeterEvent>();
        var id = 1L;
        void Add(string label, double p, double q, double t, double rise)
        {
            for (var i = 0; i < perClass; i++)
            {
                var on = i % 2 == 0;
                var ts = T.AddMinutes(rnd.Next(0, 30 * 24 * 60));
                list.Add(new MeterEvent(id++, Guid.Empty, ts, on ? N(p, 0.05) : -N(p, 0.05), on ? N(q, 0.1) : -N(q, 0.1), 0,
                    on ? N(t, 0.15) : 1, on ? rise : 0, 300, null, label, EventLabelSource.AutoPlug));
            }
        }
        Add("Frigider", 120, 60, 5, 2);
        Add("Boiler", 2000, 5, 1, 0);
        Add("TV", 80, 20, 1.2, 0);
        return list;
    }

    [Fact]
    public void Train_SyntheticLabeledSet_CrossValidatedAccuracyAbove90Percent_PredictsAndSurvivesSaveLoad()
    {
        var events = Labeled(40);
        var c = new NilmClassifier(TimeZoneInfo.Utc);
        var info = c.Train(events, T);

        info.Should().NotBeNull();
        info!.Examples.Should().Be(120);
        info.Classes.Should().Equal("Boiler", "Frigider", "TV");
        info.Folds.Should().Be(5);
        info.Accuracy.Should().BeGreaterThan(0.9);
        info.Confusion.Sum(r => r.Predicted.Sum()).Should().Be(120, "every event is predicted once, by a model that did not see it");

        var fridgeOn = new MeterEvent(0, Guid.Empty, T, 118, 58, 0, 5.2, 2, 300);
        var p = c.Predict(fridgeOn);
        p.Should().NotBeNull();
        p!.Value.Label.Should().Be("Frigider");
        p.Value.Probability.Should().BeGreaterThanOrEqualTo(NilmClassifier.MinProbability);

        var path = Path.Combine(Path.GetTempPath(), $"nilm-{Guid.NewGuid():N}.zip");
        try
        {
            c.Save(path);
            var loaded = new NilmClassifier(TimeZoneInfo.Utc);
            loaded.Load(path).Should().BeTrue();
            loaded.Predict(fridgeOn)!.Value.Label.Should().Be("Frigider");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Prediction_IsUsedOnlyNearTheCentreOfThePredictedClass()
    {
        var c = new NilmClassifier(TimeZoneInfo.Utc);
        var info = c.Train(Labeled(40), T)!;
        var fridgeOn = new MeterEvent(0, Guid.Empty, T, 118, 58, 0, 5.2, 2, 300);
        info.NearClass(fridgeOn, "Frigider").Should().BeTrue();
        var lamp = new MeterEvent(0, Guid.Empty, T, 35, 0, 0, 1, 0, 300);   // never seen by a plug
        var p = c.Predict(lamp);
        p.Should().NotBeNull("a closed-set classifier always answers");
        info.NearClass(lamp, p!.Value.Label).Should().BeFalse("35 W is far from every known class");
    }

    [Fact]
    public void Train_NeedsTwentyLabelsInTwoClasses()
    {
        NilmClassifier.CanTrain(Labeled(40).Where(e => e.Label == "TV").ToList()).Should().BeFalse("one class");
        NilmClassifier.CanTrain(Labeled(6)).Should().BeFalse("18 events");
        new NilmClassifier().Train(Labeled(6), T).Should().BeNull();
        new NilmClassifier().Predict(new MeterEvent(0, Guid.Empty, T, 1, 0, 0, 1, 0, 0)).Should().BeNull("no model");
        new NilmClassifier().Load(Path.Combine(Path.GetTempPath(), "missing-nilm-model.zip")).Should().BeFalse();
    }
}

public class NilmServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private sealed class Rig : IDisposable
    {
        public TestDbFactory Db { get; } = new();
        public TestScopeFactory Scopes { get; }
        public KasaSettingsService Settings { get; }
        public LiveStreamBroadcaster Live { get; } = new();
        public PlugStateBroadcaster States { get; } = new();
        public DateTimeOffset Now = T0;
        public string ModelPath { get; } = Path.Combine(Path.GetTempPath(), $"nilm-{Guid.NewGuid():N}.zip");

        public Rig(bool ml = false)
        {
            Scopes = new TestScopeFactory(Db.CreateContext);
            Settings = TestSettings.Create(Scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", MlEnabled: ml));
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

        public void Dispose()
        {
            Db.Dispose();
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
        }
    }

    /// <summary>
    /// House 300 W; fridge +120 W (inrush) at 5 min, off at 20 min, and (two cycles) on again at 25 min, off at
    /// 28:20; the 1 s samples of the main meter as stored.
    /// </summary>
    private static List<LiveSample> House(Guid main, int minutes = 30, int cycles = 2)
    {
        var list = new List<LiveSample>();
        for (var t = 0; t < minutes * 60; t++)
        {
            var two = cycles > 1;
            double w;
            if (t == 300 || (two && t == 1500)) w = 900;                                   // inrush
            else if (t == 301 || (two && t == 1501)) w = 560;
            else if ((t >= 302 && t < 1200) || (two && t >= 1502 && t < 1700)) w = 420;   // running
            else w = 300;
            list.Add(new LiveSample(main, T0.AddSeconds(t), w + (t % 3 - 1)));
        }
        return list;
    }

    /// <summary>The fridge plug's 10-second readings: 2 W, 122 W while running.</summary>
    private static List<Sample> FridgePlug(Guid plug, int minutes = 30, int cycles = 2) =>
        Enumerable.Range(0, minutes * 6).Select(k =>
        {
            var t = k * 10 + 4;
            var w = (t >= 300 && t < 1200) || (cycles > 1 && t >= 1500 && t < 1700) ? 122_000L : 2_000L;
            return new Sample(0, plug, T0.AddSeconds(t), true, w, 230_000, null, null, null, true, null);
        }).ToList();

    private static async Task SeedAsync(Rig rig, Guid main, Guid fridge, int cycles = 2)
    {
        using var ctx = rig.Db.CreateContext();
        await new LiveRepository(ctx).SaveBatchAsync(House(main, cycles: cycles), Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        await new SampleRepository(ctx).InsertManyAsync(FridgePlug(fridge, cycles: cycles), CancellationToken.None);
    }

    [Fact]
    public async Task FewerThanThreeEventsFromAPlug_SignatureStaysUnknown_MarkedPossiblePlug()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        await SeedAsync(rig, main, fridge, cycles: 1);
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);

        (await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None))).Should().HaveCount(2)
            .And.OnlyContain(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId == fridge);
        var sig = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().ContainSingle().Subject;
        sig.PlugId.Should().BeNull("2 events from the plug: below the minimum of 3");
        sig.Name.Should().StartWith("Necunoscut #");
        var page = await svc.GetOverviewAsync(0, 0, CancellationToken.None);
        page.Detected.Should().ContainSingle().Which.PossiblePlug.Should().Be("Frigider");
        page.PlugMeasured.Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_StoredSamples_EventsLabelledFromThePlug_SignatureTakesItsName_RunClosedWithEnergy_NoDuplicatesAfterRestart()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        await SeedAsync(rig, main, fridge);
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Should().HaveCount(4);
        events[0].TsUtc.Should().Be(T0.AddSeconds(300));
        events[0].DeltaP.Should().BeApproximately(120, 2);
        events[0].TransientRatio.Should().BeGreaterThan(3);
        events[1].TsUtc.Should().Be(T0.AddSeconds(1200));
        events[1].DeltaP.Should().BeApproximately(-120, 2);
        events[2].TsUtc.Should().Be(T0.AddSeconds(1500));
        events[3].TsUtc.Should().Be(T0.AddSeconds(1700));
        events.Should().OnlyContain(e => e.Source == EventLabelSource.AutoPlug && e.Label == "Frigider" && e.LabelPlugId == fridge);

        var sigs = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        sigs.Should().ContainSingle();
        sigs[0].Name.Should().Be("Frigider");
        sigs[0].LabelSource.Should().Be(SignatureLabelSource.Plug);
        sigs[0].PlugId.Should().Be(fridge);
        sigs[0].Count.Should().Be(4);
        events.Should().OnlyContain(e => e.SignatureId == sigs[0].Id);

        var runs = await rig.Repo(r => r.ListRunsAsync(T0, rig.Now, CancellationToken.None));
        runs.Should().HaveCount(2);
        runs[0].EndUtc.Should().Be(T0.AddSeconds(1200));
        runs[1].EndUtc.Should().Be(T0.AddSeconds(1700));
        runs[0].EstimatedWh!.Value.Should().BeApproximately(120 * 0.25, 1.5);   // 15 minutes at 120 W

        string? marker;
        using (var ctx = rig.Db.CreateContext())
            marker = await new SettingsRepository(ctx).GetAsync(SettingKeys.NilmLastProcessedTs, CancellationToken.None);
        long.Parse(marker!).Should().Be(T0.AddSeconds(30 * 60 - 1).ToUnixTimeSeconds());

        // R6: renaming keeps the plug; a plug-measured appliance is never a detected one (cards, energy, metrics).
        var svc0 = rig.Service();
        await svc0.RunOnceAsync(CancellationToken.None);
        await svc0.RenameAsync(sigs[0].Id, "Frigider bucătărie", CancellationToken.None);
        var renamed = await rig.Repo(r => r.GetSignatureAsync(sigs[0].Id, CancellationToken.None));
        renamed!.Name.Should().Be("Frigider bucătărie");
        renamed.PlugId.Should().Be(fridge);
        renamed.ConfirmedByPlug.Should().BeTrue();
        svc0.Summary.Today.Should().BeEmpty();
        svc0.Summary.Active.Should().BeEmpty();
        svc0.Metrics.Appliances.Should().BeEmpty();
        var page = await svc0.GetOverviewAsync(0, 0, CancellationToken.None);
        page.PlugMeasured.Should().ContainSingle().Which.Signature.Name.Should().Be("Frigider bucătărie");
        page.Detected.Should().BeEmpty();

        // Restart: the backfill starts after the marker, nothing is stored twice.
        rig.Now = T0.AddMinutes(40);
        await rig.Service().RunOnceAsync(CancellationToken.None);
        (await rig.Repo(r => r.CountEventsAsync(CancellationToken.None))).Should().Be(4);
    }

    [Fact]
    public async Task Live_SamplesOfTheMainMeter_EventWaitsForThePlugReadings_ThenIsStored_SummaryShowsTheRunningAppliance()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);   // resolves the main meter, nothing stored to backfill
        svc.MainMeterId.Should().Be(main);

        for (var t = 0; t < 40; t++)
            rig.Live.Publish(new LiveSample(main, T0.AddSeconds(t), t < 20 ? 300 : 2300));
        rig.Live.Publish(new LiveSample(Guid.NewGuid(), T0.AddSeconds(5), 9999));   // another channel: ignored
        rig.Now = T0.AddSeconds(41);
        await svc.RunOnceAsync(CancellationToken.None);
        svc.PendingEvents.Should().Be(1);
        (await rig.Repo(r => r.CountEventsAsync(CancellationToken.None))).Should().Be(0, "the plugs' readings around it may not be stored yet");

        rig.Now = T0.AddSeconds(66);
        await svc.RunOnceAsync(CancellationToken.None);
        svc.PendingEvents.Should().Be(0);
        var e = (await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None))).Should().ContainSingle().Subject;
        e.DeltaP.Should().BeApproximately(2000, 0.1);
        e.Source.Should().Be(EventLabelSource.None);
        e.SignatureId.Should().NotBeNull();

        rig.Now = T0.AddMinutes(2);
        await svc.RunOnceAsync(CancellationToken.None);
        svc.Summary.Active.Should().ContainSingle().Which.PowerW.Should().BeApproximately(2000, 0.1);
        svc.Summary.Active[0].Name.Should().StartWith("Necunoscut #");
        svc.Metrics.Events.Should().Be(1);

        // A second switch-on of the same appliance before any switch-off: two open runs of one signature
        // (found by the real run: the overview failed with a duplicate key).
        for (var t = 40; t < 80; t++)
            rig.Live.Publish(new LiveSample(main, T0.AddSeconds(t + 60), t < 60 ? 2300 : 4300));
        rig.Now = T0.AddSeconds(200);
        await svc.RunOnceAsync(CancellationToken.None);
        // Two events, no name, no plug: a rare signature, listed only behind the switch.
        (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).RareCount.Should().Be(1);
        var overview = await svc.GetOverviewAsync(0, 0, CancellationToken.None, showRare: true);
        overview.Detected.Should().ContainSingle().Which.ActiveW.Should().BeApproximately(4000, 0.1);
    }

    [Fact]
    public async Task NoisyPlug_MoreThanTwentyMatchesInAnHour_IsExcludedFromLearning()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var ups = await rig.AddAsync("aabbccddee02", "Ups Birou", PlugRole.Plug, PlugProtocols.Legacy);
        bool On(int t) => t / 60 % 2 == 1;   // +40 W every other minute, for 50 minutes
        using (var ctx = rig.Db.CreateContext())
        {
            var house = Enumerable.Range(0, 3000).Select(t => new LiveSample(main, T0.AddSeconds(t), 300 + (On(t) ? 40 : 0) + (t % 3 - 1))).ToList();
            await new LiveRepository(ctx).SaveBatchAsync(house, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
            await new SampleRepository(ctx).InsertManyAsync(Enumerable.Range(0, 300).Select(k => k * 10 + 4)
                .Select(t => new Sample(0, ups, T0.AddSeconds(t), true, On(t) ? 42_000 : 2_000, 230_000, null, null, null, true, null)).ToList(),
                CancellationToken.None);
        }
        rig.Now = T0.AddMinutes(51);
        await rig.Service().RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Should().HaveCount(49);
        events.Count(e => e.Source == EventLabelSource.AutoPlug).Should().Be(PlugNilmStates.MaxMatchesPerHour);
        using (var ctx = rig.Db.CreateContext())
            (await new PlugRepository(ctx).ListAllAsync(CancellationToken.None)).Single(p => p.Id == ups).NilmState.Should().Be(PlugNilmState.IgnoredAuto);
        var sigs = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        sigs.Should().OnlyContain(s => s.PlugId == null, "the labels of an excluded plug do not make a signature plug-measured");
        (await rig.Repo(r => r.CountLabeledAsync(new[] { ups }, CancellationToken.None))).Events.Should().Be(0, "and do not teach the classifier");

        // The user undoes the exclusion: the plug teaches again and is never excluded automatically.
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        await svc.SetPlugStateAsync(ups, PlugNilmState.AlwaysUse, CancellationToken.None);
        (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).Plugs.Single(p => p.PlugId == ups).State.Should().Be(PlugNilmState.AlwaysUse);
    }

    [Fact]
    public async Task RunWithoutSwitchOff_ClosedAfterSixHoursAsUncertain_NoEnergyAnywhere()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        for (var t = 0; t < 40; t++)
            rig.Live.Publish(new LiveSample(main, T0.AddSeconds(t), t < 20 ? 300 : 380));
        rig.Now = T0.AddMinutes(2);
        await svc.RunOnceAsync(CancellationToken.None);
        svc.Summary.Active.Should().ContainSingle();

        rig.Now = T0.AddHours(7);
        await svc.RunOnceAsync(CancellationToken.None);
        var run = (await rig.Repo(r => r.ListRunsAsync(T0, rig.Now, CancellationToken.None))).Should().ContainSingle().Subject;
        run.EndReason.Should().Be(RunEndReason.Timeout);
        run.EstimatedWh.Should().Be(0);
        svc.Summary.Active.Should().BeEmpty();
        svc.Summary.Today.Should().BeEmpty("an uncertain run adds no energy to „Azi” (nor to „Neidentificat”)");
        var sigId = run.SignatureId;
        await svc.RenameAsync(sigId, "Ceva", CancellationToken.None);   // named: exported
        svc.Metrics.Appliances.Should().ContainSingle().Which.EnergyWh.Should().Be(0);
        (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).Detected.Single().UncertainRuns.Should().Be(1);
    }

    [Fact]
    public async Task ExcludingAPlug_KeepsTheSignaturesAlreadyLinkedToIt()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        await SeedAsync(rig, main, fridge);
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        var sig = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Single();
        sig.PlugId.Should().Be(fridge);

        await svc.SetPlugStateAsync(fridge, PlugNilmState.IgnoredManual, CancellationToken.None);
        await svc.NightlyAsync(rig.Now, CancellationToken.None);   // recomputes every signature
        (await rig.Repo(r => r.GetSignatureAsync(sig.Id, CancellationToken.None)))!.PlugId.Should().Be(fridge,
            "excluding stops new labels and training, the link stays");
        svc.Summary.Today.Should().BeEmpty("still measured by the plug: not a detected appliance");
    }

    /// <summary>Stores switch-on/off events of three appliances (no signature) over the last day.</summary>
    private static async Task SeedEventsAsync(Rig rig, Guid main, int perClass = 24)
    {
        var events = NilmClassifierTests.Labeled(perClass);
        var t = T0.AddHours(-20);
        using var ctx = rig.Db.CreateContext();
        var repo = new NilmRepository(ctx);
        foreach (var e in events.OrderBy(e => e.Label).ThenBy(e => e.Id))
        {
            t = t.AddMinutes(5);
            // Half of them labelled by the plugs; the rest not labelled.
            var labelled = e.Id % 2 == 0;
            await repo.AddEventsAsync(new[] { e with
            {
                Id = 0, PlugId = main, TsUtc = t, Label = labelled ? e.Label : null,
                Source = labelled ? EventLabelSource.AutoPlug : EventLabelSource.None,
            } }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Nightly_Reclusters_ThreeAppliancesThreeSignatures_RunsRebuilt_ClassifierTrained()
    {
        using var rig = new Rig(ml: true);
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        await SeedEventsAsync(rig, main);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);   // load state

        await svc.NightlyAsync(rig.Now, CancellationToken.None);

        var sigs = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        sigs.Should().HaveCount(3);
        sigs.Select(s => s.Count).Should().OnlyContain(c => c == 24);
        sigs.Select(s => s.MeanDeltaP).Should().BeEquivalentTo(new[] { 80.0, 120.0, 2000.0 }, o => o.Using<double>(x => x.Subject.Should().BeApproximately(x.Expectation, x.Expectation * 0.05)).WhenTypeIs<double>());
        var runs = await rig.Repo(r => r.ListRunsAsync(T0.AddDays(-2), rig.Now, CancellationToken.None));
        runs.Should().HaveCount(36, "every switch-on is followed by its switch-off");
        runs.Should().OnlyContain(r => r.EndUtc != null && r.EstimatedWh > 0);

        svc.ModelInfo.Should().NotBeNull();
        svc.ModelInfo!.Examples.Should().Be(36);
        svc.ModelInfo.Accuracy.Should().BeGreaterThan(0.9);
        File.Exists(rig.ModelPath).Should().BeTrue();
        using (var ctx = rig.Db.CreateContext())
            (await new SettingsRepository(ctx).GetAsync(SettingKeys.NilmModelInfo, CancellationToken.None)).Should().Contain("\"Accuracy\"");

        // A restarted service loads the model and the training info.
        var again = rig.Service();
        await again.RunOnceAsync(CancellationToken.None);
        again.Classifier.HasModel.Should().BeTrue();
        again.ModelInfo!.Examples.Should().Be(36);
    }

    [Fact]
    public async Task UserActions_LabelEvent_Rename_Category_Hide_Merge_Split()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        await SeedEventsAsync(rig, main);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        await svc.NightlyAsync(rig.Now, CancellationToken.None);
        var sigs = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        var tv = sigs.Single(s => Math.Abs(s.MeanDeltaP - 80) < 10);
        var fridge = sigs.Single(s => Math.Abs(s.MeanDeltaP - 120) < 10);

        // "a fost televizorul": the event's signature takes the name, kept by the next re-clustering.
        var tvEvent = (await rig.Repo(r => r.ListEventsBySignaturesAsync(new[] { tv.Id }, DateTimeOffset.UnixEpoch, CancellationToken.None)))[0];
        (await svc.LabelEventAsync(tvEvent.Id, " Televizor ", CancellationToken.None)).Should().BeTrue();
        var renamed = await rig.Repo(r => r.GetSignatureAsync(tv.Id, CancellationToken.None));
        renamed!.Name.Should().Be("Televizor");
        renamed.LabelSource.Should().Be(SignatureLabelSource.User);
        (await rig.Repo(r => r.GetEventAsync(tvEvent.Id, CancellationToken.None)))!.Source.Should().Be(EventLabelSource.User);
        await svc.NightlyAsync(rig.Now, CancellationToken.None);
        (await rig.Repo(r => r.GetSignatureAsync(tv.Id, CancellationToken.None)))!.Name.Should().Be("Televizor");

        await svc.SetCategoryAsync(fridge.Id, "fridge", CancellationToken.None);
        await svc.SetHiddenAsync(fridge.Id, true, CancellationToken.None);
        await svc.RenameAsync(fridge.Id, "Frigider bucătărie", CancellationToken.None);
        var f = await rig.Repo(r => r.GetSignatureAsync(fridge.Id, CancellationToken.None));
        f!.Category.Should().Be("fridge");
        f.Icon.Should().Be("bi-snow");
        f.Hidden.Should().BeTrue();
        f.Name.Should().Be("Frigider bucătărie");

        // Merge the TV into the fridge, then split them apart again.
        (await svc.MergeAsync(tv.Id, fridge.Id, CancellationToken.None)).Should().BeTrue();
        (await rig.Repo(r => r.GetSignatureAsync(tv.Id, CancellationToken.None))).Should().BeNull();
        (await rig.Repo(r => r.GetSignatureAsync(fridge.Id, CancellationToken.None)))!.Count.Should().Be(48);
        (await svc.SplitAsync(fridge.Id, CancellationToken.None)).Should().Be(2);
        var after = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        after.Should().HaveCount(3);
        after.Single(s => s.Id == fridge.Id).Count.Should().Be(24);
        var runs = await rig.Repo(r => r.ListRunsAsync(T0.AddDays(-2), rig.Now, CancellationToken.None));
        runs.Should().HaveCount(36);

        var overview = await svc.GetOverviewAsync(0, 1, CancellationToken.None);
        overview.Detected.Should().HaveCount(3);
        overview.EventsInWindow.Should().Be(72);
        overview.EventsPageCount.Should().Be(2);
        overview.Events.Should().HaveCount(72 - NilmService.PageSize, "second page");
        overview.LabeledEvents.Should().Be(37, "36 from the plugs + 1 by the user");
    }

    private static ApplianceSignature Sig(string name, int count, DateTimeOffset lastSeen, SignatureLabelSource source = SignatureLabelSource.Cluster,
        Guid? plug = null) => new(0, name, "bi-question-circle", ApplianceCategories.Other, 30, 0, 1, count, lastSeen, source, plug, false);

    /// <summary>Signatures as left by a few hours of clustering: some frequent, many with one or two small events.</summary>
    private static async Task<Dictionary<string, long>> SeedSignaturesAsync(Rig rig, Guid main, Guid fridge)
    {
        var ids = new Dictionary<string, long>();
        async Task<long> Add(string key, ApplianceSignature s)
        {
            var stored = await rig.Repo(r => r.AddSignatureAsync(s, CancellationToken.None));
            ids[key] = stored.Id;
            return stored.Id;
        }
        var recent = T0.AddHours(-1);
        var big = await Add("big", Sig("", 10, recent));
        var small = await Add("small", Sig("", 3, recent));
        await Add("oldFrequent", Sig("", 5, T0.AddDays(-10)));
        await Add("named", Sig("Fierbător", 1, T0.AddDays(-10), SignatureLabelSource.User));
        var possible = await Add("possible", Sig("", 2, T0.AddDays(-10)));
        await Add("rareNew", Sig("", 1, recent));
        var rareOld = await Add("rareOld", Sig("", 2, T0.AddDays(-10)));
        await Add("measured", Sig("Frigider", 1, T0.AddDays(-10), SignatureLabelSource.Plug, fridge));

        MeterEvent E(int minutesAgo, long sig, Guid? labelPlug = null) => new(0, main, T0.AddMinutes(-minutesAgo), 30, 0, 0, 1, 1, 300, sig,
            labelPlug is null ? null : "Frigider", labelPlug is null ? EventLabelSource.None : EventLabelSource.AutoPlug, labelPlug);
        await rig.Repo(r => r.AddEventsAsync(new[]
        {
            E(10 * 1440, rareOld), E(10 * 1440 - 30, rareOld),
            E(10 * 1440 - 60, possible, fridge), E(10 * 1440 - 90, possible),
        }, CancellationToken.None));
        await rig.Repo(r => r.SaveRunsAsync(new[]
        {
            new ApplianceRun(0, big, T0.AddDays(-2), T0.AddDays(-2).AddHours(1), 500, 500, 1, 2),
            new ApplianceRun(0, small, T0.AddDays(-1), T0.AddDays(-1).AddHours(1), 100, 100, 3, 4),
            new ApplianceRun(0, ids["oldFrequent"], T0.AddDays(-10), T0.AddDays(-10).AddHours(1), 900, 900, 5, 6),
        }, CancellationToken.None));
        return ids;
    }

    [Fact]
    public async Task Overview_ByDefault_ListsFrequentNamedOrPossibleSignatures_LargestSevenDayEnergyFirst()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        var ids = await SeedSignaturesAsync(rig, main, fridge);
        var svc = rig.Service();

        var page = await svc.GetOverviewAsync(0, 0, CancellationToken.None);

        page.Detected.Select(r => r.Signature.Id).Should().Equal(ids["big"], ids["small"], ids["oldFrequent"], ids["possible"], ids["named"]);
        page.Detected[0].Last7DaysWh.Should().BeApproximately(500, 1e-6);
        page.Detected[2].Last7DaysWh.Should().Be(0, "its only run is 10 days old");
        page.Detected[2].Last30DaysWh.Should().BeApproximately(900, 1e-6);
        page.Detected.Single(r => r.Signature.Id == ids["possible"]).PossiblePlug.Should().Be("Frigider");
        page.DetectedTotal.Should().Be(7);
        page.RareCount.Should().Be(2);
        // Only the old rare one: not the new rare one, the one with the plug badge, the named one or the plug-measured one.
        page.CleanableCount.Should().Be(1);
        page.PlugMeasured.Should().ContainSingle().Which.Signature.Id.Should().Be(ids["measured"]);

        var all = await svc.GetOverviewAsync(0, 0, CancellationToken.None, showRare: true);
        all.Detected.Should().HaveCount(7);
        all.ShowRare.Should().BeTrue();
    }

    [Fact]
    public async Task CleanRare_DeletesOnlyOldRareSignatures_TheirEventsStayUnlinked()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        var ids = await SeedSignaturesAsync(rig, main, fridge);
        var svc = rig.Service();

        (await svc.CleanRareSignaturesAsync(CancellationToken.None)).Should().Be(1);

        var left = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        left.Select(x => x.Id).Should().BeEquivalentTo(ids.Where(kv => kv.Key != "rareOld").Select(kv => kv.Value),
            "the one with the „posibil Frigider” badge (2 events, 10 days old) and the named one stay");
        var events = await rig.Repo(r => r.ListEventsAsync(T0.AddDays(-11), T0, CancellationToken.None));
        events.Should().HaveCount(4, "the events are kept");
        events.Where(e => e.SignatureId is null).Should().HaveCount(2, "the deleted signature's events, unlinked");
        events.Where(e => e.SignatureId == ids["possible"]).Should().HaveCount(2);
        (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).CleanableCount.Should().Be(0);
        (await svc.CleanRareSignaturesAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Disabled_NothingIsProcessed()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("d885ac0b743c", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        await SeedAsync(rig, main, fridge);
        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false, NilmEnabled: false), CancellationToken.None);
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        (await rig.Repo(r => r.CountEventsAsync(CancellationToken.None))).Should().Be(0);
        svc.Summary.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Settings_NilmDefaults_SavedValuesClamped()
    {
        using var rig = new Rig();
        var s = await rig.Settings.GetAsync(CancellationToken.None);
        s.NilmEnabled.Should().BeTrue();
        s.MinEventDeltaW.Should().Be(30);
        s.ClusterWindowDays.Should().Be(30);
        s.MlEnabled.Should().BeFalse("the rig turns ML off");

        await rig.Settings.SaveAsync(new KasaSettingsUpdate(true, 5, "", true, "", null, false,
            NilmEnabled: false, MinEventDeltaW: 1, ClusterWindowDays: 1000, MlEnabled: true), CancellationToken.None);
        s = await rig.Settings.GetAsync(CancellationToken.None);
        s.NilmEnabled.Should().BeFalse();
        s.MinEventDeltaW.Should().Be(KasaSettingsService.MinEventDeltaWMin);
        s.ClusterWindowDays.Should().Be(KasaSettingsService.MaxClusterWindowDays);
        s.MlEnabled.Should().BeTrue();
        (await TestSettings.Create(rig.Scopes, new KasaSettingsDefaults()).GetAsync(CancellationToken.None)).MlEnabled.Should().BeTrue();
    }
}
