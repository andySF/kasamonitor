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
/// 0.13.1, through the service: a signature of a plug with a single appliance holds only what that plug confirmed or the user
/// labelled. Measured on 2026-11-04 on a fridge of ≈ 60 W: 86 real transitions in 24 h, 1,969 events in its signature with a
/// mean step of 39 W, none with the source auto-plug, 171 with the source model and the label of another plug.
/// </summary>
public class NilmExclusiveSignatureTests
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
        public Guid Main;
        public Guid Fridge;
        public string ModelPath { get; } = Path.Combine(Path.GetTempPath(), $"nilm-{Guid.NewGuid():N}.zip");

        public Rig(bool ml = false)
        {
            Scopes = new TestScopeFactory(Db.CreateContext);
            Settings = TestSettings.Create(Scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", MlEnabled: ml));
        }

        public NilmService Service() => new(Scopes, Settings, Live, States, NullLogger<NilmService>.Instance, ModelPath, () => Now, TimeZoneInfo.Utc);

        public async Task<Guid> AddPlugAsync(string mac, string alias, PlugRole role, string protocol)
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

        public Task<ApplianceSignature> AddSignatureAsync(string name, double p, Guid? plug, SignatureLabelSource source = SignatureLabelSource.Plug,
            int count = 0, int? min = null, int? max = null, long? splitFrom = null, bool hidden = false) =>
            Repo(r => r.AddSignatureAsync(new ApplianceSignature(0, name, "bi-box", "other", p, 0, 0, count, T0.AddDays(-5), source, plug, hidden, min, max, splitFrom), CancellationToken.None));

        public async Task SetStateAsync(Guid plug, PlugNilmState state)
        {
            using var ctx = Db.CreateContext();
            await new PlugRepository(ctx).SetNilmStateAsync(plug, state, CancellationToken.None);
        }

        public Task<IReadOnlyList<MeterEvent>> AddEventsAsync(IEnumerable<MeterEvent> events) => Repo(r => r.AddEventsAsync(events.ToList(), CancellationToken.None));

        public async Task<IReadOnlyList<MeterEvent>> EventsOfAsync(long signatureId) =>
            await Repo(r => r.ListEventsBySignaturesAsync(new[] { signatureId }, DateTimeOffset.UnixEpoch, CancellationToken.None));

        public Task<IReadOnlyList<MeterEvent>> AllEventsAsync() => Repo(r => r.ListEventsAsync(DateTimeOffset.UnixEpoch, Now.AddDays(1), CancellationToken.None));

        public Task<IReadOnlyList<ApplianceSignature>> SignaturesAsync() => Repo(r => r.ListSignaturesAsync(CancellationToken.None));

        public async Task<ApplianceSignature> SigAsync(long id) => (await Repo(r => r.GetSignatureAsync(id, CancellationToken.None)))!;

        public Task<IReadOnlyList<ApplianceRun>> RunsOfAsync(long id) => Repo(r => r.ListRunsBySignatureAsync(id, DateTimeOffset.UnixEpoch, CancellationToken.None));

        /// <summary>The seconds of the main meter from T0: 300 W, plus <paramref name="power"/> W during each (start, seconds).</summary>
        public async Task HouseAsync(int totalSeconds, double power, params (int Start, int Seconds)[] on)
        {
            var samples = Enumerable.Range(0, totalSeconds)
                .Select(t => new LiveSample(Main, T0.AddSeconds(t), on.Any(x => t >= x.Start && t < x.Start + x.Seconds) ? 300 + power : 300)).ToList();
            using var ctx = Db.CreateContext();
            await new LiveRepository(ctx).SaveBatchAsync(samples, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        }

        /// <summary>The fridge plug's 10-second readings: 2 W, and <paramref name="power"/> + 2 W during each (start, seconds).</summary>
        public async Task PlugReadingsAsync(Guid plug, int totalSeconds, double power, params (int Start, int Seconds)[] on)
        {
            var readings = Enumerable.Range(0, totalSeconds / 10).Select(k =>
            {
                var t = k * 10 + 4;
                var w = on.Any(x => t >= x.Start && t < x.Start + x.Seconds) ? (long)((power + 2) * 1000) : 2_000L;
                return new Sample(0, plug, T0.AddSeconds(t), true, w, 230_000, null, null, null, true, null);
            }).ToList();
            using var ctx = Db.CreateContext();
            await new SampleRepository(ctx).InsertManyAsync(readings, CancellationToken.None);
        }

        public void Dispose()
        {
            Db.Dispose();
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
        }
    }

    /// <summary>A main meter and the plug „Frigider” with a single appliance.</summary>
    private static async Task<Rig> RigAsync(bool ml = false, bool single = true)
    {
        var rig = new Rig(ml);
        rig.Main = await rig.AddPlugAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        rig.Fridge = await rig.AddPlugAsync("aabbccddee01", "Priza frigider", PlugRole.Plug, PlugProtocols.Legacy);
        using var ctx = rig.Db.CreateContext();
        (await new PlugRepository(ctx).SetApplianceAsync(rig.Fridge, single, "Frigider", "fridge", CancellationToken.None)).Should().BeTrue();
        return rig;
    }

    private static MeterEvent Ev(Rig rig, DateTimeOffset t, double p, long? sig, EventLabelSource source = EventLabelSource.None, string? label = null, Guid? plug = null) =>
        new(0, rig.Main, t, p, 0, 0, 1, 0, p > 0 ? 300 : 300 - p, sig, label, source, plug);

    private static MeterEvent Confirmed(Rig rig, DateTimeOffset t, double p, long? sig) =>
        Ev(rig, t, p, sig, EventLabelSource.AutoPlug, "Frigider", rig.Fridge);

    /// <summary>A switch-on and, 20 s later, its switch-off.</summary>
    private static IEnumerable<MeterEvent> Pair(Func<DateTimeOffset, double, MeterEvent> make, DateTimeOffset t, double p) =>
        new[] { make(t, p), make(t.AddSeconds(20), -p) };

    [Fact]
    public async Task TheRealShape_AFridgeOf60W_With1900EventsOf37WAround86Confirmed_KeepsThe86_ItsCentreBackAt60()
    {
        using var rig = await RigAsync();
        var fridge = await rig.AddSignatureAsync("Frigider", 39, rig.Fridge, count: 1986);
        var events = new List<MeterEvent>();
        var from = T0.AddDays(-3);
        var confirmedPairs = 0;
        for (var k = 0; k < 993; k++)
        {
            var t = from.AddMinutes(k);
            if (k % 23 == 0 && confirmedPairs < 43)
            {
                confirmedPairs++;
                events.AddRange(Pair((at, p) => Confirmed(rig, at, p, fridge.Id), t, 60));
            }
            else
            {
                // As measured: most without a label, some predicted by the model with the name of a plug without a signature.
                var model = k % 11 == 0;
                events.AddRange(Pair((at, p) => Ev(rig, at, p, fridge.Id, model ? EventLabelSource.Model : EventLabelSource.None, model ? "Priza UPS" : null),
                    t, 36 + k % 3));
            }
        }
        await rig.AddEventsAsync(events);
        var svc = rig.Service();

        var result = await svc.RegroupNowAsync(CancellationToken.None);

        result.Moved.Should().Be(1900);
        var after = await rig.SigAsync(fridge.Id);
        after.Count.Should().Be(86);
        after.MeanDeltaP.Should().BeApproximately(60, 0.5);
        after.PlugId.Should().Be(rig.Fridge);
        after.Name.Should().Be("Frigider");
        after.LabelSource.Should().Be(SignatureLabelSource.Plug);
        var inside = await rig.EventsOfAsync(fridge.Id);
        inside.Should().HaveCount(86).And.OnlyContain(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId == rig.Fridge);

        (await rig.RunsOfAsync(fridge.Id)).Should().BeEmpty("since 0.13.2 its runs come from the plug's readings, and this test has none");

        var unknown = (await rig.SignaturesAsync()).Where(s => s.Id != fridge.Id).ToList();
        unknown.Sum(s => s.Count).Should().Be(1900, "they went through DBSCAN and live in the unknown ones now");
        unknown.Should().OnlyContain(s => s.PlugId == null && s.LabelSource == SignatureLabelSource.Cluster);
        var all = await rig.AllEventsAsync();
        all.Count(e => e.Source == EventLabelSource.Model && e.Label == "Priza UPS").Should().BeGreaterThan(0, "the prediction stays on the event");

        var overview = await svc.GetOverviewAsync(0, 0, CancellationToken.None);
        overview.PlugMeasured.Should().ContainSingle().Which.Signature.Count.Should().Be(86, "/consumatori counts what the signature holds");

        // It holds: the next night changes nothing in it.
        var again = await svc.NightlyAsync(rig.Now, CancellationToken.None);
        again.Moved.Should().Be(0);
        (await rig.SigAsync(fridge.Id)).Count.Should().Be(86);
    }

    [Fact]
    public async Task ThePlainPlug_KeepsItsRule_WhatLiesWithinTheRadiusStays()
    {
        using var rig = await RigAsync(single: false);
        var sig = await rig.AddSignatureAsync("Priza frigider", 60, rig.Fridge);
        var events = new List<MeterEvent>();
        for (var k = 0; k < 10; k++)
            events.AddRange(Pair((at, p) => k % 3 == 0 ? Ev(rig, at, p, sig.Id) : Confirmed(rig, at, p, sig.Id), T0.AddDays(-3).AddMinutes(k), 60));
        await rig.AddEventsAsync(events);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        (await rig.EventsOfAsync(sig.Id)).Should().HaveCount(20, "not exclusive: the unlabelled ones pass the anchor and stay");
    }

    [Fact]
    public async Task TheUsersLabels_StayInTheSignature_AndSoDoesItsLink_WhenNothingElseIsLeft()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 37, rig.Fridge, count: 8);
        var from = T0.AddDays(-3);
        await rig.AddEventsAsync(new[] { Ev(rig, from, 37, sig.Id, EventLabelSource.User, "Frigider"), Ev(rig, from.AddMinutes(1), -37, sig.Id, EventLabelSource.User, "Frigider") }
            .Concat(Enumerable.Range(2, 6).Select(k => Ev(rig, from.AddMinutes(k), k % 2 == 0 ? 37 : -37, sig.Id))));

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        var after = await rig.SigAsync(sig.Id);
        after.Count.Should().Be(2);
        after.PlugId.Should().Be(rig.Fridge, "it is the plug's signature");
        after.Name.Should().Be("Frigider", "never back to „Necunoscut”");
        (await rig.EventsOfAsync(sig.Id)).Should().OnlyContain(e => e.Source == EventLabelSource.User);
    }

    [Fact]
    public async Task AnEmptiedSignature_StaysThePlugs_AndTheNextConfirmationGivesItItsCentre_WithoutADuplicate()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 39, rig.Fridge, count: 40);
        var from = T0.AddDays(-3);
        await rig.AddEventsAsync(Enumerable.Range(0, 40).Select(k => Ev(rig, from.AddMinutes(k), k % 2 == 0 ? 37 : -37, sig.Id)));
        var first = rig.Service();

        await first.RegroupNowAsync(CancellationToken.None);

        var emptied = await rig.SigAsync(sig.Id);
        emptied.Count.Should().Be(0);
        emptied.PlugId.Should().Be(rig.Fridge);
        emptied.MeanDeltaP.Should().Be(0, "nothing in it: the row does not show the old drifted centre");
        emptied.MeanDeltaQ.Should().Be(0);
        (await first.GetOverviewAsync(0, 0, CancellationToken.None)).PlugMeasured.Should().ContainSingle().Which.Signature.Id.Should().Be(sig.Id);

        // Two fridge cycles of 120 W, confirmed by the plug.
        await rig.HouseAsync(1800, 120, (300, 600), (1200, 300));
        await rig.PlugReadingsAsync(rig.Fridge, 1800, 120, (300, 600), (1200, 300));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        var linked = (await rig.SignaturesAsync()).Where(s => s.PlugId == rig.Fridge).ToList();
        linked.Should().ContainSingle("no duplicate").Which.Id.Should().Be(sig.Id);
        var after = await rig.SigAsync(sig.Id);
        after.Count.Should().Be(4);
        after.MeanDeltaP.Should().BeApproximately(120, 1);
        (await rig.EventsOfAsync(sig.Id)).Should().OnlyContain(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId == rig.Fridge);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AConfirmationBehindADriftedCentre_FindsItsSignature_BeforeAnyCleaning(bool someConfirmedBefore)
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 39, rig.Fridge, count: 30);
        var from = T0.AddDays(-3);
        var stored = Enumerable.Range(0, 30).Select(k => Ev(rig, from.AddMinutes(k), k % 2 == 0 ? 37 : -37, sig.Id)).ToList();
        if (someConfirmedBefore) stored.AddRange(Pair((at, p) => Confirmed(rig, at, p, sig.Id), from.AddHours(2), 120));
        await rig.AddEventsAsync(stored);
        using (var ctx = rig.Db.CreateContext())   // re-clustered recently: this start does not clean
            await new SettingsRepository(ctx).SetAsync(SettingKeys.NilmLastReclusterTs, T0.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        await rig.HouseAsync(1800, 120, (300, 600), (1200, 300));
        await rig.PlugReadingsAsync(rig.Fridge, 1800, 120, (300, 600), (1200, 300));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        (await rig.SignaturesAsync()).Where(s => s.PlugId == rig.Fridge).Should().ContainSingle("the centre of 39 W alone must not make a duplicate").Which.Id.Should().Be(sig.Id);
        (await rig.EventsOfAsync(sig.Id)).Count(e => e.Source == EventLabelSource.AutoPlug && e.TsUtc >= T0).Should().Be(4);
    }

    [Fact]
    public async Task ANewEventThePlugDidNotConfirm_StaysOut_OnThePathOfTheBackfill()
    {
        using var rig = await RigAsync();
        var fridge = await rig.AddSignatureAsync("Frigider", 1430, rig.Fridge, count: 5);
        await rig.HouseAsync(1800, 1430, (120, 180));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        var events = await rig.AllEventsAsync();
        events.Should().HaveCount(2).And.OnlyContain(e => e.SignatureId != null && e.SignatureId != fridge.Id, "the same step, no confirmation");
        (await rig.SignaturesAsync()).Should().HaveCount(2);
        (await rig.RunsOfAsync(fridge.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task ThePlainPlug_TakesTheSameEvent()
    {
        using var rig = await RigAsync(single: false);
        var plain = await rig.AddSignatureAsync("Priza", 1430, rig.Fridge, count: 5);
        await rig.HouseAsync(1800, 1430, (120, 180));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        (await rig.AllEventsAsync()).Should().HaveCount(2).And.OnlyContain(e => e.SignatureId == plain.Id);
    }

    /// <summary>A classifier trained on labels of three powers (ΔQ 0, transient 1, rise 0, like the synthetic house).</summary>
    private static async Task<Rig> TrainedAsync(bool single, (string Name, double Power)[] classes)
    {
        var rig = await RigAsync(ml: true, single: single);
        var events = new List<MeterEvent>();
        var t = T0.AddDays(-10);
        foreach (var (name, power) in classes)
            for (var i = 0; i < 16; i++)
            {
                t = t.AddMinutes(5);
                var p = power * (1 + (i % 5 - 2) * 0.005);
                events.Add(Ev(rig, t, i % 2 == 0 ? p : -p, null, EventLabelSource.AutoPlug, name));
            }
        await rig.AddEventsAsync(events);
        using (var ctx = rig.Db.CreateContext())   // re-clustered recently: only the missing model makes it train
            await new SettingsRepository(ctx).SetAsync(SettingKeys.NilmLastReclusterTs, T0.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        var svc = rig.Service();
        (await svc.TrainNowAsync(CancellationToken.None)).Trained.Should().BeTrue();
        return rig;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AModelPredictionWithTheNameOfTheSignature_DoesNotEnterItWhenExclusive_AndIsTreatedAsNotLabelled(bool single)
    {
        using var rig = await TrainedAsync(single, new[] { ("Frigider", 1430.0), ("Boiler", 3000.0), ("TV", 100.0) });
        var named = await rig.AddSignatureAsync("Frigider", 1430, rig.Fridge, count: 5);
        await rig.HouseAsync(1800, 1430, (120, 180));
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();
        svc.Classifier.Load(rig.ModelPath).Should().BeTrue();
        svc.Classifier.Predict(Ev(rig, T0.AddSeconds(120), 1430, null))!.Value.Label.Should().Be("Frigider", "the premise of this test");

        await svc.RunOnceAsync(CancellationToken.None);

        var fresh = (await rig.AllEventsAsync()).Where(e => e.TsUtc >= T0).ToList();
        fresh.Should().HaveCount(2);
        if (single)
        {
            fresh.Should().OnlyContain(e => e.SignatureId != named.Id && e.Source == EventLabelSource.None && e.Label == null);
        }
        else
        {
            fresh.Should().OnlyContain(e => e.SignatureId == named.Id && e.Source == EventLabelSource.Model && e.Label == "Frigider", "a plain plug: as before");
        }
    }

    [Fact]
    public async Task AModelPredictionOfANameWithoutSignature_KeepsItsLabel_ButDoesNotFallIntoTheExclusiveAnchor()
    {
        // The measured path: „Priza UPS” has no signature, and the prediction used to end in the fridge's through the anchors.
        using var rig = await TrainedAsync(true, new[] { ("Priza UPS", 1430.0), ("Boiler", 3000.0), ("TV", 100.0) });
        var fridge = await rig.AddSignatureAsync("Frigider", 1430, rig.Fridge, count: 5);
        await rig.HouseAsync(1800, 1430, (120, 180));
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();
        svc.Classifier.Load(rig.ModelPath).Should().BeTrue();
        svc.Classifier.Predict(Ev(rig, T0.AddSeconds(120), 1430, null))!.Value.Label.Should().Be("Priza UPS", "the premise of this test");

        await svc.RunOnceAsync(CancellationToken.None);

        var fresh = (await rig.AllEventsAsync()).Where(e => e.TsUtc >= T0).ToList();
        fresh.Should().HaveCount(2).And.OnlyContain(e => e.SignatureId != null && e.SignatureId != fridge.Id && e.Source == EventLabelSource.Model && e.Label == "Priza UPS");
    }

    [Fact]
    public async Task TheAutomaticMerge_LeavesTheExclusiveSignatureAlone_AndUnitesTwoOfThePlugWithConfirmedEvents()
    {
        using var rig = await RigAsync();
        var fridge = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge, count: 8);
        var second = await rig.AddSignatureAsync("Frigider", 62, rig.Fridge, count: 8);
        var named = await rig.AddSignatureAsync("Frigider", 60, null, SignatureLabelSource.User, count: 8);
        var from = T0.AddDays(-3);
        var events = new List<MeterEvent>();
        for (var k = 0; k < 4; k++)
        {
            events.AddRange(Pair((at, p) => Confirmed(rig, at, p, fridge.Id), from.AddMinutes(k * 5), 60));
            events.AddRange(Pair((at, p) => Confirmed(rig, at, p, second.Id), from.AddMinutes(k * 5 + 1), 62));
            events.AddRange(Pair((at, p) => Ev(rig, at, p, named.Id, EventLabelSource.User, "Frigider"), from.AddMinutes(k * 5 + 2), 60));
        }
        await rig.AddEventsAsync(events);

        var result = await rig.Service().RegroupNowAsync(CancellationToken.None);

        result.Merges.Should().ContainSingle().Which.Should().Match<MergeStep>(m => new[] { fridge.Id, second.Id }.Contains(m.SurvivorId) && new[] { fridge.Id, second.Id }.Contains(m.AbsorbedId));
        (await rig.SigAsync(named.Id)).Count.Should().Be(8, "the same name and centre, but not the plug's: rule c refused");
        (await rig.SignaturesAsync()).Single(s => s.PlugId == rig.Fridge).Count.Should().Be(16);
    }

    [Fact]
    public async Task AMergeByTheUser_IsPossible_AndTheNextRegroupingDoesNotUndoIt()
    {
        using var rig = await RigAsync();
        var fridge = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge, count: 4);
        var anon = await rig.AddSignatureAsync("", 37, null, SignatureLabelSource.Cluster, count: 4);
        var from = T0.AddDays(-3);
        var events = new List<MeterEvent>();
        events.AddRange(Pair((at, p) => Confirmed(rig, at, p, fridge.Id), from, 60));
        events.AddRange(Pair((at, p) => Confirmed(rig, at, p, fridge.Id), from.AddMinutes(1), 60));
        events.AddRange(Pair((at, p) => Ev(rig, at, p, fridge.Id), from.AddMinutes(2), 37));   // caught before 0.13.1: not part of what the user merges
        events.AddRange(Pair((at, p) => Ev(rig, at, p, anon.Id), from.AddMinutes(3), 37));
        events.AddRange(Pair((at, p) => Ev(rig, at, p, anon.Id), from.AddMinutes(4), 37));
        await rig.AddEventsAsync(events);
        var svc = rig.Service();
        await svc.RegroupNowAsync(CancellationToken.None);
        var after = await rig.EventsOfAsync(fridge.Id);
        after.Should().HaveCount(4, "the two caught ones left");

        (await svc.MergeWithAsync(fridge.Id, anon.Id, CancellationToken.None)).Should().Be(fridge.Id);
        var merged = await rig.EventsOfAsync(fridge.Id);
        merged.Should().HaveCountGreaterThan(4);
        var labelled = merged.Where(e => e.Source == EventLabelSource.User).ToList();
        labelled.Should().NotBeEmpty().And.OnlyContain(e => e.Label == "Frigider" && e.LabelPlugId == null);
        merged.Count(e => e.Source == EventLabelSource.AutoPlug).Should().Be(4, "what the plug confirmed keeps its source");

        await svc.NightlyAsync(rig.Now, CancellationToken.None);

        (await rig.EventsOfAsync(fridge.Id)).Should().HaveCount(merged.Count, "the user's decision stands");
    }

    [Fact]
    public async Task AMergeByTheUser_DoesNotConfirmWhatTheSignatureCaughtBefore()
    {
        using var rig = await RigAsync();
        var fridge = await rig.AddSignatureAsync("Frigider", 39, rig.Fridge, count: 8);
        var anon = await rig.AddSignatureAsync("", 60, null, SignatureLabelSource.Cluster, count: 2);
        var from = T0.AddDays(-3);
        var events = new List<MeterEvent>();
        for (var k = 0; k < 4; k++) events.AddRange(Pair((at, p) => Ev(rig, at, p, fridge.Id), from.AddMinutes(k), 37));
        events.AddRange(Pair((at, p) => Ev(rig, at, p, anon.Id), from.AddMinutes(10), 60));
        await rig.AddEventsAsync(events);
        var svc = rig.Service();

        (await svc.MergeWithAsync(fridge.Id, anon.Id, CancellationToken.None)).Should().Be(fridge.Id);
        await svc.NightlyAsync(rig.Now, CancellationToken.None);

        var left = await rig.EventsOfAsync(fridge.Id);
        left.Should().HaveCount(2).And.OnlyContain(e => e.Source == EventLabelSource.User && Math.Abs(Math.Abs(e.DeltaP) - 60) < 1,
            "only the side the user brought in; the 8 caught before are not confirmed by this merge");
    }

    [Fact]
    public async Task AMergeByTheUserIntoAnAnonymousSignature_LabelsWithTheAppliance_NotWithTheUnknownName()
    {
        using var rig = await RigAsync();
        var fridge = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge, count: 2);
        var anon = await rig.AddSignatureAsync("", 37, null, SignatureLabelSource.Cluster, count: 2);
        var from = T0.AddDays(-3);
        await rig.AddEventsAsync(Pair((at, p) => Confirmed(rig, at, p, fridge.Id), from, 60).Concat(Pair((at, p) => Ev(rig, at, p, anon.Id), from.AddMinutes(1), 37)));

        (await rig.Service().MergeAsync(fridge.Id, anon.Id, CancellationToken.None)).Should().BeTrue("the selector above the table lets the user choose the survivor");

        var events = await rig.EventsOfAsync(anon.Id);
        events.Should().HaveCount(4);
        events.Where(e => e.Source == EventLabelSource.User).Should().HaveCount(2).And.OnlyContain(e => e.Label == "Frigider", "never a class „Necunoscut #n” of the classifier");
        (await rig.SigAsync(anon.Id)).PlugId.Should().Be(rig.Fridge);
    }

    [Fact]
    public async Task EventsMovedByADurationThePlugDidNotConfirm_LeaveAtTheRegrouping_TheConfirmedOnesStay()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge, count: 4);
        var from = T0.AddDays(-3);
        await rig.AddEventsAsync(Pair((at, p) => Ev(rig, at, p, sig.Id, EventLabelSource.DurationRule, "Frigider"), from, 37)
            .Concat(Pair((at, p) => Ev(rig, at, p, sig.Id, EventLabelSource.DurationRule, "Frigider", rig.Fridge), from.AddMinutes(1), 60)));

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        var left = await rig.EventsOfAsync(sig.Id);
        left.Should().HaveCount(2).And.OnlyContain(e => e.LabelPlugId == rig.Fridge && Math.Abs(Math.Abs(e.DeltaP) - 60) < 1);
    }

    [Fact]
    public async Task AHiddenSignatureOfThePlug_TakesNothingUnconfirmed_OnTheLivePath()
    {
        using var rig = await RigAsync();
        var hidden = await rig.AddSignatureAsync("Frigider", 1430, rig.Fridge, count: 5, hidden: true);
        await rig.HouseAsync(1800, 1430, (120, 180));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        (await rig.AllEventsAsync()).Should().HaveCount(2).And.OnlyContain(e => e.SignatureId != null && e.SignatureId != hidden.Id);
    }

    [Fact]
    public async Task AHiddenSignatureOfThePlug_StillTakesItsConfirmations_WithoutADuplicate()
    {
        using var rig = await RigAsync();
        var hidden = await rig.AddSignatureAsync("Frigider", 120, rig.Fridge, count: 5, hidden: true);
        await rig.HouseAsync(1800, 120, (300, 600), (1200, 300));
        await rig.PlugReadingsAsync(rig.Fridge, 1800, 120, (300, 600), (1200, 300));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        (await rig.SignaturesAsync()).Should().ContainSingle().Which.Id.Should().Be(hidden.Id);
        (await rig.EventsOfAsync(hidden.Id)).Should().HaveCount(4).And.OnlyContain(e => e.Source == EventLabelSource.AutoPlug);
    }

    [Fact]
    public async Task APlugExcludedFromLearning_WithASingleAppliance_LabelsAndLinks_AndStaysOutOfTheTraining()
    {
        // Found in production: SingleAppliance = true, NilmState = IgnoredManual, so the fridge's plug never labelled anything.
        using var rig = await RigAsync();
        await rig.SetStateAsync(rig.Fridge, PlugNilmState.IgnoredManual);
        await rig.HouseAsync(1800, 120, (300, 600), (1200, 300));
        await rig.PlugReadingsAsync(rig.Fridge, 1800, 120, (300, 600), (1200, 300));
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();

        await svc.RunOnceAsync(CancellationToken.None);

        var events = await rig.AllEventsAsync();
        events.Should().HaveCount(4).And.OnlyContain(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId == rig.Fridge && e.Label == "Frigider");
        var sig = (await rig.SignaturesAsync()).Should().ContainSingle().Subject;
        sig.PlugId.Should().Be(rig.Fridge);
        sig.Name.Should().Be("Frigider");
        sig.Category.Should().Be("fridge");
        events.Should().OnlyContain(e => e.SignatureId == sig.Id);

        // The state in the data stays what it was, and the page and the API say what the plug does.
        var row = (await svc.ListPlugsAsync(CancellationToken.None)).Single(p => p.PlugId == rig.Fridge);
        row.State.Should().Be(PlugNilmState.IgnoredManual);
        row.LabelsEvents.Should().BeTrue();

        // Its labels count for the merge and the link; the training still leaves them out.
        await svc.NightlyAsync(rig.Now, CancellationToken.None);
        (await rig.EventsOfAsync(sig.Id)).Should().HaveCount(4);
        (await rig.SigAsync(sig.Id)).PlugId.Should().Be(rig.Fridge);
        var plugs = await new PlugRepository(rig.Db.CreateContext()).ListAllAsync(CancellationToken.None);
        NilmService.TrainingExcluded(plugs).Should().Contain(rig.Fridge);
        (await rig.Repo(r => r.ListLabeledEventsAsync(NilmService.TrainingExcluded(plugs), 100, CancellationToken.None))).Should().BeEmpty();
    }

    [Theory]
    [InlineData(PlugNilmState.IgnoredManual)]
    [InlineData(PlugNilmState.IgnoredAuto)]
    public async Task APlugExcludedFromLearning_WithoutASingleAppliance_StillDoesNotLabel(PlugNilmState state)
    {
        using var rig = await RigAsync(single: false);
        await rig.SetStateAsync(rig.Fridge, state);
        await rig.HouseAsync(1800, 120, (300, 600), (1200, 300));
        await rig.PlugReadingsAsync(rig.Fridge, 1800, 120, (300, 600), (1200, 300));
        rig.Now = T0.AddMinutes(31);
        var svc = rig.Service();

        await svc.RunOnceAsync(CancellationToken.None);

        (await rig.AllEventsAsync()).Should().HaveCount(4).And.OnlyContain(e => e.Source == EventLabelSource.None && e.LabelPlugId == null);
        (await svc.ListPlugsAsync(CancellationToken.None)).Single(p => p.PlugId == rig.Fridge).LabelsEvents.Should().BeFalse();
    }

    [Fact]
    public async Task EventsOlderThanTheWindow_LeaveToo_TheRunsBeforeThePlugsDataStay()
    {
        using var rig = await RigAsync();
        var fridge = await rig.AddSignatureAsync("Frigider", 39, rig.Fridge, count: 4);
        var old = T0.AddDays(-40);
        var events = new List<MeterEvent>();
        events.AddRange(Pair((at, p) => Ev(rig, at, p, fridge.Id), old, 37));
        events.AddRange(Pair((at, p) => Confirmed(rig, at, p, fridge.Id), old.AddMinutes(5), 60));
        events.AddRange(Pair((at, p) => Confirmed(rig, at, p, fridge.Id), T0.AddDays(-3), 60));
        var stored = await rig.AddEventsAsync(events);
        var oldOn = stored.Single(e => e.DeltaP == 37);
        var oldOff = stored.Single(e => e.DeltaP == -37);
        var oldConfirmedOn = stored.First(e => e.DeltaP == 60);
        var oldConfirmedOff = stored.First(e => e.DeltaP == -60);
        await rig.Repo(r => r.SaveRunsAsync(new[]
        {
            new ApplianceRun(0, fridge.Id, oldOn.TsUtc, oldOff.TsUtc, 37, 0.2, oldOn.Id, oldOff.Id, 300),
            new ApplianceRun(0, fridge.Id, oldConfirmedOn.TsUtc, oldConfirmedOff.TsUtc, 60, 0.3, oldConfirmedOn.Id, oldConfirmedOff.Id, 300),
        }, CancellationToken.None));

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        (await rig.Repo(r => r.GetEventAsync(oldOn.Id, CancellationToken.None)))!.SignatureId.Should().BeNull("out of the job's reach: free, without a signature");
        (await rig.Repo(r => r.GetEventAsync(oldOff.Id, CancellationToken.None)))!.SignatureId.Should().BeNull();
        var runs = await rig.RunsOfAsync(fridge.Id);
        runs.Should().HaveCount(2, "since 0.13.2 a rebuild deletes nothing older than the plug's first data: nothing could rebuild it");
        (await rig.EventsOfAsync(fridge.Id)).Should().HaveCount(4);
    }

    [Fact]
    public async Task ThePulseOfASplit_DoesNotWaitInAnExclusiveSignature()
    {
        using var rig = await RigAsync();
        var s = await rig.AddSignatureAsync("Frigider", 1430, rig.Fridge, count: 5, min: 45);
        var t = await rig.AddSignatureAsync("Espressor", 1430, null, SignatureLabelSource.User, count: 5, max: 40, splitFrom: s.Id);
        await rig.HouseAsync(1800, 1430, (120, 15));
        rig.Now = T0.AddMinutes(31);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        (await rig.AllEventsAsync()).Should().HaveCount(2).And.OnlyContain(e => e.SignatureId == t.Id && e.Source == EventLabelSource.None,
            "it stays where the anchors put it, without passing through S and the duration rule");
        (await rig.RunsOfAsync(t.Id)).Should().ContainSingle().Which.EndReason.Should().Be(RunEndReason.Paired);
    }

    [Fact]
    public async Task TheDurationRule_DoesNotBringAnUnconfirmedRunIntoAnExclusiveSide()
    {
        using var rig = await RigAsync();
        var s = await rig.AddSignatureAsync("", 1430, null, SignatureLabelSource.Cluster, count: 5, min: 45);
        var t = await rig.AddSignatureAsync("Espressor", 1430, rig.Fridge, count: 5, max: 40);
        var other = await rig.AddSignatureAsync("Fără priză", 1430, null, SignatureLabelSource.User, count: 5, min: 45);
        var from = T0.AddDays(-3);
        var events = new[] { Ev(rig, from, 1430, s.Id), Ev(rig, from.AddSeconds(15), -1430, s.Id), Ev(rig, from.AddHours(1), 1430, t.Id), Ev(rig, from.AddHours(1).AddSeconds(15), -1430, t.Id) };
        await rig.AddEventsAsync(events);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        (await rig.EventsOfAsync(t.Id)).Should().BeEmpty("neither the anchors nor the duration of the run bring it there");
        (await rig.SigAsync(t.Id)).PlugId.Should().Be(rig.Fridge);
        (await rig.AllEventsAsync()).Should().OnlyContain(e => e.Source != EventLabelSource.DurationRule);
        (await rig.EventsOfAsync(other.Id)).Should().HaveCount(4, "a plain anchor within its radius");
    }

    [Fact]
    public async Task ASplitByDurationOfAnExclusiveSignature_HasNothingToMove_ItsRunsAreThePlugs()
    {
        using var rig = await RigAsync();
        var s = await rig.AddSignatureAsync("Frigider", 1430, rig.Fridge, count: 8);
        var from = T0.AddDays(-3);
        var events = new List<MeterEvent>();
        for (var k = 0; k < 2; k++)
        {
            events.Add(Confirmed(rig, from.AddHours(k), 1430, s.Id));
            events.Add(Confirmed(rig, from.AddHours(k).AddSeconds(15), -1430, s.Id));
            events.Add(Confirmed(rig, from.AddHours(k).AddMinutes(10), 1430, s.Id));
            events.Add(Confirmed(rig, from.AddHours(k).AddMinutes(13), -1430, s.Id));
        }
        await rig.AddEventsAsync(events);
        var svc = rig.Service();
        await svc.RegroupNowAsync(CancellationToken.None);

        var split = await svc.SplitByDurationAsync(s.Id, 40, "Espressor", null, dryRun: false, CancellationToken.None);

        split!.Applied.Should().BeFalse("since 0.13.2 no run of it stands on events");
        split.Runs.Should().Be(0);
        (await rig.EventsOfAsync(s.Id)).Should().HaveCount(8, "the confirmed events stay");
        (await rig.SignaturesAsync()).Should().ContainSingle();
    }
}
