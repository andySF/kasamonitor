using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

public class MeterEventDetectorTests
{
    private static readonly Guid Main = Guid.Parse("6f1c3c1e-0000-0000-0000-00000000aaaa");
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private static LiveSample S(int second, double w, double? va = null) => new(Main, T0.AddSeconds(second), w, va);

    private static List<DetectedEvent> Feed(MeterEventDetector d, IEnumerable<LiveSample> samples) =>
        samples.SelectMany(d.Add).ToList();

    /// <summary>One sample per second: <paramref name="levels"/> = (from second, W, VA) segments, until <paramref name="end"/>.</summary>
    private static IEnumerable<LiveSample> Series(int end, params (int From, double W, double? Va)[] levels)
    {
        for (var t = 0; t < end; t++)
        {
            var l = levels.Last(x => x.From <= t);
            yield return S(t, l.W, l.Va);
        }
    }

    [Fact]
    public void CleanStep_OneEvent_WithDeltaP_RatioOne_NoRise_AndThePowerBefore()
    {
        var d = new MeterEventDetector();
        var events = Feed(d, Series(60, (0, 100, 100), (20, 1100, 1100)));

        events.Should().ContainSingle();
        var e = events[0];
        e.TsUtc.Should().Be(T0.AddSeconds(20));
        e.DeltaP.Should().BeApproximately(1000, 0.01);
        e.DeltaQ.Should().BeApproximately(0, 0.01);
        e.TransientRatio.Should().Be(1);
        e.RiseSeconds.Should().Be(0);
        e.PowerBeforeW.Should().BeApproximately(100, 0.01);

        // ...and the switch-off is the mirror image.
        var off = Feed(d, Series(120, (0, 1100, 1100), (90, 100, 100)).Where(s => s.TsUtc >= T0.AddSeconds(60)));
        off.Should().ContainSingle().Which.DeltaP.Should().BeApproximately(-1000, 0.01);
    }

    [Fact]
    public void MotorStep_InrushPeakBecomesTheTransientRatio_ReactivePowerFromApparent()
    {
        // Fridge compressor: base 200 W (resistive), inrush 800 → 450 W, then 323 W steady with pf ≈ 0.8 (Q ≈ 90 VAR above base).
        var samples = new List<LiveSample>();
        for (var t = 0; t < 20; t++) samples.Add(S(t, 200, 200));
        samples.Add(S(20, 800, 1000));
        samples.Add(S(21, 450, 520));
        for (var t = 22; t < 60; t++) samples.Add(S(t, t % 2 == 0 ? 320 : 326, t % 2 == 0 ? 332 : 338));

        var events = Feed(new MeterEventDetector(), samples);

        events.Should().ContainSingle();
        var e = events[0];
        e.TsUtc.Should().Be(T0.AddSeconds(20));
        e.DeltaP.Should().BeApproximately(123, 2);
        e.TransientRatio.Should().BeGreaterThan(3).And.BeLessThan(8);   // (800 − 200) / 123 ≈ 4.9
        e.RiseSeconds.Should().Be(2);
        e.DeltaQ.Should().BeApproximately(88.6, 3);                     // √(335² − 323²)
    }

    [Fact]
    public void NoiseBelowThreshold_AndASmallStep_NoEvent()
    {
        var rnd = new Random(7);
        var samples = new List<LiveSample>();
        for (var t = 0; t < 300; t++)
        {
            var level = t < 150 ? 300 : 310;                         // a 10 W step: below the 15 W threshold
            samples.Add(S(t, level + (rnd.NextDouble() - 0.5) * 6));  // ± 3 W noise
        }
        Feed(new MeterEventDetector(), samples).Should().BeEmpty();
    }

    [Fact]
    public void TwoAppliances2SecondsApart_TwoEvents()
    {
        var events = Feed(new MeterEventDetector(), Series(60, (0, 200, null), (20, 700, null), (22, 800, null)));

        events.Should().HaveCount(2);
        events[0].TsUtc.Should().Be(T0.AddSeconds(20));
        events[0].DeltaP.Should().BeApproximately(500, 0.01);
        events[1].TsUtc.Should().Be(T0.AddSeconds(22));
        events[1].DeltaP.Should().BeApproximately(100, 0.01);
        events[1].PowerBeforeW.Should().BeApproximately(700, 0.01);
    }

    [Fact]
    public void SlowVariation_IsNotAnEvent()
    {
        // 200 → 320 W over two minutes (1 W/s), then steady: the "before" level follows it.
        var samples = Enumerable.Range(0, 200).Select(t => S(t, 200 + Math.Min(120, Math.Max(0, t - 30)))).ToList();
        Feed(new MeterEventDetector(), samples).Should().BeEmpty();
    }

    [Fact]
    public void StoredOnChangeRows_GapsFilledWithThePreviousValue_SameEventAsTheFullSeries()
    {
        // The raw table keeps a row only on change (or every 60 s): 100 W at 0 and 60 (witness), the step at 90.
        var rows = new[] { S(0, 100), S(60, 100), S(90, 1100), S(150, 1100) };
        var events = Feed(new MeterEventDetector(), rows);

        events.Should().ContainSingle();
        events[0].TsUtc.Should().Be(T0.AddSeconds(90));
        events[0].DeltaP.Should().BeApproximately(1000, 0.01);
    }

    [Fact]
    public void GapLongerThanTheFill_RestartsWithoutAnEventAcrossIt()
    {
        var rows = new[] { S(0, 100), S(1, 100), S(2, 100), S(3, 100), S(200, 1100), S(201, 1100), S(202, 1100), S(203, 1100) };
        Feed(new MeterEventDetector(), rows).Should().BeEmpty();
    }

    [Fact]
    public void Threshold_IsTheSetting()
    {
        var series = Series(60, (0, 300, null), (20, 340, null)).ToList();
        Feed(new MeterEventDetector(minEventDeltaW: 50), series).Should().BeEmpty();
        Feed(new MeterEventDetector(minEventDeltaW: 30), series).Should().ContainSingle();
    }

    [Fact]
    public void ShortSpikeThatReturns_IsNotAnEvent()
    {
        var samples = Series(60, (0, 300, null)).Select(s => s.TsUtc == T0.AddSeconds(30) ? s with { PowerW = 900 } : s);
        Feed(new MeterEventDetector(), samples).Should().BeEmpty();
    }
}
