using FluentAssertions;
using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;
using Xunit;

namespace KasaMonitor.Core.Tests.Metering;

public class HouseRestTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Power_ClearlyPositive()
    {
        var b = HouseBalance.Balance(1000, 400);
        b.Kind.Should().Be(RestKind.Positive);
        b.RestW.Should().Be(600);
    }

    [Theory]
    [InlineData(460, 470)]   // the measured case: plugs 10 W above the meter (noise ±2 %)
    [InlineData(460, 445)]   // 15 W: on the fixed margin
    [InlineData(1000, 1029)] // 3 % of 1000 W = 30 W > 15 W
    [InlineData(1000, 971)]
    public void Power_WithinTheMargin_IsAboutZero(double main, double plugs)
    {
        var b = HouseBalance.Balance(main, plugs);
        b.Kind.Should().Be(RestKind.WithinMargin);
        b.RestW.Should().Be(Math.Max(0, main - plugs));
    }

    [Theory]
    [InlineData(460, 480)]   // 20 W over: beyond max(15 W, 13.8 W)
    [InlineData(1000, 1040)] // 40 W over: beyond 30 W
    public void Power_PlugsAboveTheMeterBeyondTheMargin_IsNegative_ShownAsZero(double main, double plugs)
    {
        var b = HouseBalance.Balance(main, plugs);
        b.Kind.Should().Be(RestKind.Negative);
        b.RestW.Should().Be(0);
        b.RawW.Should().BeNegative();
    }

    [Fact]
    public void Energy_UsesFifteenWattsTimesTheHours()
    {
        HouseBalance.EnergyMarginKwh(24).Should().BeApproximately(0.36, 1e-12);
        // A day: 11 kWh house, 11.3 kWh plugs → within 0.36 kWh.
        HouseBalance.Classify(11, 11.3, HouseBalance.EnergyMarginKwh(24)).Should().Be(RestKind.WithinMargin);
        // 3 % of 20 kWh = 0.6 kWh > 0.36 kWh.
        HouseBalance.Classify(20, 20.55, HouseBalance.EnergyMarginKwh(24)).Should().Be(RestKind.WithinMargin);
        HouseBalance.Classify(11, 11.5, HouseBalance.EnergyMarginKwh(24)).Should().Be(RestKind.Negative);
        HouseBalance.Classify(11, 5, HouseBalance.EnergyMarginKwh(24)).Should().Be(RestKind.Positive);
        // An hour: 15 Wh.
        HouseBalance.Classify(0.40, 0.39, HouseBalance.EnergyMarginKwh(1)).Should().Be(RestKind.WithinMargin);
        HouseBalance.Classify(0.40, 0.30, HouseBalance.EnergyMarginKwh(1)).Should().Be(RestKind.Positive);
    }

    private static LiveSample S(Guid id, DateTimeOffset t, double w) => new(id, t, w, null, null, null, null, null);

    private static PlugLiveState State(Guid id, PlugRole role, bool online, long powerMw) =>
        new(id, "AA", "x", online, new EmeterReading(powerMw, 0, 0, 0, null, null), T0, Role: role);

    [Fact]
    public void LastWholeMinute_AlignsMeterAndPlugAverages_NotTheLatestValues()
    {
        var main = Guid.NewGuid();
        var fridge = Guid.NewGuid();
        var kettle = Guid.NewGuid();
        var meter = Guid.NewGuid();
        var now = T0.AddMinutes(1).AddSeconds(20); // last whole minute = T0 … T0+60 s
        var samples = new Dictionary<Guid, List<LiveSample>>
        {
            // Main meter at 1 s: 450 W over the minute, a 2 kW spike only after it (not counted).
            [main] = Enumerable.Range(0, 60).Select(i => S(main, T0.AddSeconds(i), 450)).Append(S(main, T0.AddSeconds(65), 2450)).ToList(),
            // Fridge at 10 s: mean 100 W; its latest value (after the minute) is 0 W.
            [fridge] = Enumerable.Range(0, 6).Select(i => S(fridge, T0.AddSeconds(i * 10), i % 2 == 0 ? 80 : 120)).Append(S(fridge, T0.AddSeconds(70), 0)).ToList(),
        };
        var states = new[]
        {
            State(main, PlugRole.MainMeter, true, 2_450_000),
            State(fridge, PlugRole.Plug, true, 0),
            State(kettle, PlugRole.Plug, true, 340_000),     // no live sample: its polled 340 W counts
            State(meter, PlugRole.SubMeter, true, 999_000),  // sub-meters never count
        };

        var b = HouseBalance.LastWholeMinute(now, main, id => samples.TryGetValue(id, out var l) ? l : new List<LiveSample>(), states)!;

        b.IntervalStartUtc.Should().Be(T0);
        b.MainW.Should().BeApproximately(450, 1e-9);
        b.PlugsW.Should().BeApproximately(440, 1e-9);
        b.Kind.Should().Be(RestKind.WithinMargin);
    }

    [Fact]
    public void WithoutALiveStream_TheSameMinuteFromThe10sPolls()
    {
        var main = Guid.NewGuid();
        var fridge = Guid.NewGuid();
        var tv = Guid.NewGuid();
        var lamp = Guid.NewGuid();
        var polled = new Dictionary<Guid, double> { [main] = 900, [fridge] = 100 };
        var live = new Dictionary<Guid, List<LiveSample>> { [tv] = new() { S(tv, T0.AddSeconds(10), 200), S(tv, T0.AddSeconds(40), 200) } };
        var states = new[]
        {
            State(main, PlugRole.MainMeter, true, 1_500_000),
            State(fridge, PlugRole.Plug, true, 0),           // polled mean 100 W (not its latest 0 W)
            State(tv, PlugRole.Plug, true, 0),               // live mean 200 W
            State(lamp, PlugRole.Plug, true, 50_000),        // neither: latest reading 50 W
        };

        var b = HouseBalance.FromPolledMinute(T0, main, polled, id => live.TryGetValue(id, out var l) ? l : new List<LiveSample>(), states)!;

        b.FromPolls.Should().BeTrue();
        b.MainW.Should().Be(900);
        b.PlugsW.Should().BeApproximately(350, 1e-9);
        b.RestW.Should().BeApproximately(550, 1e-9);
        b.Kind.Should().Be(RestKind.Positive);
        HouseBalance.FromPolledMinute(T0, main, new Dictionary<Guid, double>(), _ => Array.Empty<LiveSample>(), states).Should().BeNull();
    }

    [Fact]
    public void LastWholeMinute_WithoutMainSamplesInThatMinute_IsNull()
    {
        var main = Guid.NewGuid();
        HouseBalance.LastWholeMinute(T0.AddMinutes(5), main, _ => new[] { S(main, T0, 400) }, Array.Empty<PlugLiveState>()).Should().BeNull();
    }
}
