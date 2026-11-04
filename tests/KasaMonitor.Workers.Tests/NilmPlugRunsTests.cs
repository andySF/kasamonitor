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
/// 0.13.2, through the service: the runs of a plug with a single appliance come from the plug itself (decisions 0.13.2).
/// Measured on 68 h of a fridge: 240 real transitions (84.5 a day), 75 of them with one candidate among the main meter's
/// events, about 2,900 events a day.
/// </summary>
public class NilmPlugRunsTests
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

        public Rig()
        {
            Scopes = new TestScopeFactory(Db.CreateContext);
            Settings = TestSettings.Create(Scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", MlEnabled: false));
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

        public Task<ApplianceSignature> AddSignatureAsync(string name, double p, Guid? plug, SignatureLabelSource source = SignatureLabelSource.Plug, int count = 0) =>
            Repo(r => r.AddSignatureAsync(new ApplianceSignature(0, name, "bi-box", "other", p, 0, 0, count, T0.AddDays(-5), source, plug, false), CancellationToken.None));

        public Task<IReadOnlyList<MeterEvent>> AddEventsAsync(IEnumerable<MeterEvent> events) => Repo(r => r.AddEventsAsync(events.ToList(), CancellationToken.None));

        public Task<IReadOnlyList<MeterEvent>> AllEventsAsync() => Repo(r => r.ListEventsAsync(DateTimeOffset.UnixEpoch, Now.AddDays(1), CancellationToken.None));

        public Task<IReadOnlyList<ApplianceSignature>> SignaturesAsync() => Repo(r => r.ListSignaturesAsync(CancellationToken.None));

        public async Task<MeterEvent> EventAsync(long id) => (await Repo(r => r.GetEventAsync(id, CancellationToken.None)))!;

        public async Task<ApplianceSignature> SigAsync(long id) => (await Repo(r => r.GetSignatureAsync(id, CancellationToken.None)))!;

        public Task<IReadOnlyList<ApplianceRun>> RunsOfAsync(long id) => Repo(r => r.ListRunsBySignatureAsync(id, DateTimeOffset.UnixEpoch, CancellationToken.None));

        public Task<IReadOnlyList<ApplianceRun>> AllRunsAsync() => Repo(r => r.ListRunsAsync(DateTimeOffset.UnixEpoch, Now.AddDays(1), CancellationToken.None));

        /// <summary>
        /// The plug's stream as it is stored: a row on a change and every 60 s, from T0 + <paramref name="fromSecond"/> to
        /// <paramref name="toSecond"/>: 2 W, and <paramref name="power"/> W inside each (start, seconds).
        /// </summary>
        public async Task PlugStreamAsync(Guid plug, int fromSecond, int toSecond, double power, params (int Start, int Seconds)[] on)
        {
            double W(int t) => on.Any(x => t >= x.Start && t < x.Start + x.Seconds) ? power : 2;
            var rows = new List<LiveSample>();
            var last = fromSecond - 1000;
            for (var t = fromSecond; t <= toSecond; t++)
            {
                if (t - last < 60 && W(t) == W(t - 1)) continue;
                rows.Add(new LiveSample(plug, T0.AddSeconds(t), W(t)));
                last = t;
            }
            using var ctx = Db.CreateContext();
            await new LiveRepository(ctx).SaveBatchAsync(rows, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        }

        public void Dispose()
        {
            Db.Dispose();
            if (File.Exists(ModelPath)) File.Delete(ModelPath);
        }
    }

    /// <summary>A main meter and the plug „Frigider” with a single appliance.</summary>
    private static async Task<Rig> RigAsync(bool single = true)
    {
        var rig = new Rig();
        rig.Main = await rig.AddPlugAsync("aabbccddee00", "Casa", PlugRole.MainMeter, PlugProtocols.ShellyRpc);
        rig.Fridge = await rig.AddPlugAsync("aabbccddee01", "Priza frigider", PlugRole.Plug, PlugProtocols.Legacy);
        using var ctx = rig.Db.CreateContext();
        (await new PlugRepository(ctx).SetApplianceAsync(rig.Fridge, single, "Frigider", "fridge", CancellationToken.None)).Should().BeTrue();
        return rig;
    }

    private static MeterEvent Ev(Rig rig, int second, double p, long? sig = null, EventLabelSource source = EventLabelSource.None, string? label = null, Guid? plug = null) =>
        new(0, rig.Main, T0.AddSeconds(second), p, 0, 0, 1, 0, p > 0 ? 300 : 300 - p, sig, label, source, plug);

    private static MeterEvent Confirmed(Rig rig, int second, double p, long sig) => Ev(rig, second, p, sig, EventLabelSource.AutoPlug, "Frigider", rig.Fridge);

    private static (int, int)[] Cycles(int first, int count, int on = 300, int period = 600) => Enumerable.Range(0, count).Select(k => (first + k * period, on)).ToArray();

    [Fact]
    public async Task WithoutAnyEvent_TheRunsComeFromThePlug_OnASignatureMadeLikeThatOfAConfirmation()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 2400, 60, Cycles(300, 3));
        rig.Now = T0.AddSeconds(2400);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        var sig = (await rig.SignaturesAsync()).Should().ContainSingle().Subject;
        sig.PlugId.Should().Be(rig.Fridge);
        sig.Name.Should().Be("Frigider");
        sig.Category.Should().Be("fridge");
        sig.LabelSource.Should().Be(SignatureLabelSource.Plug);
        sig.Count.Should().Be(0, "no event entered it");
        var runs = await rig.RunsOfAsync(sig.Id);
        runs.Should().HaveCount(3).And.OnlyContain(r => r.FromPlug && r.EndEventId == null && r.EndReason == RunEndReason.Paired);
        runs[0].StartUtc.Should().Be(T0.AddSeconds(300));
        runs[0].EndUtc.Should().Be(T0.AddSeconds(600));
        runs[0].PowerW.Should().Be(60);
        runs[0].EstimatedWh.Should().BeApproximately(5, 1e-6);
        (await rig.AllEventsAsync()).Should().BeEmpty("no false event in MeterEvents");
    }

    [Fact]
    public async Task ConfirmedEvents_DoNotDoubleTheRuns_AndStayWhatTheCentreIsMadeOf()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge);
        await rig.PlugStreamAsync(rig.Fridge, 0, 2400, 60, Cycles(300, 3));
        await rig.AddEventsAsync(new[] { Confirmed(rig, 298, 58, sig.Id), Confirmed(rig, 599, -58, sig.Id), Confirmed(rig, 900, 62, sig.Id), Confirmed(rig, 1199, -62, sig.Id) });
        rig.Now = T0.AddSeconds(2400);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        var runs = await rig.RunsOfAsync(sig.Id);
        runs.Should().HaveCount(3).And.OnlyContain(r => r.FromPlug);
        var after = await rig.SigAsync(sig.Id);
        after.Count.Should().Be(4);
        after.MeanDeltaP.Should().BeApproximately(60, 0.5);
    }

    [Fact]
    public async Task ConfirmedEventsWithoutPlugData_FormNoRun()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge);
        await rig.AddEventsAsync(new[] { Confirmed(rig, 100, 60, sig.Id), Confirmed(rig, 400, -60, sig.Id) });
        rig.Now = T0.AddSeconds(600);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        (await rig.RunsOfAsync(sig.Id)).Should().BeEmpty("the plug is the only source of these runs");
        (await rig.SigAsync(sig.Id)).Count.Should().Be(2);
    }

    [Fact]
    public async Task ARebuild_IsIdempotent_RegroupingTwice_ARestart_AndTheNextPasses()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 2400, 60, Cycles(300, 3));
        rig.Now = T0.AddSeconds(2400);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);

        static IEnumerable<(long, DateTimeOffset, DateTimeOffset?, double, double?)> Key(IEnumerable<ApplianceRun> runs) =>
            runs.Select(r => (r.SignatureId, r.StartUtc, r.EndUtc, r.PowerW, r.EstimatedWh)).OrderBy(x => x.StartUtc);
        var first = Key(await rig.AllRunsAsync()).ToList();
        first.Should().HaveCount(3);

        await svc.RegroupNowAsync(CancellationToken.None);
        Key(await rig.AllRunsAsync()).Should().Equal(first);
        await svc.RegroupNowAsync(CancellationToken.None);
        Key(await rig.AllRunsAsync()).Should().Equal(first);

        var restarted = rig.Service();
        await restarted.RunOnceAsync(CancellationToken.None);
        rig.Now = rig.Now.AddSeconds(30);
        await restarted.RunOnceAsync(CancellationToken.None);
        Key(await rig.AllRunsAsync()).Should().Equal(first);
        (await rig.SignaturesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ARestart_WithARunStillOpen_KeepsOneRun_WithTheSameStart_AndItsWholeEnergy()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 900, 60, (300, 1200));
        rig.Now = T0.AddSeconds(900);
        await rig.Service().RunOnceAsync(CancellationToken.None);
        (await rig.AllRunsAsync()).Should().ContainSingle().Which.IsOpen.Should().BeTrue();

        var restarted = rig.Service();
        await restarted.RunOnceAsync(CancellationToken.None);
        rig.Now = rig.Now.AddSeconds(30);
        await restarted.RunOnceAsync(CancellationToken.None);

        var open = (await rig.AllRunsAsync()).Should().ContainSingle("neither a duplicate nor a lost run").Subject;
        open.IsOpen.Should().BeTrue();
        open.StartUtc.Should().Be(T0.AddSeconds(300));
        (await restarted.GetOverviewAsync(0, 0, CancellationToken.None)).PlugMeasured.Should().ContainSingle().Which.ActiveW.Should().Be(60);

        await rig.PlugStreamAsync(rig.Fridge, 901, 1800, 60, (300, 1200));
        rig.Now = T0.AddSeconds(1800);
        await restarted.RunOnceAsync(CancellationToken.None);

        var closed = (await rig.AllRunsAsync()).Should().ContainSingle().Subject;
        closed.Id.Should().Be(open.Id, "after the restart it goes on on its own row");
        closed.StartUtc.Should().Be(T0.AddSeconds(300));
        closed.EndUtc.Should().Be(T0.AddSeconds(1500));
        closed.EstimatedWh.Should().BeApproximately(1200 * 60 / 3600.0, 1e-6, "the seconds before the restart count too");
    }

    [Fact]
    public async Task ARestart_AfterTheRunEndedWhileItWasDown_ClosesThatRun()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 900, 60, (300, 1200));
        rig.Now = T0.AddSeconds(900);
        await rig.Service().RunOnceAsync(CancellationToken.None);

        await rig.PlugStreamAsync(rig.Fridge, 901, 1800, 60, (300, 1200));
        rig.Now = T0.AddSeconds(1800);
        await rig.Service().RunOnceAsync(CancellationToken.None);

        var run = (await rig.AllRunsAsync()).Should().ContainSingle().Subject;
        run.StartUtc.Should().Be(T0.AddSeconds(300));
        run.EndUtc.Should().Be(T0.AddSeconds(1500));
        run.EndReason.Should().Be(RunEndReason.Paired);
        run.EstimatedWh.Should().BeApproximately(1200 * 60 / 3600.0, 1e-6);
    }

    [Fact]
    public async Task LiveAndRebuilt_GiveTheSameRuns_AndAnOpenRunClosesOnItsOwnRow()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 400, 60, (300, 300));
        rig.Now = T0.AddSeconds(400);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);

        var open = (await rig.AllRunsAsync()).Should().ContainSingle().Subject;
        open.IsOpen.Should().BeTrue("the switch-on is known, there is no switch-off yet");
        open.PowerW.Should().Be(60);

        await rig.PlugStreamAsync(rig.Fridge, 401, 900, 60, (300, 300));
        rig.Now = T0.AddSeconds(900);
        await svc.RunOnceAsync(CancellationToken.None);

        var closed = (await rig.AllRunsAsync()).Should().ContainSingle().Subject;
        closed.Id.Should().Be(open.Id, "the same row closes");
        closed.EndUtc.Should().Be(T0.AddSeconds(600));
        closed.EstimatedWh.Should().BeApproximately(5, 1e-6);
    }

    [Fact]
    public async Task AGapInThePlugData_ClosesTheRunAsUncertain_WithTheEnergyMeasured_AndItsDurationDoesNotCount()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 500, 60, (300, 10_000));
        await rig.PlugStreamAsync(rig.Fridge, 1500, 2000, 60);
        rig.Now = T0.AddSeconds(2000);

        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);

        var run = (await rig.AllRunsAsync()).Should().ContainSingle().Subject;
        run.EndReason.Should().Be(RunEndReason.Timeout);
        run.EndUtc.Should().BeOnOrBefore(T0.AddSeconds(500 + 70));
        run.EstimatedWh.Should().BeGreaterThan(3).And.BeLessThan(5);
        (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).PlugMeasured.Should().ContainSingle().Which.UncertainRuns.Should().Be(1);
    }

    [Fact]
    public async Task AHeartbeatLate_By9S_IsNoGap_AsMeasured()
    {
        // Rows of the stream come 60 s after the last one or at the next sample: up to 69 s measured on a fridge.
        using var rig = await RigAsync();
        var rows = new List<LiveSample> { new(rig.Fridge, T0, 2), new(rig.Fridge, T0.AddSeconds(50), 2), new(rig.Fridge, T0.AddSeconds(100), 60) };
        for (var t = 169; t < 800; t += 69) rows.Add(new LiveSample(rig.Fridge, T0.AddSeconds(t), 60));
        rows.Add(new LiveSample(rig.Fridge, T0.AddSeconds(800), 2));
        rows.Add(new LiveSample(rig.Fridge, T0.AddSeconds(860), 2));
        using (var ctx = rig.Db.CreateContext()) await new LiveRepository(ctx).SaveBatchAsync(rows, Array.Empty<LiveMinuteAggregate>(), CancellationToken.None);
        rig.Now = T0.AddSeconds(950);

        await rig.Service().RunOnceAsync(CancellationToken.None);

        var run = (await rig.AllRunsAsync()).Should().ContainSingle().Subject;
        run.EndReason.Should().Be(RunEndReason.Paired);
        run.EndUtc.Should().Be(T0.AddSeconds(800));
    }

    [Fact]
    public async Task AnEmptiedExclusiveSignature_GetsTheRuns_WithoutADuplicate()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 39, rig.Fridge, count: 2);
        await rig.AddEventsAsync(new[] { Ev(rig, 100, 37, sig.Id), Ev(rig, 130, -37, sig.Id) });
        await rig.PlugStreamAsync(rig.Fridge, 0, 2400, 60, Cycles(300, 3));
        rig.Now = T0.AddSeconds(2400);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        (await rig.SigAsync(sig.Id)).Count.Should().Be(0, "what the plug did not confirm left");
        (await rig.RunsOfAsync(sig.Id)).Should().HaveCount(3);
        (await rig.SignaturesAsync()).Where(s => s.PlugId == rig.Fridge).Should().ContainSingle();
    }

    [Fact]
    public async Task SeveralSignaturesOfThePlug_TheRunsLiveOnTheOneWithMostConfirmations_AndAMergeDoublesNothing()
    {
        using var rig = await RigAsync();
        var few = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge);
        var many = await rig.AddSignatureAsync("Frigider compresor", 150, rig.Fridge);
        var stale = await rig.Repo(r => r.SaveRunsAsync(new[] { new ApplianceRun(0, few.Id, T0.AddSeconds(50), T0.AddSeconds(90), 60, 1, 777, 778) }, CancellationToken.None));
        await rig.AddEventsAsync(new[] { Confirmed(rig, 298, 60, few.Id), Confirmed(rig, 599, -150, many.Id), Confirmed(rig, 900, 150, many.Id) });
        await rig.PlugStreamAsync(rig.Fridge, 0, 2400, 60, Cycles(300, 3));
        rig.Now = T0.AddSeconds(2400);
        var svc = rig.Service();

        await svc.RegroupNowAsync(CancellationToken.None);

        (await rig.RunsOfAsync(many.Id)).Should().HaveCount(3).And.OnlyContain(r => r.FromPlug);
        (await rig.RunsOfAsync(few.Id)).Should().BeEmpty("all on one; the old run from events is gone");

        (await svc.MergeWithAsync(few.Id, many.Id, CancellationToken.None)).Should().NotBeNull();

        var left = (await rig.SignaturesAsync()).Should().ContainSingle().Subject;
        (await rig.RunsOfAsync(left.Id)).Should().HaveCount(3).And.OnlyContain(r => r.FromPlug);
    }

    [Fact]
    public async Task ARebuild_KeepsTheRunsOlderThanThePlugsFirstData()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge);
        var on = Confirmed(rig, -864_000, 60, sig.Id);
        var off = Confirmed(rig, -863_700, -60, sig.Id);
        var stored = await rig.AddEventsAsync(new[] { on, off });
        await rig.Repo(r => r.SaveRunsAsync(new[] { new ApplianceRun(0, sig.Id, on.TsUtc, off.TsUtc, 60, 5, stored[0].Id, stored[1].Id, 300) }, CancellationToken.None));
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, Cycles(300, 1));
        rig.Now = T0.AddSeconds(1200);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        var runs = await rig.RunsOfAsync(sig.Id);
        runs.Should().HaveCount(2);
        runs[0].FromPlug.Should().BeFalse("before the first data of the plug nothing can rebuild it");
        runs[1].FromPlug.Should().BeTrue();
    }

    [Fact]
    public async Task TheRunsOfThePlug_StayOutOfTheDetected_TheBalanceAndTheMetrics()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 2400, 60, (300, 300), (900, 300), (1500, 100_000));
        rig.Now = T0.AddSeconds(2400);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        var sig = (await rig.SignaturesAsync()).Should().ContainSingle().Subject;
        (await rig.AllRunsAsync()).Should().HaveCount(3).And.ContainSingle(r => r.IsOpen);

        await svc.RegroupNowAsync(CancellationToken.None);   // refreshes the summary

        var (detected, runs, _) = await svc.GetDetectedRunsAsync(T0, rig.Now, CancellationToken.None);
        detected.Should().NotContainKey(sig.Id);
        runs.Should().BeEmpty();
        svc.Summary.Active.Should().BeEmpty("„Acum pornite” and kasa_appliance_power_w");
        svc.Summary.Today.Should().BeEmpty();
        svc.Metrics.Appliances.Should().BeEmpty();
        var row = (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).PlugMeasured.Should().ContainSingle().Subject;
        row.ActiveW.Should().Be(60, "its own row may say that it runs");
        row.TodayWh.Should().BeGreaterThan(10);
    }

    // ------------------------------------------------------------------ shadows

    [Fact]
    public async Task AShadow_LeavesItsSignature_ItsRunGoes_TheDetectedFalls_AndThePlugsEnergyStays()
    {
        using var rig = await RigAsync();
        var unknown = await rig.AddSignatureAsync("Necunoscut", 60, null, SignatureLabelSource.Cluster, count: 2);
        var stored = await rig.AddEventsAsync(new[] { Ev(rig, 298, 58, unknown.Id), Ev(rig, 599, -56, unknown.Id) });
        await rig.Repo(r => r.SaveRunsAsync(new[] { new ApplianceRun(0, unknown.Id, stored[0].TsUtc, stored[1].TsUtc, 58, 4.8, stored[0].Id, stored[1].Id, 300) }, CancellationToken.None));
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, (300, 300));
        rig.Now = T0.AddSeconds(1200);
        var svc = rig.Service();
        (await svc.GetDetectedRunsAsync(T0, rig.Now, CancellationToken.None)).Runs.Should().ContainSingle("before: the plug's energy subtracted a second time");

        await svc.RegroupNowAsync(CancellationToken.None);

        foreach (var e in stored)
        {
            var shadow = await rig.EventAsync(e.Id);
            shadow.Source.Should().Be(EventLabelSource.PlugShadow);
            shadow.SignatureId.Should().BeNull();
            shadow.Label.Should().BeNull();
            shadow.LabelPlugId.Should().Be(rig.Fridge);
        }
        (await svc.GetDetectedRunsAsync(T0, rig.Now, CancellationToken.None)).Runs.Should().BeEmpty();
        svc.Summary.Today.Should().BeEmpty();
        var fridge = (await rig.SignaturesAsync()).Should().ContainSingle(s => s.PlugId == rig.Fridge).Subject;
        fridge.Count.Should().Be(0, "a shadow confirms nothing");
        var run = (await rig.RunsOfAsync(fridge.Id)).Should().ContainSingle().Subject;
        run.EstimatedWh.Should().BeApproximately(5, 1e-6);
    }

    [Fact]
    public async Task Live_AnEventAlreadyInASignature_IsCorrectedAfterTheTransitionIsKnown_WithItsRun()
    {
        using var rig = await RigAsync();
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);

        // The house steps by +60 W at T0 + 298, the plug's stream (2 s later) is written as usual.
        for (var t = 0; t < 400; t++)
            rig.Live.Publish(new LiveSample(rig.Main, T0.AddSeconds(t), t < 298 ? 300 : 360));
        await rig.PlugStreamAsync(rig.Fridge, 0, 400, 60, (300, 100_000));
        rig.Now = T0.AddSeconds(345);
        await svc.RunOnceAsync(CancellationToken.None);

        var e = (await rig.AllEventsAsync()).Should().ContainSingle().Subject;
        e.Source.Should().Be(EventLabelSource.None, "the plug's stream is not in memory, and the transition is not known yet: T0 + 300 + 20 + the lag");
        e.SignatureId.Should().NotBeNull();
        var anonymous = e.SignatureId!.Value;
        var open = (await rig.RunsOfAsync(anonymous)).Should().ContainSingle().Subject;
        open.IsOpen.Should().BeTrue();

        await rig.PlugStreamAsync(rig.Fridge, 401, 500, 60, (300, 100_000));
        rig.Now = T0.AddSeconds(400);
        await svc.RunOnceAsync(CancellationToken.None);

        var shadow = await rig.EventAsync(e.Id);
        shadow.Source.Should().Be(EventLabelSource.PlugShadow);
        shadow.SignatureId.Should().BeNull();
        shadow.LabelPlugId.Should().Be(rig.Fridge);
        (await rig.RunsOfAsync(anonymous)).Should().BeEmpty("the run of the shadow is undone");
        (await rig.SigAsync(anonymous)).Count.Should().Be(0);
        var fridge = (await rig.SignaturesAsync()).Should().ContainSingle(s => s.PlugId == rig.Fridge).Subject;
        (await rig.RunsOfAsync(fridge.Id)).Should().ContainSingle(r => r.IsOpen && r.FromPlug);
    }

    [Theory]
    [InlineData(true)]    // cleared on the page: the service knows at once
    [InlineData(false)]   // the service learns it when it reads the plugs again
    public async Task ATransitionWaitingForItsCheck_MarksNothing_AfterTheFlagWasCleared(bool throughTheService)
    {
        using var rig = await RigAsync();
        // Another plug keeps the pass going: the check runs while any plug has a single appliance.
        var other = await rig.AddPlugAsync("aabbccddee02", "Priza AC", PlugRole.Plug, PlugProtocols.Legacy);
        using (var ctx = rig.Db.CreateContext()) await new PlugRepository(ctx).SetApplianceAsync(other, true, "AC", "hvac", CancellationToken.None);
        await rig.PlugStreamAsync(rig.Fridge, 0, 500, 60, (300, 100_000));
        rig.Now = T0.AddSeconds(355);   // the transition of T0 + 300 is known, and 60 s old only at T0 + 360
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        var stored = await rig.AddEventsAsync(new[] { Ev(rig, 298, 60) });

        if (throughTheService)
        {
            await svc.SetPlugApplianceAsync(rig.Fridge, false, null, null, CancellationToken.None);
        }
        else
        {
            using var ctx = rig.Db.CreateContext();
            await new PlugRepository(ctx).SetApplianceAsync(rig.Fridge, false, null, null, CancellationToken.None);
        }
        rig.Now = T0.AddSeconds(400);
        await svc.RunOnceAsync(CancellationToken.None);

        var e = await rig.EventAsync(stored[0].Id);
        e.Source.Should().Be(EventLabelSource.None, "the plug has several consumers now");
        e.LabelPlugId.Should().BeNull();
    }

    [Fact]
    public async Task ATransitionOfAPlugStillFlagged_DoesMark_TheControlOfTheCaseAbove()
    {
        using var rig = await RigAsync();
        await rig.PlugStreamAsync(rig.Fridge, 0, 500, 60, (300, 100_000));
        rig.Now = T0.AddSeconds(355);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        var stored = await rig.AddEventsAsync(new[] { Ev(rig, 298, 60) });

        rig.Now = T0.AddSeconds(400);
        await svc.RunOnceAsync(CancellationToken.None);

        (await rig.EventAsync(stored[0].Id)).Source.Should().Be(EventLabelSource.PlugShadow);
    }

    [Fact]
    public async Task AShadow_SurvivesTheRegrouping_DbscanAndTheAnchorsDoNotTouchIt_AndTheResultIsTheSameTwice()
    {
        using var rig = await RigAsync();
        var named = await rig.AddSignatureAsync("Lampă", 60, null, SignatureLabelSource.User);
        var near = await rig.AddEventsAsync(new[] { Ev(rig, 298, 60), Ev(rig, 599, -58) });
        // Many like them, far from the plug's transitions: DBSCAN and the anchor would take the shadows along.
        await rig.AddEventsAsync(Enumerable.Range(0, 40).Select(k => Ev(rig, -30_000 + k * 60, k % 2 == 0 ? 60 : -60)));
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, (300, 300));
        rig.Now = T0.AddSeconds(1200);
        var svc = rig.Service();

        await svc.RegroupNowAsync(CancellationToken.None);

        (await rig.EventAsync(near[0].Id)).Should().Match<MeterEvent>(e => e.Source == EventLabelSource.PlugShadow && e.SignatureId == null);
        (await rig.EventAsync(near[1].Id)).Should().Match<MeterEvent>(e => e.Source == EventLabelSource.PlugShadow && e.SignatureId == null);
        var others = (await rig.AllEventsAsync()).Where(e => e.Source == EventLabelSource.None).ToList();
        others.Should().HaveCount(40).And.OnlyContain(e => e.SignatureId == named.Id, "the anchor still takes what is not a shadow");

        var before = await rig.AllEventsAsync();
        var again = await svc.RegroupNowAsync(CancellationToken.None);

        again.Moved.Should().Be(0);
        (await rig.AllEventsAsync()).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task AShadowNoLongerExplained_IsReleased_AndAShadowBeforeTheDataStays()
    {
        using var rig = await RigAsync();
        var stored = await rig.AddEventsAsync(new[]
        {
            Ev(rig, 500, 60, null, EventLabelSource.PlugShadow, null, rig.Fridge),        // no transition near it
            Ev(rig, -86_000, 60, null, EventLabelSource.PlugShadow, null, rig.Fridge),    // before the plug's first data
        });
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60);
        rig.Now = T0.AddSeconds(1200);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        var released = await rig.EventAsync(stored[0].Id);
        released.Source.Should().Be(EventLabelSource.None);
        released.LabelPlugId.Should().BeNull();
        (await rig.EventAsync(stored[1].Id)).Source.Should().Be(EventLabelSource.PlugShadow);
    }

    [Fact]
    public async Task TwoCandidates_AreNotShadows_AndConfirmedEventsStayWhatTheyAre()
    {
        using var rig = await RigAsync();
        var stored = await rig.AddEventsAsync(new[]
        {
            Ev(rig, 296, 60), Ev(rig, 299, 58),      // two candidates of the switch-on
            Ev(rig, 599, -60, null, EventLabelSource.AutoPlug, "Frigider", rig.Fridge),
        });
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, (300, 300));
        rig.Now = T0.AddSeconds(1200);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        (await rig.AllEventsAsync()).Should().NotContain(e => e.Source == EventLabelSource.PlugShadow);
        (await rig.EventAsync(stored[2].Id)).Source.Should().Be(EventLabelSource.AutoPlug);
    }

    [Fact]
    public async Task AShadow_StaysOutOfTheTraining_AndTheUserCanLabelIt()
    {
        using var rig = await RigAsync();
        var stored = await rig.AddEventsAsync(new[] { Ev(rig, 298, 60) });
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, (300, 300));
        rig.Now = T0.AddSeconds(1200);
        var svc = rig.Service();
        await svc.RegroupNowAsync(CancellationToken.None);
        (await rig.EventAsync(stored[0].Id)).Source.Should().Be(EventLabelSource.PlugShadow);

        var overview = await svc.GetOverviewAsync(0, 0, CancellationToken.None);
        overview.LabeledEvents.Should().Be(0, "not an example of the classifier");
        overview.Events.Should().ContainSingle().Which.SignatureName.Should().BeNull("the list of events shows it without a signature");
        (await svc.ListEventsAsync(T0, rig.Now, 0, CancellationToken.None)).Should().ContainSingle().Which.Event.Source.Should().Be(EventLabelSource.PlugShadow);

        (await svc.LabelEventAsync(stored[0].Id, "Lampă", CancellationToken.None)).Should().BeTrue();

        var labelled = await rig.EventAsync(stored[0].Id);
        labelled.Source.Should().Be(EventLabelSource.User);
        labelled.Label.Should().Be("Lampă");
        labelled.LabelPlugId.Should().BeNull();
        labelled.SignatureId.Should().NotBeNull();

        await svc.RegroupNowAsync(CancellationToken.None);

        (await rig.EventAsync(stored[0].Id)).Source.Should().Be(EventLabelSource.User, "the user decided; it is no shadow again");
        (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).LabeledEvents.Should().Be(1);
    }

    // ------------------------------------------------------------------ the row of /consumatori

    [Fact]
    public async Task TheRealShape_ThousandsOfUnconfirmedEvents_And84TransitionsADay_TheRowShowsWhatThePlugMeasured()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 39, rig.Fridge, count: 3000);
        var day = 86_400;
        // 84 transitions a day: 42 on, 42 off, about 17 minutes each cycle.
        var cycles = Cycles(600, 42, on: 900, period: 2040);
        await rig.PlugStreamAsync(rig.Fridge, 0, day, 62, cycles);
        // About 3,000 events a day, of 37 W: the band of small loads next to the fridge, none confirmed by it.
        await rig.AddEventsAsync(Enumerable.Range(0, 3000).Select(k => Ev(rig, k * 28 + 7, k % 2 == 0 ? 37 : -37, sig.Id, k % 11 == 0 ? EventLabelSource.Model : EventLabelSource.None, k % 11 == 0 ? "Priza UPS" : null)));
        rig.Now = T0.AddSeconds(day);
        var svc = rig.Service();

        await svc.RegroupNowAsync(CancellationToken.None);

        var row = (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).PlugMeasured.Should().ContainSingle().Subject;
        row.FromPlug.Should().NotBeNull();
        row.FromPlug!.Starts.Should().Be(42, "84 transitions: 42 switch-ons and 42 switch-offs");
        row.FromPlug.MeanPowerW.Should().BeApproximately(62, 0.5);
        row.FromPlug.Repetition.Durations.P50.Should().Be(900);
        row.FromPlug.Repetition.Intervals.P50.Should().Be(2040);
        row.Signature.Count.Should().Be(0, "Count stays the events it holds, and it holds no unconfirmed one");
        row.Last7DaysWh.Should().BeApproximately(42 * 900 * 62 / 3600.0, 1);
        (await rig.Repo(r => r.ListEventsBySignaturesAsync(new[] { sig.Id }, DateTimeOffset.UnixEpoch, CancellationToken.None))).Should().BeEmpty();
        (await rig.RunsOfAsync(sig.Id)).Should().HaveCount(42);
    }

    [Fact]
    public async Task TheRowsOfThePlugs_AreOrderedByWhatTheyShow_NotByTheEventsOfTheSignature()
    {
        using var rig = await RigAsync();
        var plain = await rig.AddPlugAsync("aabbccddee02", "Priza birou", PlugRole.Plug, PlugProtocols.Legacy);
        var other = await rig.AddSignatureAsync("Priza birou", 40, plain, count: 9);   // its last event: 5 days ago
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, (300, 300));
        rig.Now = T0.AddSeconds(1200);
        var svc = rig.Service();

        await svc.RunOnceAsync(CancellationToken.None);

        var rows = (await svc.GetOverviewAsync(0, 0, CancellationToken.None)).PlugMeasured;
        rows.Should().HaveCount(2);
        rows[0].FromPlug.Should().NotBeNull("no confirmed event, but its last switch-on is the newest");
        rows[0].Signature.LastSeenUtc.Should().BeNull("the signature itself stays as it is");
        rows[0].Signature.Count.Should().Be(0);
        rows[1].Signature.Id.Should().Be(other.Id);
    }

    [Fact]
    public async Task TheSheet_ShowsTheRunsFromThePlug_TheListOfEventsWhatItConfirmed()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge);
        await rig.AddEventsAsync(new[] { Confirmed(rig, 299, 60, sig.Id) });
        await rig.PlugStreamAsync(rig.Fridge, 0, 2400, 60, Cycles(300, 3));
        rig.Now = T0.AddSeconds(2400);
        var svc = rig.Service();
        await svc.RegroupNowAsync(CancellationToken.None);

        var sheet = (await svc.GetProfileAsync(sig.Id, CancellationToken.None))!;

        sheet.Profile.Runs.Should().Be(3);
        sheet.Profile.MedianRunSeconds.Should().Be(300);
        sheet.Profile.Repetition!.Intervals.P50.Should().Be(600);
        sheet.Profile.Events.Should().Be(1);
        (await svc.ListSignatureEventsAsync(sig.Id, 10, CancellationToken.None)).Should().ContainSingle();
    }

    // ------------------------------------------------------------------ what does not change

    [Fact]
    public async Task APlugWithSeveralConsumers_KeepsItsRunsFromEvents_AndNoShadows()
    {
        using var rig = await RigAsync(single: false);
        var sig = await rig.AddSignatureAsync("Priza frigider", 60, rig.Fridge);
        await rig.AddEventsAsync(new[] { Confirmed(rig, 299, 60, sig.Id), Ev(rig, 599, -60, sig.Id) });
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, (300, 300));
        rig.Now = T0.AddSeconds(1200);

        await rig.Service().RegroupNowAsync(CancellationToken.None);

        var run = (await rig.RunsOfAsync(sig.Id)).Should().ContainSingle().Subject;
        run.FromPlug.Should().BeFalse();
        run.EndEventId.Should().NotBeNull();
        (await rig.AllEventsAsync()).Should().NotContain(e => e.Source == EventLabelSource.PlugShadow);
        (await rig.SignaturesAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task ClearingTheFlag_GivesTheRunsBackToTheEvents()
    {
        using var rig = await RigAsync();
        var sig = await rig.AddSignatureAsync("Frigider", 60, rig.Fridge);
        await rig.AddEventsAsync(new[] { Confirmed(rig, 299, 60, sig.Id), Confirmed(rig, 599, -60, sig.Id) });
        await rig.PlugStreamAsync(rig.Fridge, 0, 1200, 60, (300, 300));
        rig.Now = T0.AddSeconds(1200);
        var svc = rig.Service();
        await svc.RunOnceAsync(CancellationToken.None);
        (await rig.RunsOfAsync(sig.Id)).Should().ContainSingle().Which.FromPlug.Should().BeTrue();

        await svc.SetPlugApplianceAsync(rig.Fridge, false, null, null, CancellationToken.None);

        (await rig.RunsOfAsync(sig.Id)).Should().ContainSingle().Which.FromPlug.Should().BeFalse();
    }

    [Fact]
    public async Task SettingTheFlag_TakesTheRunsOverAtTheNextPass()
    {
        using var rig = await RigAsync(single: false);
        var sig = await rig.AddSignatureAsync("Priza frigider", 60, rig.Fridge);
        await rig.AddEventsAsync(new[] { Confirmed(rig, 299, 60, sig.Id), Confirmed(rig, 599, -60, sig.Id), Confirmed(rig, 899, 60, sig.Id), Confirmed(rig, 1199, -60, sig.Id) });
        await rig.PlugStreamAsync(rig.Fridge, 0, 1800, 60, Cycles(300, 2));
        rig.Now = T0.AddSeconds(1800);
        var svc = rig.Service();
        await svc.RegroupNowAsync(CancellationToken.None);
        (await rig.RunsOfAsync(sig.Id)).Should().HaveCount(2).And.OnlyContain(r => !r.FromPlug);

        await svc.SetPlugApplianceAsync(rig.Fridge, true, "Frigider", null, CancellationToken.None);
        rig.Now = rig.Now.AddSeconds(30);
        await svc.RunOnceAsync(CancellationToken.None);

        (await rig.RunsOfAsync(sig.Id)).Should().HaveCount(2).And.OnlyContain(r => r.FromPlug);
    }
}
