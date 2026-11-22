using FluentAssertions;
using KasaMonitor.Core.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Heating;

public class EcoTimelineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 22, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Id = Guid.NewGuid();
    private static readonly TimeSpan Cadence = TimeSpan.FromMinutes(2);

    private static HeatingReading R(int minute, bool? eco, string? reason = null) =>
        new(Id, T0.AddMinutes(minute), null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            EcoActive: eco, EcoReason: reason);

    [Fact]
    public void AnInterval_RunsFromTheFirstReadingOn_ToTheNextReadingOff()
    {
        var list = EcoTimeline.Build(new[] { R(0, false), R(2, true, "summerEco"), R(4, true, "other"), R(6, false), R(8, true, "summerEco"), R(10, null) },
            Cadence, T0.AddHours(1));

        list.Should().Equal(
            new EcoInterval(T0.AddMinutes(2), T0.AddMinutes(6), "summerEco", false),
            new EcoInterval(T0.AddMinutes(8), T0.AddMinutes(10), "summerEco", false));
    }

    [Fact]
    public void OneStillOn_RunsOneCadenceFurther_NeverPastNow()
    {
        EcoTimeline.Build(new[] { R(0, true, "summerEco") }, Cadence, T0.AddHours(1)).Single()
            .Should().Be(new EcoInterval(T0, T0.AddMinutes(2), "summerEco", true));
        EcoTimeline.Build(new[] { R(0, true) }, Cadence, T0.AddMinutes(1)).Single().EndUtc.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public void NoReadings_OrNoneOn_NoInterval()
    {
        EcoTimeline.Build(Array.Empty<HeatingReading>(), Cadence, T0).Should().BeEmpty();
        EcoTimeline.Build(new[] { R(0, false), R(2, null) }, Cadence, T0.AddHours(1)).Should().BeEmpty();
    }
}
