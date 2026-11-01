using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Web.Ui;
using Xunit;

namespace KasaMonitor.Web.Tests;

/// <summary>The sliding window behind the live chart of /contor (logic only, not the rendering).</summary>
public class LiveChartWindowTests
{
    private static readonly Guid Plug = Guid.NewGuid();
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KeepsTenMinutes_AtMost600Points_InTheSameListInstance()
    {
        var w = new LiveChartWindow();
        var list = w.Points;

        for (var i = 0; i < 900; i++) w.Add(new LiveSample(Plug, T0.AddSeconds(i), i)).Should().BeTrue();

        w.Points.Should().BeSameAs(list);   // the chart keeps its Items reference; only UpdateSeriesAsync is called
        w.Points.Should().HaveCount(LiveChartWindow.MaxPoints);
        w.Points[0].TimeUtc.Should().Be(T0.AddSeconds(300).UtcDateTime);
        w.Points[^1].PowerW.Should().Be(899);
        w.Points[^1].TimeUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void Gaps_ShrinkTheWindowByTime_AndOutOfOrderSamplesAreIgnored()
    {
        var w = new LiveChartWindow();
        w.Seed(new[] { new LiveSample(Plug, T0.AddSeconds(2), 2), new LiveSample(Plug, T0, 0), new LiveSample(Plug, T0.AddSeconds(1), 1) });
        w.Points.Select(p => p.PowerW).Should().Equal(0, 1, 2);   // seeded oldest first

        w.Add(new LiveSample(Plug, T0.AddSeconds(1), 9)).Should().BeFalse();
        w.Add(new LiveSample(Plug, T0.AddMinutes(10).AddSeconds(1.5), 1234.56)).Should().BeTrue();   // 10 min later: the first two are gone

        w.Points.Select(p => p.PowerW).Should().Equal(2, 1234.6m);
    }
}
