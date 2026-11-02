using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.Reports;

/// <summary>
/// 0.9.2: the reports compare a period in progress with the same share of the previous one (the home page's SameFraction,
/// audit R4), and the free interval („Interval”, de la – până la) of /history.
/// </summary>
public class SameFractionAndRangeTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly Plug Main = P("Contor", PlugRole.MainMeter);
    private static readonly Plug Fridge = P("Frigider", PlugRole.Plug);

    private static Plug P(string alias, PlugRole role) => new(Guid.NewGuid(), "AA:BB", IPAddress.Loopback, alias, false, "m", "", "", "",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null, Role: role);

    private static PlugDailyKwh D(Plug p, DateOnly d, long wh) => new(p.Id, new DailyKwh(d.Year, d.Month, d.Day, wh));

    private static HouseReportInput Input(ReportPeriod p, IReadOnlyList<PlugDailyKwh> daily, DateTimeOffset now) =>
        new(p, Ro, Main, new[] { Fridge }, Array.Empty<Plug>(), daily, Array.Empty<PlugMonthWh>(), Array.Empty<PlugMonthWh>(),
            Array.Empty<PlugHourWh>(), now, new DateOnly(2026, 9, 1));

    [Fact]
    public void House_MonthInProgress_IsComparedWithTheSameShareOfTheLastMonth_NotWithAllOfIt()
    {
        // 2 November 2026, 12:00 local (10:00Z): day 2 at half; October: 10 kWh every day (310 kWh).
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 2));
        var daily = Enumerable.Range(1, 31).Select(d => D(Main, new DateOnly(2026, 10, d), 10_000))
            .Append(D(Main, new DateOnly(2026, 11, 1), 12_000)).Append(D(Main, new DateOnly(2026, 11, 2), 3_000)).ToList();

        var r = HouseReportBuilder.Build(Input(month, daily, now));

        r.IsCurrent.Should().BeTrue();
        r.PreviousHouseTotalKwh.Should().BeApproximately(310, 1e-9);
        r.PreviousSameFractionKwh.Should().BeApproximately(15, 1e-9, "1 October whole + half of 2 October");
        r.CurrentSoFarKwh.Should().BeApproximately(15, 1e-9);
        r.DeltaPct.Should().BeApproximately(0, 1e-9, "15 kWh so far against the same 15 kWh, not −95 % against all of October");
    }

    [Fact]
    public void House_PastMonth_IsComparedWithTheWholePreviousMonth()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var october = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 10, 1));
        var daily = new[] { D(Main, new DateOnly(2026, 10, 5), 20_000), D(Main, new DateOnly(2026, 9, 5), 10_000) };

        var r = HouseReportBuilder.Build(Input(october, daily, now));

        r.IsCurrent.Should().BeFalse();
        r.PreviousSameFractionKwh.Should().BeNull();
        r.DeltaPct.Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void House_PlugBuckets_ArePerPlugAndOnlyWhereTheMeterHasData()
    {
        var now = new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);
        var week = ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 10, 28));
        var daily = new[]
        {
            D(Main, new DateOnly(2026, 10, 27), 5_000), D(Fridge, new DateOnly(2026, 10, 27), 1_000),
            D(Fridge, new DateOnly(2026, 10, 28), 900),   // no meter that day
        };

        var r = HouseReportBuilder.Build(Input(week, daily, now));

        var fridge = r.PlugBuckets!.Single(p => p.PlugId == Fridge.Id);
        fridge.Kwh[1].Should().BeApproximately(1.0, 1e-9);
        fridge.Kwh[2].Should().BeNull("a plug is counted only on the days the meter has");
        fridge.Kwh.Sum(v => v ?? 0).Should().BeApproximately(r.PlugsTotalKwh!.Value, 1e-9);
    }

    private static readonly Guid Dev = Guid.NewGuid();

    private static HeatingEnergyDay GasDay(DateOnly day, double heating, double dhw) =>
        new(Dev, day, heating, dhw, 10 * heating, 10 * dhw, null, null, HeatingKinds.SourceSummary, heating, dhw);

    [Fact]
    public void Heating_WeekInProgress_ComparesWithTheSameShareOfTheLastWeek()
    {
        // Wednesday 4 November 2026, 12:00 boiler time (11:00Z): Monday, Tuesday whole + half of Wednesday.
        var boilerTz = BoilerClock.DefaultTimeZone;
        var today = new DateOnly(2026, 11, 4);
        var now = new DateTimeOffset(2026, 11, 4, 11, 0, 0, TimeSpan.Zero);
        var week = ReportPeriod.Containing(ReportGranularity.Week, today);
        var days = new List<HeatingEnergyDay>();
        for (var d = new DateOnly(2026, 10, 26); d <= new DateOnly(2026, 11, 1); d = d.AddDays(1)) days.Add(GasDay(d, 3, 1));   // 4 m³ a day
        days.Add(GasDay(new DateOnly(2026, 11, 2), 3, 1));
        days.Add(GasDay(new DateOnly(2026, 11, 3), 3, 1));
        days.Add(GasDay(today, 1.5, 0.5));

        var r = HeatingReportBuilder.Build(new HeatingReportInput(week, boilerTz, today, days, Array.Empty<HeatingMonthSums>(),
            Array.Empty<HeatingEnergyPeriod>(), Array.Empty<HeatingCounterReading>(), new DateOnly(2026, 10, 1), Ro, now));

        r.Gas.InProgress.Should().BeTrue();
        r.Gas.PreviousTotal.Should().BeApproximately(28, 1e-9);
        r.Gas.PreviousSameFraction.Should().BeApproximately(10, 1e-9, "4 + 4 + half of 4");
        r.Gas.Total.Should().BeApproximately(10, 1e-9);
        r.Gas.DeltaPct.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void Heating_DayInProgress_ComparesWithYesterdayUpToTheSameTime_FromItsReadings()
    {
        var boilerTz = BoilerClock.DefaultTimeZone;
        var today = new DateOnly(2026, 11, 4);
        var day = ReportPeriod.Containing(ReportGranularity.Day, today);
        var yesterday = day.Previous();
        // Boiler midnight (CET) = 23:00Z. Now: 06:00 boiler time (05:00Z).
        var now = day.StartUtc(boilerTz).AddHours(6);
        HeatingCounterReading R(DateTimeOffset t, double heating) => new(t, heating, 0, null, null);
        var todayReadings = new[] { R(day.StartUtc(boilerTz).AddMinutes(-5), 9), R(day.StartUtc(boilerTz).AddMinutes(20), 0.5), R(day.StartUtc(boilerTz).AddHours(5).AddMinutes(30), 2.0) };
        var ys = yesterday.StartUtc(boilerTz);
        var yesterdayReadings = new[] { R(ys.AddMinutes(-5), 7), R(ys.AddMinutes(20), 0.4), R(ys.AddHours(5).AddMinutes(30), 1.0), R(ys.AddHours(20), 9) };

        var r = HeatingReportBuilder.Build(new HeatingReportInput(day, boilerTz, today, Array.Empty<HeatingEnergyDay>(), Array.Empty<HeatingMonthSums>(),
            Array.Empty<HeatingEnergyPeriod>(), todayReadings, new DateOnly(2026, 10, 1), Ro, now, yesterdayReadings));

        r.Gas.InProgress.Should().BeTrue();
        r.Gas.Total.Should().BeApproximately(2.0, 1e-9);
        r.Gas.PreviousSameFraction.Should().BeApproximately(1.0, 1e-9, "yesterday up to 06:00 boiler time, not its 9 m³");
        r.Gas.DeltaPct.Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void Range_HasDayBars_ItsPreviousIsTheSameLengthBefore_AndItIsOrderedAndCapped()
    {
        var r = ReportPeriod.Between(new DateOnly(2026, 10, 20), new DateOnly(2026, 10, 1));
        r.Kind.Should().Be(ReportGranularity.Range);
        r.Start.Should().Be(new DateOnly(2026, 10, 1));
        r.LastDay.Should().Be(new DateOnly(2026, 10, 20));
        r.DayCount.Should().Be(20);
        r.Buckets(Ro).Should().HaveCount(20).And.OnlyContain(b => b.EndUtc > b.StartUtc);
        r.Buckets(Ro)[19].Day.Should().Be(new DateOnly(2026, 10, 20));
        r.Previous().Should().Be(ReportPeriod.Between(new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 30)));
        r.Next().Should().Be(ReportPeriod.Between(new DateOnly(2026, 10, 21), new DateOnly(2026, 11, 9)));
        r.BucketOfDay(new DateOnly(2026, 10, 5)).Should().Be(4);
        ReportPeriod.Between(new DateOnly(2020, 1, 1), new DateOnly(2026, 1, 1)).DayCount.Should().Be(ReportPeriod.MaxRangeDays);
        // The autumn DST day (25 October 2026) is one 25-hour bar.
        var dst = ReportPeriod.Between(new DateOnly(2026, 10, 24), new DateOnly(2026, 10, 26)).Buckets(Ro);
        (dst[1].EndUtc - dst[1].StartUtc).TotalHours.Should().Be(25);
    }
}
