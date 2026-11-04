using FluentAssertions;
using KasaMonitor.Core.Nilm;
using KasaMonitor.NilmReplay;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

/// <summary>The replay behind the 0.13.0 decision on the median filter, on synthetic seconds; the real series stays outside the repository.</summary>
public class MedianReplayTests
{
    private const long T0 = 1_800_000_000;

    private static MeterRow R(long second, double w, double? va = null) => new(T0 + second, w, va ?? w, 1);

    private static List<MeterRow> Series(int seconds, Func<int, double> w) => Enumerable.Range(0, seconds).Select(t => R(t, w(t))).ToList();

    [Fact]
    public void Grid_HoldsAMissingSecondUpToTheDetectorsLimit_AndNotAcrossALongerHole()
    {
        var rows = new List<MeterRow> { R(0, 100), R(3, 200), R(3, 999), R(3 + MeterEventDetector.MaxFillSeconds + 1, 300) };

        var grid = MedianReplay.Grid(rows);

        grid.Take(4).Select(r => r.PowerW).Should().Equal(100, 100, 100, 200);
        grid.Select(r => r.Ts).Should().OnlyHaveUniqueItems();
        grid.Should().HaveCount(5, "0–2 held, 3, then the row after the hole, nothing in it");
    }

    [Fact]
    public void Median_RemovesASpikeOfOneSecond_AndKeepsTheSecondOfAStep()
    {
        var spike = MedianReplay.Median3(Series(9, t => t == 4 ? 900 : 100));
        spike.Select(r => r.PowerW).Should().OnlyContain(w => w == 100);

        var step = MedianReplay.Median3(Series(9, t => t < 4 ? 100 : 900));
        step.Select(r => r.PowerW).Should().Equal(100, 100, 100, 100, 900, 900, 900, 900, 900);
        step.Select(r => r.Ts).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Median_KeepsPowerAndApparentPowerFromTheSameSecond()
    {
        var rows = new List<MeterRow> { R(0, 100, 111), R(1, 900, 999), R(2, 200, 222) };
        MedianReplay.Median3(rows)[1].Should().Be(new MeterRow(T0 + 1, 200, 222, 1));
    }

    [Fact]
    public void Median_NextToAHole_KeepsItsOwnValue()
    {
        var rows = new List<MeterRow> { R(0, 100), R(1, 900), R(100, 300), R(101, 100), R(102, 900) };
        MedianReplay.Median3(rows).Select(r => r.PowerW).Should().Equal(100, 900, 300, 300, 900);
    }

    [Fact]
    public void TheMedianMode_TakesItsLevelsFromTheFilteredSeries_ButTheInrushFromTheRawOne()
    {
        // A motor: 100 W, 700 W for one second, then 300 W.
        var c = MedianReplay.Run(Series(60, t => t < 20 ? 100 : t == 20 ? 700 : 300));

        c.Actual.Should().ContainSingle();
        c.Median.Should().ContainSingle();
        c.Matches.Should().ContainSingle();
        c.Actual[0].TransientRatio.Should().Be(3);
        c.Median[0].TransientRatio.Should().Be(3, "the raw peak, not the filtered series");
        c.Median[0].DeltaP.Should().BeApproximately(200, 0.1);
        c.Matches[0].SecondsApart.Should().Be(0);
        c.Matches[0].TransientChanged.Should().BeFalse();
    }

    [Fact]
    public void Compare_PairsTheNearestInTimeOfTheSameDirection_AndListsTheRest()
    {
        DetectedEvent E(int second, double p) => new(Guid.Empty, DateTimeOffset.FromUnixTimeSeconds(T0 + second), p, 0, 0, 1, 0, 300);
        var actual = new[] { E(10, 100), E(20, -100), E(30, 500) };
        var median = new[] { E(11, 110), E(21, 100), E(33, 500), E(40, 50) };

        var c = MedianReplay.Compare(actual, median);

        c.Matches.Should().ContainSingle().Which.Should().Match<EventMatch>(m => m.SecondsApart == 1 && m.DeltaPDifference == 10);
        c.OnlyActual.Select(e => e.DeltaP).Should().Equal(-100, 500);
        c.OnlyMedian.Select(e => e.DeltaP).Should().Equal(100, 500, 50);
    }

    [Fact]
    public void Flickers_CountAStepAndItsOppositeWithinTheLimit_OnlyOnce()
    {
        DetectedEvent E(int second, double p) => new(Guid.Empty, DateTimeOffset.FromUnixTimeSeconds(T0 + second), p, 0, 0, 1, 0, 300);
        MedianReplay.Flickers(new[] { E(0, 100), E(4, -100), E(5, 100), E(30, -100), E(32, 400), E(34, -100) }, 5).Should().Be(1);
    }

    [Fact]
    public void ConfirmedByRaw_NeedsALastingChangeOfLevel()
    {
        var raw = Series(40, t => t is >= 20 and < 30 ? 900 : 100).ToDictionary(r => r.Ts, r => r.PowerW);
        DetectedEvent E(int second, double p) => new(Guid.Empty, DateTimeOffset.FromUnixTimeSeconds(T0 + second), p, 0, 0, 1, 0, 100);

        MedianReplay.ConfirmedByRaw(E(20, 800), raw).Should().BeTrue();
        MedianReplay.ConfirmedByRaw(E(30, -800), raw).Should().BeTrue();
        MedianReplay.ConfirmedByRaw(E(10, 800), raw).Should().BeFalse("no such level in the raw seconds");
        MedianReplay.ConfirmedByRaw(E(1000, 800), raw).Should().BeFalse("no raw seconds");
    }

    [Fact]
    public void ReadCsv_SkipsAHeader_AndReadsTheColumnsOfTheExport()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nilm-replay-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllLines(path, new[]
            {
                "Ts,PowerW,ApparentVa,VoltageV,CurrentA,Pf",
                $"{T0 + 1},500.5,750.25,230,3.25,0.75",
                $"{T0},100,,230,1,",
            });
            MedianReplay.ReadCsv(path).Should().Equal(new MeterRow(T0, 100, null, null), new MeterRow(T0 + 1, 500.5, 750.25, 0.75));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
