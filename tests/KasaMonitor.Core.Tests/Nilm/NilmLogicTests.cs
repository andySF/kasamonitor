using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

public class SignatureClustererTests
{
    /// <summary>Three appliances, 12 switch-ons and 12 switch-offs each, with measurement noise; plus two stray events.</summary>
    private static List<EventFeatures> ThreeAppliances(out Dictionary<long, string> truth)
    {
        var rnd = new Random(3);
        double N(double x, double rel) => x * (1 + (rnd.NextDouble() - 0.5) * 2 * rel);
        var list = new List<EventFeatures>();
        var names = new Dictionary<long, string>();
        truth = names;
        long id = 1;
        void Add(string name, double p, double q, double t)
        {
            for (var i = 0; i < 12; i++)
            {
                names[id] = name;
                list.Add(new EventFeatures(id++, N(p, 0.03), N(q, 0.05), N(t, 0.1)));     // switch-on
                names[id] = name;
                list.Add(new EventFeatures(id++, -N(p, 0.03), -N(q, 0.05), 1));           // switch-off: no inrush, Q mirrored
            }
        }
        Add("fridge", 120, 60, 5);
        Add("kettle", 2000, 5, 1);
        Add("tv", 75, 20, 1.2);
        list.Add(new EventFeatures(id++, 900, 300, 2));
        list.Add(new EventFeatures(id, -430, -10, 1));
        return list;
    }

    [Fact]
    public void Dbscan_ThreeAppliances_ThreeClustersWithOnAndOffTogether_StrayEventsAreNoise()
    {
        var events = ThreeAppliances(out var truth);
        var labels = SignatureClusterer.Cluster(events);

        var clusters = events.Select((e, i) => (e, label: labels[i])).Where(x => x.label >= 0).GroupBy(x => x.label).ToList();
        clusters.Should().HaveCount(3);
        foreach (var c in clusters)
        {
            c.Select(x => truth[x.e.Id]).Distinct().Should().ContainSingle("a cluster is one appliance");
            c.Should().HaveCount(24);
            c.Count(x => x.e.DeltaP > 0).Should().Be(12);
            c.Count(x => x.e.DeltaP < 0).Should().Be(12);
        }
        labels[^1].Should().Be(-1);
        labels[^2].Should().Be(-1);
    }

    [Fact]
    public void Dbscan_MotorAndResistiveOfTheSamePower_AreSeparatedByTransient()
    {
        var events = new List<EventFeatures>();
        for (var i = 0; i < 5; i++) events.Add(new EventFeatures(i, 500, 0, 1));
        for (var i = 5; i < 10; i++) events.Add(new EventFeatures(i, 500, 0, 6));
        SignatureClusterer.Cluster(events).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Nearest_WithinEps_ElseNull()
    {
        var centres = new[] { new SignatureCentre(1, 120, 60, 5, true), new SignatureCentre(2, 2000, 5, 1, true) };
        SignatureClusterer.Nearest(new EventFeatures(0, -118, -58, 1), centres).Should().Be(1);       // switch-off of the fridge
        SignatureClusterer.Nearest(new EventFeatures(0, 1950, 10, 1.1), centres).Should().Be(2);
        SignatureClusterer.Nearest(new EventFeatures(0, 400, 0, 1), centres).Should().BeNull();
        SignatureClusterer.Eps(120, 118).Should().BeApproximately(0.08 * 120 + 3, 1e-9);
    }

    [Fact]
    public void Dbscan_DenseBandOfSmallLoads_IsCutIntoNarrowClusters_NotChainedIntoOne()
    {
        // 30 to 90 W, one event every 0.25 W: DBSCAN alone chains the whole band.
        var events = Enumerable.Range(0, 240).Select(i => new EventFeatures(i, 30 + i * 0.25, 0, 1)).ToList();
        var labels = SignatureClusterer.Cluster(events);
        foreach (var g in events.Select((e, i) => (e, l: labels[i])).Where(x => x.l >= 0).GroupBy(x => x.l))
        {
            var min = g.Min(x => x.e.Magnitude);
            g.Max(x => x.e.Magnitude).Should().BeLessThanOrEqualTo(SignatureClusterer.MaxSpan(min));
        }
        labels.Where(l => l >= 0).Distinct().Count().Should().BeGreaterThan(3);
    }

    [Fact]
    public void Dbscan_TwentyThousandEventsInADenseBand_FinishesQuickly()
    {
        var rnd = new Random(1);
        var events = Enumerable.Range(0, SignatureClusterer.MaxEvents)
            .Select(i => new EventFeatures(i, (rnd.Next(2) == 0 ? 1 : -1) * (15 + rnd.NextDouble() * 25), rnd.NextDouble() * 20 - 10, 1 + rnd.NextDouble()))
            .ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var labels = SignatureClusterer.Cluster(events);
        sw.Stop();
        labels.Should().HaveCount(events.Count);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().BeLessThan(200L * 1024 * 1024, "the expansion queue holds each point once");
    }

    [Fact]
    public void Means_MagnitudeOnSideQ_AndTransientOfSwitchOnsOnly()
    {
        var (p, q, t) = SignatureClusterer.Means(new[] { new EventFeatures(1, 100, 50, 4), new EventFeatures(2, -110, -60, 1) });
        p.Should().Be(105);
        q.Should().Be(55);
        t.Should().Be(4);
    }
}

public class PlugEventMatcherTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid Fridge = Guid.NewGuid();
    private static readonly Guid Tv = Guid.NewGuid();

    /// <summary>10-second readings from T−60 to T+60, stepping by <paramref name="delta"/> at <paramref name="stepAt"/> (seconds from T).</summary>
    private static PlugSeries Plug(Guid id, string alias, double before, double delta, int stepAt, int phase = 3) =>
        new(id, alias, Enumerable.Range(-6, 13).Select(k => k * 10 + phase)
            .Select(s => new PlugPowerPoint(T.AddSeconds(s), s >= stepAt ? before + delta : before)).ToList());

    [Fact]
    public void PlugThatMadeACleanStepOfDeltaP_InsideTheWindow_LabelsTheEvent()
    {
        var m = PlugEventMatcher.Match(T, 120, new[] { Plug(Fridge, "Frigider", 2, 118, 8), Plug(Tv, "TV", 60, 0, 0) });
        m.Should().NotBeNull();
        m!.Alias.Should().Be("Frigider");
        m.DeltaW.Should().Be(118);
    }

    [Fact]
    public void ChangeOutsideTheWindow_OrOfAnotherSize_OrDirection_NoMatch()
    {
        PlugEventMatcher.Match(T, 120, new[] { Plug(Fridge, "Frigider", 2, 118, 40) }).Should().BeNull();      // 40 s later
        PlugEventMatcher.Match(T, 120, new[] { Plug(Fridge, "Frigider", 2, 160, 5) }).Should().BeNull();       // ±max(10 W, 15 %) = ±18 W
        PlugEventMatcher.Match(T, -120, new[] { Plug(Fridge, "Frigider", 2, 118, 5) }).Should().BeNull();      // wrong direction
        PlugEventMatcher.Match(T, -120, new[] { Plug(Fridge, "Frigider", 120, -118, -12) }).Should().NotBeNull();
    }

    [Fact]
    public void AReadingDuringTheInrushIsSkipped()
    {
        var withInrush = Plug(Fridge, "Frigider", 2, 120, 5);
        var points = withInrush.Points.Select(p => p.TsUtc == T.AddSeconds(13) ? p with { PowerW = 700 } : p).ToList();
        var m = PlugEventMatcher.Match(T, 121, new[] { withInrush with { Points = points } });
        m!.Alias.Should().Be("Frigider");
        m.ErrorW.Should().Be(1);
    }

    [Fact]
    public void TwoPlugsThatMovedInTheWindow_NoLabel()
    {
        PlugEventMatcher.Match(T, 121, new[] { Plug(Fridge, "Frigider", 2, 120, 5), Plug(Tv, "TV", 10, 90, 5) })
            .Should().BeNull("TV moved by at least 60 % of the step too: ambiguous");
    }

    [Fact]
    public void APlugExcludedFromLearningThatMovedToo_MakesTheEventAmbiguous_ButCannotLabelItself()
    {
        var lamp = Plug(Fridge, "Hol mic", 2, 35, 5);
        var ups = Plug(Tv, "Ups Birou", 60, 34, 5) with { CanLabel = false };
        PlugEventMatcher.Match(T, 35, new[] { lamp, ups }).Should().BeNull("the excluded UPS moved in the same window");
        PlugEventMatcher.Match(T, 35, new[] { lamp }).Should().NotBeNull();
        PlugEventMatcher.Match(T, 34, new[] { ups }).Should().BeNull("an excluded plug never labels");
    }

    [Fact]
    public void VariableLoadIsNotAStep_AndOnePlugChangeLabelsOneEvent()
    {
        // A UPS jumping around: the readings before and after the "step" do not agree with it.
        var watts = new[] { 60.0, 95, 70, 100, 62, 98 };
        var ups = new PlugSeries(Fridge, "Ups", new[] { -17, -7, 3, 13, 23, 33 }
            .Select((s, i) => new PlugPowerPoint(T.AddSeconds(s), watts[i])).ToList());
        PlugEventMatcher.Match(T, 30, new[] { ups }).Should().BeNull();

        var used = new HashSet<(Guid, DateTimeOffset)>();
        var fridge = Plug(Fridge, "Frigider", 2, 118, 8);
        PlugEventMatcher.Match(T, 120, new[] { fridge }, used).Should().NotBeNull();
        PlugEventMatcher.Match(T.AddSeconds(2), 120, new[] { fridge }, used).Should().BeNull("that plug change already labelled an event");
    }
}

public class ApplianceRunTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    private static MeterEvent E(long id, long? sig, int minute, double deltaP, double before = 300) =>
        new(id, Guid.Empty, T.AddMinutes(minute), deltaP, 0, 0, 1, 0, before, sig);

    [Fact]
    public void OnThenOff_SameSignature_ClosesTheRun_WithEnergyFromDeltaPAndDuration()
    {
        var b = new ApplianceRunBuilder();
        b.Add(E(1, 7, 0, 120)).Opened.Should().NotBeNull();
        b.Open().Should().ContainSingle().Which.HouseBeforeW.Should().Be(300);
        var closed = b.Add(E(2, 7, 20, -117, 420)).Closed;
        closed.Should().ContainSingle();
        closed[0].EstimatedWh.Should().BeApproximately(120 * 20 / 60.0, 1e-9);   // 40 Wh
        closed[0].EndEventId.Should().Be(2);
        closed[0].EndReason.Should().Be(RunEndReason.Paired);
        b.Open().Should().BeEmpty();
    }

    [Fact]
    public void Fifo_OtherSignatureOrMagnitude_NotPaired()
    {
        var b = new ApplianceRunBuilder();
        b.Add(E(1, 7, 0, 120, 300));
        b.Add(E(2, 7, 1, 125, 420));
        b.Add(E(3, 8, 2, -40, 900)).Closed.Should().BeEmpty("another signature, house still high");
        b.Add(E(4, 7, 3, -500, 1400)).Closed.Should().BeEmpty("another magnitude, house still above both levels");
        b.Add(E(5, 7, 10, -122, 900)).Closed.Single().StartEventId.Should().Be(1, "first in, first out");
        b.Add(new MeterEvent(8, Guid.Empty, T, 50, 0, 0, 1, 0, 0)).Opened.Should().BeNull("no signature, no run");
    }

    [Fact]
    public void OffOfAnotherAppliance_BringingTheHouseBelowTheLevelBeforeTheSwitchOn_ClosesTheRun()
    {
        var b = new ApplianceRunBuilder();
        b.Add(E(1, 7, 0, 120, 300));                          // house 300 → 420
        b.Add(E(2, 9, 5, 80, 420));                           // house 420 → 500
        var closed = b.Add(E(3, null, 10, -205, 500)).Closed; // house 500 → 295: below both levels
        closed.Select(r => r.StartEventId).Should().BeEquivalentTo(new long[] { 1, 2 });
        closed.Should().OnlyContain(r => r.EndReason == RunEndReason.HouseBelowLevel && r.EstimatedWh > 0);
    }

    [Fact]
    public void RunOpenLongerThanSixHours_ClosedAsUncertain_WithoutEnergy()
    {
        var b = new ApplianceRunBuilder();
        b.Add(E(1, 7, 0, 100));
        b.Expire(T.AddHours(5)).Should().BeEmpty();
        var expired = b.Expire(T.AddHours(7));
        expired.Should().ContainSingle();
        expired[0].EndReason.Should().Be(RunEndReason.Timeout);
        expired[0].EndUtc.Should().Be(T.AddHours(6));
        expired[0].EstimatedWh.Should().Be(0, "the switch-off was never seen: no energy");
        ApplianceEnergy.InRange(expired[0], T, T.AddDays(1), T.AddHours(7)).Should().Be(0);
        b.Open().Should().BeEmpty();
        b.Add(E(2, 7, 8 * 60, -100)).Closed.Should().BeEmpty();
    }

    [Fact]
    public void EnergyInRange_SplitsARunAcrossMidnight_AndCountsAnOpenRunUntilNowAtMostSixHours()
    {
        var midnight = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var run = new ApplianceRun(1, 7, midnight.AddHours(-1), midnight.AddHours(1), 1000, 2000, 1, 2);
        ApplianceEnergy.InRange(run, midnight, midnight.AddDays(1), midnight.AddHours(5)).Should().BeApproximately(1000, 1e-9);
        var open = new ApplianceRun(2, 7, midnight.AddHours(2), null, 500, null, 3, null);
        ApplianceEnergy.InRange(open, midnight, midnight.AddDays(1), midnight.AddHours(4)).Should().BeApproximately(1000, 1e-9);
        ApplianceEnergy.InRange(open, midnight, midnight.AddDays(3), midnight.AddHours(30)).Should().BeApproximately(3000, 1e-9, "at most 6 h");
    }

    [Fact]
    public void Corrected_FromTheHouseMean_OnlyWithinHalfAndTwiceTheNominal_PlugsScaledToTheCoveredTime()
    {
        var d = TimeSpan.FromMinutes(30);
        ApplianceEnergy.Corrected(60, houseMeanW: 410, powerBeforeW: 300, d).Should().BeApproximately(55, 1e-9);
        ApplianceEnergy.Corrected(60, houseMeanW: 900, powerBeforeW: 300, d).Should().Be(60);
        ApplianceEnergy.Unidentified(5000, 1200, 1500).Should().Be(2300);
        ApplianceEnergy.Unidentified(1000, 1200, 500).Should().Be(0);
        ApplianceEnergy.PlugsOverCoveredTime(1200, coveredSeconds: 900, secondsSinceMidnight: 36_000).Should().Be(30);
    }

    [Fact]
    public void MeanPower_ForwardFillsRowsStoredOnChange()
    {
        var id = Guid.NewGuid();
        var rows = new[] { new LiveSample(id, T, 300), new LiveSample(id, T.AddSeconds(30), 400), new LiveSample(id, T.AddSeconds(60), 400) };
        SignatureNaming.MeanPower(rows, T.AddSeconds(10), T.AddSeconds(50)).Should().BeApproximately(350, 1e-9);
        SignatureNaming.MeanPower(rows, T.AddHours(1), T.AddHours(2)).Should().BeNull("not covered");
    }

    [Fact]
    public void PlugName_NeedsSixtyPercentOfTheEventsFromOnePlug_AndNamesAreCleaned()
    {
        var fridge = Guid.NewGuid();
        MeterEvent L(int i, Guid? plug) => new(i, Guid.Empty, T.AddMinutes(i), 120, 0, 0, 1, 0, 0, 1,
            plug is null ? null : "Frigider", plug is null ? EventLabelSource.None : EventLabelSource.AutoPlug, plug);
        SignatureNaming.FromPlugs(new[] { L(1, fridge), L(2, fridge), L(3, fridge), L(4, null), L(5, null) })!.Value.Alias.Should().Be("Frigider");
        SignatureNaming.FromPlugs(new[] { L(1, fridge), L(2, fridge), L(3, null), L(4, null), L(5, null) }).Should().BeNull();
        SignatureNaming.FromPlugs(new[] { L(1, fridge), L(2, fridge) }).Should().BeNull("100 %, but fewer than 3 events");
        SignatureNaming.LinksToPlug(3, 5).Should().BeTrue();
        SignatureNaming.LinksToPlug(2, 2).Should().BeFalse();
        SignatureNaming.CleanName("  Frigider   bucătărie ").Should().Be("Frigider bucătărie");
        SignatureNaming.CleanName(new string('x', 100)).Should().HaveLength(SignatureNaming.MaxNameLength);
    }

    [Fact]
    public void SignatureMeasuredByAPlug_IsDecidedByThePlugId_NotByTheName()
    {
        var s = new ApplianceSignature(1, "UPS birou (redenumit)", "", "other", 30, 0, 1, 10, null, SignatureLabelSource.User, Guid.NewGuid(), false);
        s.ConfirmedByPlug.Should().BeTrue();
        (s with { PlugId = null, LabelSource = SignatureLabelSource.Plug }).ConfirmedByPlug.Should().BeFalse();
    }
}
