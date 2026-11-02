using FluentAssertions;
using KasaMonitor.Core.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Heating;

public class BoilerClockTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly TimeZoneInfo Cet = BoilerClock.DefaultTimeZone;
    private static readonly Guid Dev = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    /// <summary>A Romanian local time (EET/EEST) as UTC.</summary>
    private static DateTimeOffset RoTime(int y, int mo, int d, int h, int mi)
    {
        var local = new DateTime(y, mo, d, h, mi, 0);
        return new DateTimeOffset(local, Ro.GetUtcOffset(local));
    }

    [Fact]
    public void DefaultZone_IsEuropeBerlin_UnknownIdsFallBack()
    {
        BoilerClock.DefaultTimeZoneId.Should().Be("Europe/Berlin");
        BoilerClock.TryResolve("Europe/Bucharest", out var ro).Should().BeTrue();
        ro.BaseUtcOffset.Should().Be(TimeSpan.FromHours(2));
        BoilerClock.TryResolve("Mars/Olympus", out _).Should().BeFalse();
        BoilerClock.TryResolve("", out _).Should().BeFalse();
        BoilerClock.Resolve("Mars/Olympus").BaseUtcOffset.Should().Be(TimeSpan.FromHours(1));
    }

    [Theory]
    // November (CET vs EET): the boiler's day ends at 01:00 Romanian time.
    [InlineData(2026, 11, 1, 23, 30, "2026-11-01", "2026-11-01")]  // 22:30 CET: same day for both
    [InlineData(2026, 11, 2, 0, 30, "2026-11-01", "2026-11-02")]   // 23:30 CET: still the boiler's previous day
    [InlineData(2026, 11, 2, 1, 5, "2026-11-02", "2026-11-02")]    // 00:05 CET: the boiler's new day
    // July (CEST vs EEST): the difference stays one hour.
    [InlineData(2026, 7, 15, 0, 30, "2026-07-14", "2026-07-15")]
    [InlineData(2026, 7, 15, 1, 5, "2026-07-15", "2026-07-15")]
    public void Day_InTheBoilerZone_VersusBucharest(int y, int mo, int d, int h, int mi, string boilerDay, string localDay)
    {
        var utc = RoTime(y, mo, d, h, mi);
        BoilerClock.DayOf(utc, Cet).Should().Be(DateOnly.Parse(boilerDay));
        HeatingEnergy.DayOf(utc, Ro).Should().Be(DateOnly.Parse(localDay));
    }

    [Theory]
    [InlineData(2026, 11, 2)]
    [InlineData(2026, 7, 15)]
    [InlineData(2026, 3, 30)]   // the day after the spring change
    [InlineData(2026, 10, 26)]  // the day after the autumn change
    public void BoilerMidnight_IsOneOClockRomanianTime_AllYear(int y, int mo, int d)
    {
        var midnight = BoilerClock.MidnightUtc(new DateOnly(y, mo, d), Cet);
        TimeZoneInfo.ConvertTime(midnight, Ro).DateTime.Should().Be(new DateTime(y, mo, d, 1, 0, 0));
    }

    [Fact]
    public void Stale_YesterdaysTotalInTheFirst20MinutesAfterTheBoilerMidnight()
    {
        var at0001 = RoTime(2026, 11, 2, 1, 1);   // 00:01 CET
        BoilerClock.IsStale(1.4, 1.4, 1.4, at0001, Cet).Should().BeTrue("yesterday's total again");
        BoilerClock.IsStale(1.3, 1.4, 1.4, at0001, Cet).Should().BeTrue("≥ 90 % of yesterday's highest");
        BoilerClock.IsStale(0.5, 1.4, 0.4, at0001, Cet).Should().BeTrue("not lower than yesterday's last value");
        BoilerClock.IsStale(0.0, 1.4, 1.4, at0001, Cet).Should().BeFalse("the reset");
        BoilerClock.IsStale(1.2, 1.4, 1.4, at0001, Cet).Should().BeFalse("under 90 %");
        BoilerClock.IsStale(1.4, null, null, at0001, Cet).Should().BeFalse("nothing stored for yesterday");
        BoilerClock.IsStale(1.4, 1.4, 1.4, RoTime(2026, 11, 2, 1, 30), Cet).Should().BeTrue("exactly 30 minutes (the end of the reset window)");
        BoilerClock.IsStale(1.4, 1.4, 1.4, RoTime(2026, 11, 2, 1, 31), Cet).Should().BeFalse("31 minutes after the boiler's midnight");
        // After a day of about zero nothing is stale: a new small value is today's, never counted twice.
        BoilerClock.IsStale(0.2, 0.0, 0.0, RoTime(2026, 11, 2, 1, 15), Cet).Should().BeFalse("yesterday 0, a shower at 00:15 CET");
        BoilerClock.IsStale(0.2, 0.04, 0.04, RoTime(2026, 11, 2, 1, 15), Cet).Should().BeFalse("yesterday under 0.05 m3");
        // Heat / electricity: the same rule with their own thresholds (kWh).
        BoilerClock.IsStale(14, 14, null, at0001, Cet, BoilerClock.StaleMinPreviousHeatKwh).Should().BeTrue();
        BoilerClock.IsStale(0.4, 0.4, null, at0001, Cet, BoilerClock.StaleMinPreviousHeatKwh).Should().BeFalse("under 0.5 kWh of heat yesterday");
        BoilerClock.IsStale(0.3, 0.3, null, at0001, Cet, BoilerClock.StaleMinPreviousPowerKwh).Should().BeTrue();
        BoilerClock.IsStale(1.4, 1.4, 1.4, RoTime(2026, 7, 15, 1, 5), Cet).Should().BeTrue("summer: 00:05 CEST");
    }

    [Fact]
    public void Reset_At0104Romanian_IsInTheBoilerWindow()
    {
        var at = RoTime(2026, 11, 2, 1, 4);   // 00:04 CET
        BoilerClock.InResetWindow(at, Cet).Should().BeTrue();
        BoilerClock.IsReset(1.4, 0.0, at, Cet, lastResetUtc: null).Should().BeTrue();
        // Even with a reset a few minutes before (the window has no gap rule).
        BoilerClock.IsReset(0.2, 0.1, at, Cet, at.AddMinutes(-2)).Should().BeTrue();
        // The same instant judged in the Bucharest zone is outside the window (0.8.0's mistake).
        BoilerClock.InResetWindow(at, Ro).Should().BeFalse();
    }

    [Fact]
    public void Reset_At1400_IsRefused_UnlessUnder20Percent_OnceIn20Hours()
    {
        var at = RoTime(2026, 11, 2, 14, 0);
        BoilerClock.IsReset(1.4, 1.2, at, Cet, null).Should().BeFalse("1.2 is not under 20 % of 1.4");
        BoilerClock.IsReset(1.4, 0.2, at, Cet, null).Should().BeTrue("0.2 < 0.28 and no reset before");
        BoilerClock.IsReset(1.4, 0.2, at, Cet, at.AddHours(-13)).Should().BeFalse("a reset 13 h before (at 01:00)");
        BoilerClock.IsReset(1.4, 0.2, at, Cet, at.AddHours(-20)).Should().BeTrue("20 h after the last reset");
        BoilerClock.IsReset(1.4, 1.4, at, Cet, null).Should().BeFalse("not a drop");
    }

    [Theory]
    [InlineData(23, 44, false)]
    [InlineData(23, 45, true)]
    [InlineData(0, 30, true)]
    [InlineData(0, 31, false)]
    public void Window_IsInclusive_InBoilerTime(int h, int mi, bool inside)
    {
        var local = new DateTime(2026, 11, 2, h, mi, 0);
        BoilerClock.InResetWindow(new DateTimeOffset(local, Cet.GetUtcOffset(local)), Cet).Should().Be(inside);
    }

    [Fact]
    public void Classify_ResetBeforeTheBoilerMidnight_GoesToTheNextDay_AfterIt_CarriesTheStoredValueBack()
    {
        var day = new DateOnly(2026, 11, 1);
        var before = RoTime(2026, 11, 2, 0, 50);   // 23:50 CET 1 Nov
        BoilerClock.Classify(0.0, 1.4, day, before, Cet).Should().Be(new GasDayDecision(GasDayAction.Reset, day.AddDays(1), false));

        var after = RoTime(2026, 11, 2, 1, 4);     // 00:04 CET 2 Nov
        BoilerClock.Classify(0.0, 1.4, day.AddDays(1), after, Cet).Should().Be(new GasDayDecision(GasDayAction.Reset, day.AddDays(1), true));

        var noon = RoTime(2026, 11, 2, 14, 0);
        // During the day even a drop under 20 % leaves the day as it is (the hourly profile alone may count it as a reset).
        BoilerClock.Classify(0.1, 1.4, day.AddDays(1), noon, Cet).Action.Should().Be(GasDayAction.IgnoredDecrease);
        BoilerClock.Classify(1.2, 1.4, day.AddDays(1), noon, Cet).Action.Should().Be(GasDayAction.IgnoredDecrease);
        BoilerClock.Classify(1.5, 1.4, day.AddDays(1), noon, Cet).Action.Should().Be(GasDayAction.Upsert);
        BoilerClock.Classify(0.0, null, day.AddDays(1), noon, Cet).Action.Should().Be(GasDayAction.Upsert);
    }

    [Fact]
    public void Repair_TodaysCase_TheDayHoldingYesterdaysTotalIsSplitBack()
    {
        // Measured on 2026-11-02 (0.8.0): 1 Nov 1.4 / 1.4 heating, 0.1 hot water; 2 Nov highest 1.4 (yesterday's total
        // read between 00:00 and 01:00 Romanian time), last 0.7.
        var yesterday = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 1), 1.4, 0.1, null, null, null, null, HeatingKinds.SourceSummary, 1.4, 0.1);
        var today = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 2), 1.4, 0.1, null, null, null, null, HeatingKinds.SourceSummary, 0.7, 0.0);

        var fixes = BoilerClock.RepairCurrentDay(yesterday, today, RoTime(2026, 11, 2, 10, 0), Cet);

        fixes.Should().HaveCount(2);
        var heating = fixes.Single(f => f.Purpose == HeatingKinds.Heating);
        (heating.NewPreviousMaxM3, heating.NewPreviousLastM3, heating.CurrentLastM3).Should().Be((1.4, 1.4, 0.7));
        var dhw = fixes.Single(f => f.Purpose == HeatingKinds.Dhw);
        (dhw.NewPreviousMaxM3, dhw.NewPreviousLastM3, dhw.CurrentLastM3).Should().Be((0.1, 0.1, 0.0));
    }

    [Fact]
    public void Repair_RaisesThePreviousDay_AndWaits30MinutesAfterTheBoilerMidnight()
    {
        var yesterday = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 1), 1.3, null, null, null, null, null, HeatingKinds.SourceSummary, 1.2, null);
        var today = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 2), 1.4, null, null, null, null, null, HeatingKinds.SourceSummary, 0.1, null);

        BoilerClock.RepairCurrentDay(yesterday, today, RoTime(2026, 11, 2, 1, 29), Cet).Should().BeEmpty("only 29 minutes after 00:00 CET");
        var fix = BoilerClock.RepairCurrentDay(yesterday, today, RoTime(2026, 11, 2, 1, 31), Cet).Should().ContainSingle().Subject;
        (fix.NewPreviousMaxM3, fix.NewPreviousLastM3).Should().Be((1.4, 1.4));

        // Last over 50 % of the highest, or no drop at all: nothing to repair.
        BoilerClock.RepairCurrentDay(yesterday, today with { GasHeatingLastM3 = 0.71 }, RoTime(2026, 11, 2, 10, 0), Cet).Should().BeEmpty();
        BoilerClock.RepairCurrentDay(yesterday, today with { GasHeatingLastM3 = 1.4 }, RoTime(2026, 11, 2, 10, 0), Cet).Should().BeEmpty();
        // A stored day that is not today's boiler day is left alone.
        BoilerClock.RepairCurrentDay(null, yesterday with { GasHeatingLastM3 = 0.1 }, RoTime(2026, 11, 2, 10, 0), Cet).Should().BeEmpty();
    }

    [Fact]
    public void Repair_DeploymentCase_TodaysHighestIsYesterdaysTotal_EvenAboveHalf()
    {
        // At the deployment (~10:30): 1 Nov 1.4 / 1.4, 2 Nov highest 1.4 (yesterday's) and last 0.8 (> 50 %).
        var yesterday = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 1), 1.4, 0.1, null, null, null, null, HeatingKinds.SourceSummary, 1.4, 0.1);
        var today = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 2), 1.4, 0.1, null, null, null, null, HeatingKinds.SourceSummary, 0.8, 0.1);
        var at = RoTime(2026, 11, 2, 10, 30);

        var fix = BoilerClock.RepairCurrentDay(yesterday, today, at, Cet).Should().ContainSingle().Subject;
        (fix.Purpose, fix.NewPreviousMaxM3, fix.NewPreviousLastM3, fix.CurrentLastM3).Should().Be((HeatingKinds.Heating, 1.4, 1.4, 0.8));

        // Tolerance max(0.05, 1 %): 1.44 is still yesterday's 1.4; 1.5 is not.
        BoilerClock.RepairCurrentDay(yesterday, today with { GasHeatingMaxM3 = 1.44 }, at, Cet).Should().ContainSingle();
        BoilerClock.RepairCurrentDay(yesterday, today with { GasHeatingMaxM3 = 1.5, GasHeatingLastM3 = 0.9 }, at, Cet).Should().BeEmpty();
        // A day with highest = last is never touched; a legitimate drop above yesterday's total neither.
        BoilerClock.RepairCurrentDay(yesterday, today with { GasHeatingMaxM3 = 2.0, GasHeatingLastM3 = 2.0 }, at, Cet).Should().BeEmpty();
        BoilerClock.RepairCurrentDay(yesterday, today with { GasHeatingMaxM3 = 2.0, GasHeatingLastM3 = 1.2 }, at, Cet).Should().BeEmpty();
        // Without yesterday only the 50 % rule applies.
        BoilerClock.RepairCurrentDay(null, today, at, Cet).Should().BeEmpty();
    }

    [Fact]
    public void RepairKwh_HeatAndElectricityEqualToYesterdayAreYesterdays()
    {
        var yesterday = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 1), 1.4, 0.1, 14.0, 1.0, 0.30, 0.02, HeatingKinds.SourceSummary, 1.4, 0.1);
        var today = new HeatingEnergyDay(Dev, new DateOnly(2026, 11, 2), 1.4, 0.1, 14.1, 1.0, 0.40, null, HeatingKinds.SourceSummary, 0.8, 0.1);

        var fixes = BoilerClock.RepairCurrentDayKwh(yesterday, today);

        fixes.Select(f => (f.Kind, f.Purpose, f.NewPreviousKwh)).Should().BeEquivalentTo(new[]
        {
            (HeatingKinds.Heat, HeatingKinds.Heating, 14.1),   // within 1 % of 14
            (HeatingKinds.Heat, HeatingKinds.Dhw, 1.0),
        }, "0.40 kWh is today's own electricity (not 0.30), the dhw electricity is missing today");
        BoilerClock.RepairCurrentDayKwh(null, today).Should().BeEmpty();
    }

    [Fact]
    public void CarryToPrevious_KeepsAHigherPreviousValue()
    {
        BoilerClock.CarryToPrevious(1.3, 1.2, 1.4).Should().Be((1.4, 1.4));
        BoilerClock.CarryToPrevious(1.6, 1.6, 1.4).Should().Be((1.6, 1.6));
        BoilerClock.CarryToPrevious(null, null, 1.4).Should().Be((1.4, 1.4));
    }
}
