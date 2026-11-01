using FluentAssertions;
using KasaMonitor.Core.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Heating;

public class HeatingEnergyTests
{
    // The real zone of the QNAP (TZ=Europe/Bucharest): EET/EEST, so the DST days (23 h / 25 h) are covered.
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly Guid Dev = Guid.Parse("00000000-0000-0000-0000-0000000000b0");

    [Fact]
    public void CubicMeters_ToKwh_WithTheFactor()
    {
        HeatingEnergy.ToKwh(1.3, EnergyUnit.CubicMeter, 10.55).Should().BeApproximately(13.715, 1e-9);
        HeatingEnergy.ToKwh(13.715, EnergyUnit.KilowattHour, 10.55).Should().Be(13.715);
        HeatingEnergy.ToM3(21.1, EnergyUnit.KilowattHour, 10.55).Should().BeApproximately(2.0, 1e-9);
        HeatingEnergy.ToM3(2.0, EnergyUnit.CubicMeter, 10.55).Should().Be(2.0);
    }

    [Fact]
    public void UnknownUnit_FallsBackToTheConfiguredOne()
    {
        HeatingEnergy.ToKwh(2, EnergyUnit.Unknown, 10, EnergyUnit.CubicMeter).Should().Be(20);
        HeatingEnergy.ToKwh(2, EnergyUnit.Unknown, 10, EnergyUnit.KilowattHour).Should().Be(2);
        HeatingEnergy.ToM3(20, EnergyUnit.Unknown, 10, EnergyUnit.KilowattHour).Should().Be(2);
    }

    [Fact]
    public void DayOf_IsTheLocalDayOfOurReading_InBucharest()
    {
        // Winter (EET, UTC+2): 2 Nov starts at 1 Nov 22:00 UTC.
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 11, 1, 21, 59, 59, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 11, 1));
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 11, 2));
        // Summer (EEST, UTC+3): 1 Aug starts at 31 Jul 21:00 UTC.
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 7, 31, 20, 59, 59, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 7, 31));
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 7, 31, 21, 0, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 8, 1));
    }

    [Fact]
    public void DayOf_AcrossTheDstChanges_25hAnd23hDays()
    {
        // 25 Oct 2026 (25 h): starts 24 Oct 21:00 UTC (00:00 EEST), ends 25 Oct 22:00 UTC (00:00 EET).
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 10, 24, 20, 59, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 10, 24));
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 10, 24, 21, 30, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 10, 25));   // 00:30 local
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 10, 25));    // 03:30 local (after the change)
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 10, 25, 21, 59, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 10, 25));
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 10, 26));
        // 29 Mar 2026 (23 h): starts 28 Mar 22:00 UTC (00:00 EET), ends 29 Mar 21:00 UTC (00:00 EEST).
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 3, 28, 22, 0, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 3, 29));
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 3, 29, 20, 59, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 3, 29));
        HeatingEnergy.DayOf(new DateTimeOffset(2026, 3, 29, 21, 0, 0, TimeSpan.Zero), Ro).Should().Be(new DateOnly(2026, 3, 30));
    }

    [Fact]
    public void DailyCallCounter_CountsTheLocalCalendarDay()
    {
        var now = new DateTimeOffset(2026, 10, 25, 21, 50, 0, TimeSpan.Zero);   // 23:50 EET, 25 Oct
        var counter = new DailyCallCounter(() => now, Ro);
        counter.Record();
        counter.Record();
        counter.Today.Should().Be(2);
        now = now.AddMinutes(15);                                               // 00:05, 26 Oct
        counter.Today.Should().Be(0);
        counter.Record();
        counter.Today.Should().Be(1);
    }

    [Fact]
    public void SeriesDays_Day0IsToday()
    {
        var days = HeatingEnergy.SeriesDays(new[] { 1.0, 2.0, 3.0 }, new DateTimeOffset(2026, 11, 1, 9, 0, 0, TimeSpan.Zero), Ro).ToList();
        days.Should().Equal((new DateOnly(2026, 11, 1), 1.0), (new DateOnly(2026, 10, 31), 2.0), (new DateOnly(2026, 10, 30), 3.0));
    }

    [Fact]
    public void Efficiency_IsHeatOverGasEnergy()
    {
        HeatingEnergy.Efficiency(146.1, 14.6 * 10.55).Should().BeApproximately(0.9485, 1e-3);
        HeatingEnergy.Efficiency(10, 0).Should().BeNull();
        HeatingEnergy.Efficiency(null, 5).Should().BeNull();
    }

    [Fact]
    public void B0ka_OneDailyRowForTheFeatureDay_GasInM3()
    {
        var read = new DateTimeOffset(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);
        var s = HeatingFeatureParser.Parse(VicareFixtures.B0ka, read);

        var days = HeatingEnergyBuilder.Days(Dev, s, Ro, 10.55, EnergyUnit.CubicMeter);

        days.Should().ContainSingle();
        var d = days[0];
        d.Day.Should().Be(new DateOnly(2026, 11, 1));
        d.GasHeatingMaxM3.Should().Be(1.3);
        d.GasDhwMaxM3.Should().Be(0.1);
        d.HeatHeatingKwh.Should().Be(14);
        d.HeatDhwKwh.Should().Be(1);
        d.PowerHeatingKwh.Should().Be(0.3);
        d.PowerDhwKwh.Should().Be(0);
        d.Source.Should().Be(HeatingKinds.SourceSummary);
        d.GasTotalM3(d.Day).Should().BeApproximately(1.4, 1e-9);
    }

    [Fact]
    public void B0ka_Periods_ForEveryKindPurposeAndPeriod()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.B0ka, new DateTimeOffset(2026, 11, 1, 16, 0, 0, TimeSpan.Zero));

        var periods = HeatingEnergyBuilder.Periods(Dev, s, 10.55, EnergyUnit.CubicMeter);

        periods.Should().HaveCount(3 * 2 * 6);
        periods.Single(p => p is { Kind: "gas", Purpose: "dhw", Period: "lastmonth" }).Value.Should().Be(14.8);
        periods.Single(p => p is { Kind: "gas", Purpose: "heating", Period: "year" }).Should()
            .Match<HeatingEnergyPeriod>(p => p.Value == 368.8 && p.Unit == "m3");
        periods.Single(p => p is { Kind: "heat", Purpose: "heating", Period: "lastyear" }).Should()
            .Match<HeatingEnergyPeriod>(p => p.Value == 2957.5 && p.Unit == "kWh");
    }

    [Fact]
    public void Series_FillEveryDay_KwhConvertedToM3()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.Synthetic, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero));

        var days = HeatingEnergyBuilder.Days(Dev, s, Ro, 10.0, EnergyUnit.CubicMeter);

        days.Should().HaveCount(8);
        days.Select(d => d.Day).Should().BeInAscendingOrder();
        var today = days.Single(d => d.Day == new DateOnly(2026, 11, 1));
        today.GasHeatingMaxM3.Should().BeApproximately(1.2, 1e-9);          // day[0] = 12 kWh / 10
        today.GasDhwMaxM3.Should().BeApproximately(0.2, 1e-9);
        today.Source.Should().Be(HeatingKinds.SourceSeries);
        var yesterday = days.Single(d => d.Day == new DateOnly(2026, 10, 31));
        yesterday.GasHeatingMaxM3.Should().BeApproximately(3.05, 1e-9);
        yesterday.GasDhwMaxM3.Should().BeApproximately(0.3, 1e-9);
        days.Single(d => d.Day == new DateOnly(2026, 10, 25)).GasDhwMaxM3.Should().BeNull();
    }
}
