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

        public NilmService Service(INilmGroundTruth? groundTruth = null) =>
            new(Scopes, Settings, Live, States, NullLogger<NilmService>.Instance, ModelPath, () => Now, TimeZoneInfo.Utc, groundTruth);

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

    /// <summary>A ground truth that answers for one plug (or every plug) with a fixed label, and records the questions.</summary>
    private sealed class FixedGroundTruth : INilmGroundTruth
    {
        private readonly Guid? _plug;
        private readonly string _label;
        public List<Guid> Asked { get; } = new();
        public FixedGroundTruth(Guid? plug, string label) { _plug = plug; _label = label; }
        public string? LabelFor(Guid plugId, DateTimeOffset tsUtc)
        {
            Asked.Add(plugId);
            return _plug is null || _plug == plugId ? _label : null;
        }
    }

    [Fact]
    public async Task GroundTruth_PlugConfirmedEvents_TakeThePhaseLabel_SignatureNamedAndKeptWhenLinkedToThePlug()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var washer = await rig.AddAsync("aabbccddee01", "Priza masina", PlugRole.Plug, PlugProtocols.Legacy);
        await SeedAsync(rig, main, washer);
        rig.Now = T0.AddMinutes(31);
        var truth = new FixedGroundTruth(washer, "Mașina · Spălare principală");

        await rig.Service(truth).RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Should().HaveCount(4).And.OnlyContain(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId == washer && e.Label == "Mașina · Spălare principală");
        var sig = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().ContainSingle().Subject;
        sig.Name.Should().Be("Mașina · Spălare principală", "the ground truth name wins over the plug's alias");
        sig.LabelSource.Should().Be(SignatureLabelSource.Plug);
        sig.PlugId.Should().Be(washer, "4 events from the plug: linked by the plug rule, name kept");
        truth.Asked.Should().OnlyContain(p => p == washer);
    }

    [Fact]
    public async Task GroundTruth_NeverForEventsWithoutThePlugsConfirmation_NorForAnotherPlug()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        using (var ctx = rig.Db.CreateContext())
            await new LiveRepository(ctx).SaveBatchAsync(House(main), Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        rig.Now = T0.AddMinutes(31);
        var always = new FixedGroundTruth(null, "Mașina · Centrifugare");

        await rig.Service(always).RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Should().NotBeEmpty().And.OnlyContain(e => e.Source == EventLabelSource.None && e.Label == null, "no plug matched: no ground truth");
        always.Asked.Should().BeEmpty("only events confirmed by a plug are asked about");
        (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().OnlyContain(s => s.Name.StartsWith("Necunoscut #"));

        using var rig2 = new Rig();
        var main2 = await rig2.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge2 = await rig2.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        await SeedAsync(rig2, main2, fridge2);
        rig2.Now = T0.AddMinutes(31);
        await rig2.Service(new FixedGroundTruth(Guid.NewGuid(), "Mașina · Centrifugare")).RunOnceAsync(CancellationToken.None);
        (await rig2.Repo(r => r.ListEventsAsync(T0, rig2.Now, CancellationToken.None))).Should().OnlyContain(e => e.Label == "Frigider");
        (await rig2.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().ContainSingle().Which.Name.Should().Be("Frigider");
        _ = fridge;
    }

    [Fact]
    public async Task FewerThanThreeEventsFromAPlug_SignatureStaysUnknown_MarkedPossiblePlug()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
    public async Task Mark_WithoutAStoredEvent_WaitsAndLabelsTheFirstLargeEventTheDetectorStores_Once()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);

        var mark = await svc.MarkAsync("  Fierbător ", "cooking", null, CancellationToken.None);
        mark!.Outcome.Should().Be(MarkOutcome.Pending);
        mark.Event.Should().BeNull();
        mark.Mark.ExpiresUtc.Should().Be(T0 + NilmService.MarkPendingFor);

        // +60 W at 10 s (too small for a mark), +2000 W at 20 s (the kettle), then +2000 W more at 100 s (something else).
        for (var t = 0; t < 140; t++)
            rig.Live.Publish(new LiveSample(main, T0.AddSeconds(t), 300 + (t >= 10 ? 60 : 0) + (t >= 20 ? 2000 : 0) + (t >= 100 ? 2000 : 0)));
        rig.Now = T0.AddSeconds(141);
        await svc.RunOnceAsync(CancellationToken.None);
        rig.Now = T0.AddSeconds(200);
        await svc.RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Select(e => Math.Round(e.DeltaP)).Should().Equal(60, 2000, 2000);
        events[0].Source.Should().Be(EventLabelSource.None, "below the mark's minimum step");
        events[1].Label.Should().Be("Fierbător");
        events[1].Source.Should().Be(EventLabelSource.User);
        events[2].Source.Should().Be(EventLabelSource.None, "a mark labels one event");
        var sig = await rig.Repo(r => r.GetSignatureAsync(events[1].SignatureId!.Value, CancellationToken.None));
        sig!.Name.Should().Be("Fierbător");
        sig.Category.Should().Be("cooking");
        sig.LabelSource.Should().Be(SignatureLabelSource.User);
    }

    [Fact]
    public async Task Mark_ATimeLongPastWithoutAnEvent_IsNotFound_NeverPending()
    {
        using var rig = new Rig();
        var svc = rig.Service();

        var r = await svc.MarkAsync("Fierbător", null, T0.AddMinutes(-10), CancellationToken.None);

        r!.Outcome.Should().Be(MarkOutcome.NotFound);
        (await svc.MarkAsync("   ", null, null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task PlugStream_LabelsTheEvent_LiveAndFromStorage_WithoutAny10SecondReading()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var fridge = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy);
        // Stored: the main meter steps +120 W at 300 s; the plug's stream (every 3 s, a ramp reading at the step), no Samples.
        using (var ctx = rig.Db.CreateContext())
        {
            var house = Enumerable.Range(0, 600).Select(t => new LiveSample(main, T0.AddSeconds(t), (t >= 300 ? 420 : 300) + (t % 3 - 1))).ToList();
            var plug = Enumerable.Range(0, 200).Select(k => k * 3).Select(t => new LiveSample(fridge, T0.AddSeconds(t), t < 300 ? 2 : t == 300 ? 60 : 122)).ToList();
            await new LiveRepository(ctx).SaveBatchAsync(house.Concat(plug).ToList(), Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        }
        rig.Now = T0.AddMinutes(11);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        var stored = (await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None))).Should().ContainSingle().Subject;
        stored.LabelPlugId.Should().Be(fridge, "the backfill reads the plug's LiveSamples");

        // Live: a −120 W step at 700 s, the plug's stream published, still no 10-second reading.
        for (var t = 600; t < 760; t++)
        {
            rig.Live.Publish(new LiveSample(main, T0.AddSeconds(t), (t < 700 ? 420 : 300) + (t % 3 - 1)));
            if (t % 3 == 0) rig.Live.Publish(new LiveSample(fridge, T0.AddSeconds(t), t < 700 ? 122 : 2));
        }
        rig.Now = T0.AddSeconds(760);
        await svc.RunOnceAsync(CancellationToken.None);
        rig.Now = T0.AddSeconds(800);
        await svc.RunOnceAsync(CancellationToken.None);
        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Should().HaveCount(2);
        events[1].DeltaP.Should().BeApproximately(-120, 2);
        events[1].LabelPlugId.Should().Be(fridge, "the live labelling keeps the plugs' stream in memory");
    }

    [Fact]
    public async Task NoisyPlug_MoreThanTwentyMatchesInAnHour_IsExcludedFromLearning()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
    public async Task Classifier_TrainedAtStartupWithoutASavedModel_AndOnRequest_AndAfterRegroupNow()
    {
        using var rig = new Rig(ml: true);
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        await SeedEventsAsync(rig, main);
        using (var ctx = rig.Db.CreateContext())   // re-clustered recently: only the missing model makes it train
            await new SettingsRepository(ctx).SetAsync(SettingKeys.NilmLastReclusterTs,
                T0.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        var svc = rig.Service();

        await svc.RunOnceAsync(CancellationToken.None);
        svc.ModelInfo.Should().NotBeNull("36 labels in 3 classes and no saved model: trained at startup, not at 03:30");
        File.Exists(rig.ModelPath).Should().BeTrue();
        (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().BeEmpty("no re-clustering ran");

        var now = await svc.TrainNowAsync(CancellationToken.None);
        now.Trained.Should().BeTrue();
        now.Info!.Classes.Should().Equal("Boiler", "Frigider", "TV");
        now.Info.Accuracy.Should().BeGreaterThan(0.9);
        now.LabeledEvents.Should().Be(36);
        now.LabeledClasses.Should().Be(3);
        now.Duration.Should().BePositive();

        (await svc.RegroupNowAsync(CancellationToken.None)).Training!.Trained.Should().BeTrue("the labels may have moved: retrained");

        // A restart with the saved model does not train again at startup.
        var again = rig.Service();
        await again.RunOnceAsync(CancellationToken.None);
        again.Classifier.HasModel.Should().BeTrue();
    }

    [Fact]
    public async Task TrainNow_WithTooFewLabels_SaysWhatIsMissing()
    {
        using var rig = new Rig(ml: true);
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        await SeedEventsAsync(rig, main, perClass: 4);
        var r = await rig.Service().TrainNowAsync(CancellationToken.None);
        r.Trained.Should().BeFalse();
        r.Info.Should().BeNull();
        r.LabeledEvents.Should().Be(6);
        r.LabeledClasses.Should().Be(3);
    }

    [Fact]
    public async Task UserActions_LabelEvent_Rename_Category_Hide_Merge_Split()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
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

    // ------------------------------------------------------------------ 0.8.2: anchors, automatic merge, „fișa”, „Regrupează acum”

    private static async Task<ApplianceSignature> AddSigAsync(Rig rig, string name, double p, double q, double t,
        SignatureLabelSource source = SignatureLabelSource.Cluster, Guid? plug = null) =>
        await rig.Repo(r => r.AddSignatureAsync(new ApplianceSignature(0, name, "bi-question-circle", ApplianceCategories.Other, p, q, t, 0, null,
            source, plug, false), CancellationToken.None));

    /// <summary>Stores events (minutes before T0) in a signature; an event with a plug is labelled by it.</summary>
    private static async Task<List<MeterEvent>> AddEventsAsync(Rig rig, Guid main, long? sig,
        params (double Minutes, double P, double Q, double Ratio, double Rise, Guid? Plug)[] events)
    {
        var list = events.Select(x => new MeterEvent(0, main, T0.AddMinutes(-x.Minutes), x.P, x.Q, 0, x.Ratio, x.Rise, 500, sig,
            x.Plug is null ? null : "Hol mic", x.Plug is null ? EventLabelSource.None : EventLabelSource.AutoPlug, x.Plug)).ToList();
        var stored = await rig.Repo(r => r.AddEventsAsync(list, CancellationToken.None));
        await rig.Repo(r => r.RecomputeSignaturesAsync(sig is { } id ? new[] { id } : Array.Empty<long>(), T0.AddDays(-30), CancellationToken.None));
        return stored.ToList();
    }

    [Fact]
    public async Task NewEvent_GoesToTheLabelledSignatureWithinOneAndAHalfEps_EvenWhenAnAnonymousOneIsNearer()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var kettle = await AddSigAsync(rig, "Fierbător", 1520, 105, 1.0, SignatureLabelSource.User);
        var nearer = await AddSigAsync(rig, "", 1400, 20, 1.0);
        using (var ctx = rig.Db.CreateContext())
        {
            var house = Enumerable.Range(0, 900).Select(t => new LiveSample(main, T0.AddSeconds(t), (t >= 300 && t < 420 ? 1825 : 400) + (t % 3 - 1))).ToList();
            await new LiveRepository(ctx).SaveBatchAsync(house, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        }
        rig.Now = T0.AddMinutes(16);
        await rig.Service().RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Should().HaveCount(2);
        events.Select(e => e.DeltaP).Should().BeEquivalentTo(new[] { 1425.0, -1425.0 }, o => o.Using<double>(x => x.Subject.Should().BeApproximately(x.Expectation, 2)).WhenTypeIs<double>());
        events.Should().OnlyContain(e => e.SignatureId == kettle.Id, "the kettle is labelled: it is tried first, with 1.5 × eps (−6 % is inside)");
        (await rig.Repo(r => r.GetSignatureAsync(nearer.Id, CancellationToken.None)))!.Count.Should().Be(0);
    }

    [Fact]
    public async Task Nightly_AnchorsTakeTheEventsInTheirRadius_UnknownTransientsDoNotSplitAComponent_UserLabelKept()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var hol = await rig.AddAsync("aabbccddee03", "Hol mic", PlugRole.Plug, PlugProtocols.Legacy);
        // Real events of 2 Nov (rounded): the kettle (labelled), its starts at −6 % in two anonymous groups, and the washing
        // machine's heater split in two by the ratios ≈ 0 of the starts that fell between two samples.
        var kettle = await AddSigAsync(rig, "Fierbător", 0, 0, 0, SignatureLabelSource.User);
        await AddEventsAsync(rig, main, kettle.Id, (600, 1570, 109, 1.003, 0, null), (599, -1470, -101, 1.006, 1, null));
        var s59 = await AddSigAsync(rig, "", 0, 0, 0);
        var s88 = await AddSigAsync(rig, "", 0, 0, 0);
        var starts = new List<MeterEvent>();
        starts.AddRange(await AddEventsAsync(rig, main, s59.Id, (60, 1425, 63, 0.999, 0, null), (300, 1394, 74, 1.013, 0, null), (299, -1383, -11, 1.004, 0, null)));
        starts.AddRange(await AddEventsAsync(rig, main, s88.Id, (62, 1411, 113, 0.026, 5, null), (61, -1417, -111, 1.001, 0, null), (58, 1439, 87, 0.012, 2, null)));
        var s53 = await AddSigAsync(rig, "", 0, 0, 0);
        var s55 = await AddSigAsync(rig, "", 0, 0, 0);
        await AddEventsAsync(rig, main, s53.Id, (200, 2008, 122, 1.001, 0, null), (199, -2002, -134, 0, 2, null), (190, -1994, -108, 0.999, 0, hol),
            (180, 1963, 115, 1.013, 0, null), (170, 1993, 126, 0.985, 1, hol), (169, -1991, -101, 1.007, 0, hol));
        await AddEventsAsync(rig, main, s55.Id, (198, 1967, 117, 0, 2, null), (189, 1978, 99, 1.018, 0, hol), (188, -1929, -156, 1.008, 1, null),
            (179, -1945, -132, 1.038, 11, null), (175, 2007, 175, 0.008, 2, hol), (174, -1958, -110, 1.033, 6, hol));
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);   // stored events and no re-clustering yet: it runs at startup

        var after = await rig.Repo(r => r.ListEventsAsync(T0.AddDays(-1), T0, CancellationToken.None));
        foreach (var e in starts.Where(e => e.DeltaP > 1400))
            after.Single(x => x.Id == e.Id).SignatureId.Should().Be(kettle.Id, $"the start of {e.DeltaP:0} W is within 1.5 × eps of the kettle");
        var k = await rig.Repo(r => r.GetSignatureAsync(kettle.Id, CancellationToken.None));
        k!.Name.Should().Be("Fierbător");
        k.LabelSource.Should().Be(SignatureLabelSource.User);
        var washer = after.Where(e => Math.Abs(e.DeltaP) > 1900).Select(e => e.SignatureId).Distinct().ToList();
        washer.Should().ContainSingle("one heater, one signature").Which.Should().NotBeNull();
        (await rig.Repo(r => r.GetSignatureAsync(washer[0]!.Value, CancellationToken.None)))!.Count.Should().Be(12);
        using (var ctx = rig.Db.CreateContext())
            long.Parse((await new SettingsRepository(ctx).GetAsync(SettingKeys.NilmLastReclusterTs, CancellationToken.None))!)
                .Should().Be(T0.ToUnixTimeSeconds());
    }

    /// <summary>Two components 9 % apart in |ΔP|, apart in ΔQ (two clusters), both „posibil Hol mic”: rule b merges them.</summary>
    private static async Task<(ApplianceSignature X, ApplianceSignature Y)> SeedSamePlugAsync(Rig rig, Guid main, Guid hol)
    {
        var x = await AddSigAsync(rig, "", 0, 0, 0);
        var y = await AddSigAsync(rig, "", 0, 0, 0);
        await AddEventsAsync(rig, main, x.Id, (100, 1000, 0, 1, 0, hol), (95, -1000, 0, 1, 0, hol), (90, 1005, 0, 1, 0, hol),
            (85, -1003, 0, 1, 0, null), (80, 998, 0, 1, 0, null), (75, -997, 0, 1, 0, null));
        await AddEventsAsync(rig, main, y.Id, (70, 1100, 200, 1, 0, hol), (65, -1100, -200, 1, 0, hol), (60, 1102, 200, 1, 0, null),
            (55, -1098, -200, 1, 0, null), (50, 1101, 200, 1, 0, null), (45, -1099, -200, 1, 0, null));
        await rig.Repo(r => r.SaveRunsAsync(new[] { new ApplianceRun(0, y.Id, T0.AddDays(-40), T0.AddDays(-40).AddHours(1), 1100, 1100, 1, 2) },
            CancellationToken.None));
        return (x, y);
    }

    [Fact]
    public async Task RegroupNow_MergesSignaturesOfTheSamePlug_EventsAndRunsGoToTheSurvivor()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var hol = await rig.AddAsync("aabbccddee03", "Hol mic", PlugRole.Plug, PlugProtocols.Legacy);
        var (x, y) = await SeedSamePlugAsync(rig, main, hol);
        var svc = rig.Service();

        var result = await svc.RegroupNowAsync(CancellationToken.None);

        result.Merged.Should().Be(1);
        result.Signatures.Should().Be(1);
        var sigs = await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None));
        sigs.Should().ContainSingle().Which.Id.Should().Be(x.Id, "6 events each: the older id survives");
        sigs[0].Count.Should().Be(12);
        sigs[0].PlugId.Should().BeNull("5 of 12 from the plug: still only „posibil”");
        (await rig.Repo(r => r.ListRunsAsync(T0.AddDays(-41), T0.AddDays(-39), CancellationToken.None))).Should().ContainSingle()
            .Which.SignatureId.Should().Be(x.Id, "the absorbed signature's runs are re-indexed, not deleted");
        (await rig.Repo(r => r.GetSignatureAsync(y.Id, CancellationToken.None))).Should().BeNull();

        var view = await svc.GetProfileAsync(x.Id, CancellationToken.None);
        view!.PlugAlias.Should().Be("Hol mic");
        view.PlugLinked.Should().BeFalse();
        view.Profile.Events.Should().Be(12);
        view.Profile.TopPlugEvents.Should().Be(5);
        (await svc.GetProfileAsync(y.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Startup_ReclustersWhenTheLastJobIsOlderThan24h_NotWhenItIsRecent()
    {
        foreach (var (hoursAgo, merged) in new[] { (25, true), (1, false) })
        {
            using var rig = new Rig();
            var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
            var hol = await rig.AddAsync("aabbccddee03", "Hol mic", PlugRole.Plug, PlugProtocols.Legacy);
            await SeedSamePlugAsync(rig, main, hol);
            using (var ctx = rig.Db.CreateContext())
                await new SettingsRepository(ctx).SetAsync(SettingKeys.NilmLastReclusterTs,
                    T0.AddHours(-hoursAgo).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);

            await rig.Service().RunOnceAsync(CancellationToken.None);

            (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().HaveCount(merged ? 1 : 2, $"last job {hoursAgo} h ago");
        }
    }

    [Fact]
    public async Task MergeWith_TheUserLabelledSignatureSurvives_AddTo_KeepsTheChosenOne()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var lamp = await AddSigAsync(rig, "Lampă", 0, 0, 0, SignatureLabelSource.User);
        await AddEventsAsync(rig, main, lamp.Id, (30, 60, 0, 1, 0, null));
        var big = await AddSigAsync(rig, "", 0, 0, 0);
        await AddEventsAsync(rig, main, big.Id, (20, 61, 0, 1, 0, null), (19, -61, 0, 1, 0, null), (18, 59, 0, 1, 0, null));
        var other = await AddSigAsync(rig, "", 0, 0, 0);
        await AddEventsAsync(rig, main, other.Id, (10, 300, 0, 1, 0, null), (9, -300, 0, 1, 0, null));
        await rig.Repo(async r => { await r.UpdateSignatureAsync(other with { Category = "lighting", Icon = "bi-lightbulb" }, CancellationToken.None); return true; });
        var svc = rig.Service();

        (await svc.MergeWithAsync(big.Id, lamp.Id, CancellationToken.None)).Should().Be(lamp.Id, "the user's label wins over more events");
        (await rig.Repo(r => r.GetSignatureAsync(lamp.Id, CancellationToken.None)))!.Count.Should().Be(4);
        (await rig.Repo(r => r.GetSignatureAsync(big.Id, CancellationToken.None))).Should().BeNull();

        (await svc.MergeAsync(other.Id, lamp.Id, CancellationToken.None)).Should().BeTrue();   // the selector above the table: explicit
        var l = await rig.Repo(r => r.GetSignatureAsync(lamp.Id, CancellationToken.None));
        l!.Name.Should().Be("Lampă");
        l.Count.Should().Be(6);
        l.Category.Should().Be("lighting", "the survivor had no category: it takes the absorbed one's");
        (await svc.MergeWithAsync(lamp.Id, lamp.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AddTo_MergesACloseComponent_OnlyNamesADistantOne()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var heater = await AddSigAsync(rig, "Mașina de spălat", 0, 0, 0, SignatureLabelSource.User);
        await AddEventsAsync(rig, main, heater.Id, (90, 1990, 120, 1, 0, null), (80, -1985, -118, 1, 0, null));
        await rig.Repo(async r => { await r.UpdateSignatureAsync((await r.GetSignatureAsync(heater.Id, CancellationToken.None))! with { Category = "washer", Icon = "bi-water" }, CancellationToken.None); return true; });
        var heater2 = await AddSigAsync(rig, "", 0, 0, 0);
        await AddEventsAsync(rig, main, heater2.Id, (70, 1900, 130, 1, 0, null), (60, -1905, -128, 1, 0, null));
        var motor = await AddSigAsync(rig, "", 0, 0, 0);
        await AddEventsAsync(rig, main, motor.Id, (50, 280, 230, 1, 0, null), (49, -282, -231, 1, 0, null), (48, 279, 229, 1, 0, null));
        var svc = rig.Service();
        var sigs = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).ToDictionary(x => x.Id);
        NilmService.AddToMerges(sigs[heater2.Id], sigs[heater.Id]).Should().BeTrue();
        NilmService.AddToMerges(sigs[motor.Id], sigs[heater.Id]).Should().BeFalse();

        (await svc.AddToAsync(motor.Id, heater.Id, CancellationToken.None)).Should().Be(AddToOutcome.Named);
        var m = await rig.Repo(r => r.GetSignatureAsync(motor.Id, CancellationToken.None));
        m!.Name.Should().Be("Mașina de spălat");
        m.Category.Should().Be("washer");
        m.LabelSource.Should().Be(SignatureLabelSource.User);
        m.Count.Should().Be(3, "a distant component keeps its events: two rows with one name");
        (await rig.Repo(r => r.GetSignatureAsync(heater.Id, CancellationToken.None)))!.Count.Should().Be(2);

        (await svc.AddToAsync(heater2.Id, heater.Id, CancellationToken.None)).Should().Be(AddToOutcome.Merged);
        (await rig.Repo(r => r.GetSignatureAsync(heater.Id, CancellationToken.None)))!.Count.Should().Be(4);
        (await rig.Repo(r => r.GetSignatureAsync(heater2.Id, CancellationToken.None))).Should().BeNull();

        // The next re-clustering keeps both rows: the same name on distant components is not merged (rule c needs close centres).
        await svc.RegroupNowAsync(CancellationToken.None);
        (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Where(x => x.Name == "Mașina de spălat").Should().HaveCount(2);
    }

    [Fact]
    public async Task Regroup_EventsAnAnchorCaughtCanLeave_OnlyTheUsersOwnLabelsArePinned()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var kettle = await AddSigAsync(rig, "Fierbător", 0, 0, 0, SignatureLabelSource.User);
        var own = await AddEventsAsync(rig, main, kettle.Id, (500, 1520, 105, 1, 0, null), (499, -1515, -100, 1, 0, null));
        await rig.Repo(async r => { await r.UpdateEventAsync(own[0] with { Label = "Fierbător", Source = EventLabelSource.User }, CancellationToken.None); return true; });
        // Caught earlier by the kettle: a 1.9 kW load (far from 1.5 kW now) and a 1.2 kW one.
        var stray = await AddEventsAsync(rig, main, kettle.Id, (300, 1900, 300, 1, 0, null), (200, 1200, 300, 1, 0, null));
        await rig.Repo(r => r.RecomputeSignaturesAsync(new[] { kettle.Id }, T0.AddDays(-30), CancellationToken.None));
        var svc = rig.Service();

        await svc.RegroupNowAsync(CancellationToken.None);

        var after = (await rig.Repo(r => r.ListEventsAsync(T0.AddDays(-1), T0, CancellationToken.None))).ToDictionary(e => e.Id);
        after[own[0].Id].SignatureId.Should().Be(kettle.Id, "labelled by the user: pinned");
        after[own[1].Id].SignatureId.Should().Be(kettle.Id, "within the kettle's radius: stays");
        after[stray[0].Id].SignatureId.Should().NotBe(kettle.Id, "outside the radius: it leaves (no ratchet)");
        after[stray[1].Id].SignatureId.Should().NotBe(kettle.Id);
        var k = await rig.Repo(r => r.GetSignatureAsync(kettle.Id, CancellationToken.None));
        k!.Name.Should().Be("Fierbător");
        k.Count.Should().Be(2);
    }

    [Fact]
    public async Task Regroup_AHiddenNamedSignatureIsNoAnchor()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var hidden = await AddSigAsync(rig, "Ignorat", 500, 0, 1, SignatureLabelSource.User);
        await rig.Repo(async r => { await r.UpdateSignatureAsync((await r.GetSignatureAsync(hidden.Id, CancellationToken.None))! with { Hidden = true }, CancellationToken.None); return true; });
        var events = await AddEventsAsync(rig, main, null, (30, 505, 0, 1, 0, null), (20, -505, 0, 1, 0, null), (10, 498, 0, 1, 0, null));
        await rig.Service().RegroupNowAsync(CancellationToken.None);
        var after = await rig.Repo(r => r.ListEventsAsync(T0.AddDays(-1), T0, CancellationToken.None));
        after.Where(e => events.Any(x => x.Id == e.Id)).Should().OnlyContain(e => e.SignatureId != hidden.Id);
    }

    // ------------------------------------------------------------------ „un singur aparat pe priză” (0.9.1)

    private static async Task SetSingleAsync(Rig rig, Guid plug, string? name, string? category, bool single = true)
    {
        using var ctx = rig.Db.CreateContext();
        (await new PlugRepository(ctx).SetApplianceAsync(plug, single, name, category, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task SingleAppliancePlug_LabelsWithTheApplianceName_AndLinksItsSignatureAtOnce_NotAmongTheUnknown()
    {
        // One fridge cycle = 2 confirmed events: below the plug rule's 3 events, so a plain plug leaves the signature unlinked.
        using (var plain = new Rig())
        {
            var m = await plain.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
            var p = await plain.AddAsync("aabbccddee01", "Priza bucatarie", PlugRole.Plug, PlugProtocols.Legacy);
            await SeedAsync(plain, m, p, cycles: 1);
            plain.Now = T0.AddMinutes(31);
            await plain.Service().RunOnceAsync(CancellationToken.None);
            (await plain.Repo(r => r.ListEventsAsync(T0, plain.Now, CancellationToken.None))).Should().HaveCount(2).And.OnlyContain(e => e.Label == "Priza bucatarie");
            (await plain.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().ContainSingle().Which.PlugId.Should().BeNull("2 events < 3");
        }

        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var plug = await rig.AddAsync("aabbccddee01", "Priza bucatarie", PlugRole.Plug, PlugProtocols.Legacy);
        await SetSingleAsync(rig, plug, "Frigider", "fridge");
        await SeedAsync(rig, main, plug, cycles: 1);
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();

        await svc.RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Should().HaveCount(2).And.OnlyContain(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId == plug && e.Label == "Frigider",
            "the appliance's name, not the plug's alias");
        var sig = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().ContainSingle().Subject;
        sig.PlugId.Should().Be(plug, "linked from the first confirmed event, without the 60 % / 3 events threshold");
        sig.Name.Should().Be("Frigider");
        sig.Category.Should().Be("fridge");
        sig.Icon.Should().Be(ApplianceCategories.Get("fridge").Icon);
        sig.LabelSource.Should().Be(SignatureLabelSource.Plug);
        events.Should().OnlyContain(e => e.SignatureId == sig.Id);

        var overview = await svc.GetOverviewAsync(0, 0, CancellationToken.None);
        overview.PlugMeasured.Should().ContainSingle().Which.Signature.Id.Should().Be(sig.Id);
        overview.DetectedTotal.Should().Be(0, "never in the list of unknown (detected) signatures");
        var row = overview.Plugs.Should().ContainSingle().Subject;
        row.SingleAppliance.Should().BeTrue();
        row.ApplianceName.Should().Be("Frigider");
        row.State.Should().Be(PlugNilmState.Auto, "not excluded from learning: the plug still labels its events");

        // The nightly re-clustering keeps the link (the plug's events are pinned) and the name.
        await svc.NightlyAsync(rig.Now, CancellationToken.None);
        var after = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().ContainSingle().Subject;
        after.PlugId.Should().Be(plug);
        after.Name.Should().Be("Frigider");
    }

    [Fact]
    public async Task SettingTheAppliance_RenamesTheSignaturesLinkedToThePlug_AndClearingItGivesBackTheAlias()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var plug = await rig.AddAsync("aabbccddee01", "Priza bucatarie", PlugRole.Plug, PlugProtocols.Legacy);
        await SeedAsync(rig, main, plug);   // 4 events: linked by the plain plug rule, named after the alias
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        var sig = (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Single();
        sig.Name.Should().Be("Priza bucatarie");

        var row = await svc.SetPlugApplianceAsync(plug, true, "  Frigider   Arctic ", "fridge", CancellationToken.None);
        row!.ApplianceName.Should().Be("Frigider Arctic");
        var named = (await rig.Repo(r => r.GetSignatureAsync(sig.Id, CancellationToken.None)))!;
        named.Name.Should().Be("Frigider Arctic", "the linked signatures follow at once, not only with the next event");
        named.Category.Should().Be("fridge");

        (await svc.SetPlugApplianceAsync(plug, false, null, null, CancellationToken.None))!.SingleAppliance.Should().BeFalse();
        (await rig.Repo(r => r.GetSignatureAsync(sig.Id, CancellationToken.None)))!.Name.Should().Be("Priza bucatarie");
        using (var ctx = rig.Db.CreateContext())
        {
            var p = (await new PlugRepository(ctx).ListAllAsync(CancellationToken.None)).Single(x => x.Id == plug);
            p.ApplianceCategory.Should().Be("fridge", "a null category keeps the current one");
            p.Appliance.Should().BeNull();
        }
        (await svc.SetPlugApplianceAsync(main, true, "Casa", null, CancellationToken.None)).Should().BeNull("a meter measures more than one appliance");
        (await svc.SetPlugApplianceAsync(Guid.NewGuid(), true, "X", null, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SingleAppliancePlug_IsLeftOutOfTheTraining_ButStaysUsedForLabelling()
    {
        using var rig = new Rig(ml: true);
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var plugs = new Dictionary<string, Guid>
        {
            ["Frigider"] = await rig.AddAsync("aabbccddee01", "Frigider", PlugRole.Plug, PlugProtocols.Legacy),
            ["Boiler"] = await rig.AddAsync("aabbccddee02", "Boiler", PlugRole.Plug, PlugProtocols.Legacy),
            ["TV"] = await rig.AddAsync("aabbccddee03", "TV", PlugRole.Plug, PlugProtocols.Legacy),
        };
        using (var ctx = rig.Db.CreateContext())
        {
            var repo = new NilmRepository(ctx);
            var t = T0.AddHours(-20);
            foreach (var e in NilmClassifierTests.Labeled(24).OrderBy(e => e.Label).ThenBy(e => e.Id))
            {
                t = t.AddMinutes(5);
                await repo.AddEventsAsync(new[] { e with { Id = 0, PlugId = main, TsUtc = t, LabelPlugId = plugs[e.Label!] } }, CancellationToken.None);
            }
        }
        using (var ctx = rig.Db.CreateContext())   // re-clustered recently: no regroup at startup
            await new SettingsRepository(ctx).SetAsync(SettingKeys.NilmLastReclusterTs,
                T0.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        (await svc.TrainNowAsync(CancellationToken.None)).Info!.Classes.Should().Equal("Boiler", "Frigider", "TV");

        await svc.SetPlugApplianceAsync(plugs["Frigider"], true, "Frigider", "fridge", CancellationToken.None);

        var trained = await svc.TrainNowAsync(CancellationToken.None);
        trained.Info!.Classes.Should().Equal(new[] { "Boiler", "TV" }, "its events are explained by the plug: neither a class nor examples");
        trained.LabeledEvents.Should().Be(48);
        trained.Info.Examples.Should().Be(48);
        var overview = await svc.GetOverviewAsync(0, 0, CancellationToken.None);
        overview.LabeledEvents.Should().Be(48);
        overview.LabeledClasses.Should().Be(2);
        overview.Plugs.Single(p => p.PlugId == plugs["Frigider"]).State.Should().Be(PlugNilmState.Auto, "NilmIgnored is not touched");
        NilmService.TrainingExcluded(new[] { new Plug(Guid.NewGuid(), "m", IPAddress.Loopback, "a", false, "", "", "", "", T0, T0, false, null, SingleAppliance: true) })
            .Should().HaveCount(1);
    }

    [Fact]
    public async Task SingleAppliancePlug_IsNeverExcludedAutomatically_ForManyMatchesAnHour()
    {
        using var rig = new Rig();
        var main = await rig.AddAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        var ac = await rig.AddAsync("aabbccddee02", "Priza AC", PlugRole.Plug, PlugProtocols.Legacy);
        await SetSingleAsync(rig, ac, "AC sufragerie", "hvac");
        bool On(int t) => t / 60 % 2 == 1;   // +40 W every other minute, for 50 minutes
        using (var ctx = rig.Db.CreateContext())
        {
            var house = Enumerable.Range(0, 3000).Select(t => new LiveSample(main, T0.AddSeconds(t), 300 + (On(t) ? 40 : 0) + (t % 3 - 1))).ToList();
            await new LiveRepository(ctx).SaveBatchAsync(house, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
            await new SampleRepository(ctx).InsertManyAsync(Enumerable.Range(0, 300).Select(k => k * 10 + 4)
                .Select(t => new Sample(0, ac, T0.AddSeconds(t), true, On(t) ? 42_000 : 2_000, 230_000, null, null, null, true, null)).ToList(),
                CancellationToken.None);
        }
        rig.Now = T0.AddMinutes(51);
        await rig.Service().RunOnceAsync(CancellationToken.None);

        var events = await rig.Repo(r => r.ListEventsAsync(T0, rig.Now, CancellationToken.None));
        events.Count(e => e.Source == EventLabelSource.AutoPlug).Should().BeGreaterThan(PlugNilmStates.MaxMatchesPerHour,
            "its matches are the appliance: the events do not become unknown");
        events.Where(e => e.Source == EventLabelSource.AutoPlug).Should().OnlyContain(e => e.Label == "AC sufragerie");
        using (var ctx = rig.Db.CreateContext())
            (await new PlugRepository(ctx).ListAllAsync(CancellationToken.None)).Single(p => p.Id == ac).NilmState.Should().Be(PlugNilmState.Auto);
        (await rig.Repo(r => r.ListSignaturesAsync(CancellationToken.None))).Should().NotBeEmpty()
            .And.OnlyContain(s => s.PlugId == ac && s.Name == "AC sufragerie" && s.Category == "hvac");
    }
}
