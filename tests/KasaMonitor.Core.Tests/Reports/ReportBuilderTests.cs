using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.Reports;

public class HouseReportBuilderTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly Plug Main = P("Contor", PlugRole.MainMeter);
    private static readonly Plug Fridge = P("Frigider", PlugRole.Plug);
    private static readonly Plug Tv = P("TV", PlugRole.Plug);
    private static readonly Plug Clamp = P("Clemă 2", PlugRole.SubMeter);

    private static Plug P(string alias, PlugRole role) => new(Guid.NewGuid(), "AA:BB", IPAddress.Loopback, alias, false, "m", "", "", "",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null, Role: role);

    private static PlugDailyKwh D(Plug p, string day, long wh)
    {
        var d = DateOnly.Parse(day);
        return new PlugDailyKwh(p.Id, new DailyKwh(d.Year, d.Month, d.Day, wh));
    }

    // Now: 01.11.2026 20:00 local.
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 18, 0, 0, TimeSpan.Zero);

    private static HouseReportInput Input(ReportPeriod p, IReadOnlyList<PlugDailyKwh>? daily = null, IReadOnlyList<PlugHourWh>? live = null,
        DateTimeOffset? now = null, IReadOnlyList<PlugMonthWh>? monthly = null, IReadOnlyList<PlugMonthWh>? byMonth = null) =>
        new(p, Ro, Main, new[] { Fridge, Tv }, new[] { Clamp }, daily ?? Array.Empty<PlugDailyKwh>(), monthly ?? Array.Empty<PlugMonthWh>(),
            byMonth ?? Array.Empty<PlugMonthWh>(), live ?? Array.Empty<PlugHourWh>(), now ?? Now, new DateOnly(2026, 10, 28));

    [Fact]
    public void Week_SplitsHouseIntoPlugsAndRest_MissingDaysAreNull_PreviousWeekCompared()
    {
        var week = ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 1)); // 26.10–01.11
        var daily = new[]
        {
            D(Main, "2026-10-30", 10_000), D(Fridge, "2026-10-30", 1_000), D(Tv, "2026-10-30", 500), D(Clamp, "2026-10-30", 2_000),
            D(Main, "2026-10-31", 8_000), D(Fridge, "2026-10-31", 1_200),
            // Plugs above the meter (clock skew, rounding): rest clamped to 0.
            D(Main, "2026-11-01", 1_000), D(Fridge, "2026-11-01", 1_500),
            D(Main, "2026-10-20", 7_000), D(Main, "2026-10-21", 3_000), // previous week
        };

        var r = HouseReportBuilder.Build(Input(week, daily));

        r.Buckets.Should().HaveCount(7);
        r.HouseKwh.Take(4).Should().OnlyContain(v => v == null);     // Mon–Thu: no data
        r.PlugsKwh[4].Should().BeApproximately(1.5, 1e-9);
        r.RestKwh[4].Should().BeApproximately(8.5, 1e-9);
        r.RestKwh[5].Should().BeApproximately(6.8, 1e-9);
        r.RestKwh[6].Should().Be(0);
        r.RestClamped.Should().BeTrue();
        r.HouseTotalKwh.Should().BeApproximately(19, 1e-9);
        r.PlugsTotalKwh.Should().BeApproximately(4.2, 1e-9);
        r.RestTotalKwh.Should().BeApproximately(14.8, 1e-9);   // max(0, 19 − 4.2), not Σ of the clamped days
        r.PreviousHouseTotalKwh.Should().BeApproximately(10, 1e-9);
        r.DeltaPct.Should().BeApproximately(90, 1e-9);
        r.SubMeters.Should().ContainSingle().Which.Kwh.Should().BeApproximately(2, 1e-9);
    }

    [Fact]
    public void Rest_IsMarkedPositive_WithinTheMargin_OrNegativeBeyondIt()
    {
        var week = ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 1));
        var daily = new[]
        {
            D(Main, "2026-10-26", 10_000), D(Fridge, "2026-10-26", 2_000),   // clearly positive
            D(Main, "2026-10-27", 11_000), D(Fridge, "2026-10-27", 11_300),  // 0.3 kWh over: within 15 W × 24 h = 0.36 kWh
            D(Main, "2026-10-28", 11_000), D(Fridge, "2026-10-28", 11_500),  // 0.5 kWh over: beyond the margin
        };

        var r = HouseReportBuilder.Build(Input(week, daily));

        r.RestKinds[0].Should().Be(KasaMonitor.Core.Metering.RestKind.Positive);
        r.RestKwh[0].Should().BeApproximately(8, 1e-9);
        r.RestKinds[1].Should().Be(KasaMonitor.Core.Metering.RestKind.WithinMargin);
        r.RestKwh[1].Should().Be(0);
        r.RestKinds[2].Should().Be(KasaMonitor.Core.Metering.RestKind.Negative);
        r.RestKwh[2].Should().Be(0);
        r.RestKinds[3].Should().BeNull();
        r.RestClamped.Should().BeTrue();
        r.RestWithinMargin.Should().BeTrue();
        r.RestTotalKind.Should().Be(KasaMonitor.Core.Metering.RestKind.Positive);
    }

    [Fact]
    public void PastDay_IsTheLargerOfDeviceMemoryAndLiveSum_PartialCoverageIsNotAClampWarning()
    {
        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 20));
        var now = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        // 01.11: the meter's memory kept 252 minutes (1 kWh), the stream saw 5 h (1.5 kWh); the plugs' memory has the whole day (3 kWh).
        var h0 = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);
        var live = Enumerable.Range(15, 5).Select(i => new PlugHourWh(Main.Id, h0.AddHours(i), 300, 60)).ToList();
        // 02.11: the stream covered the whole day (24 × 60 minutes), the plugs exceed the meter → a real clamp warning.
        live.AddRange(Enumerable.Range(24, 24).Select(i => new PlugHourWh(Main.Id, h0.AddHours(i), 100, 60)));
        var daily = new[]
        {
            D(Main, "2026-11-01", 1_000), D(Fridge, "2026-11-01", 3_000),
            D(Main, "2026-11-02", 2_000), D(Fridge, "2026-11-02", 3_000),
        };

        var r = HouseReportBuilder.Build(Input(month, daily, live, now));

        r.HouseKwh[0].Should().BeApproximately(1.5, 1e-9);                        // max(1 kWh memory, 1.5 kWh live)
        r.PartialBuckets![0].Should().BeTrue();                                   // 300 of 1440 minutes
        r.RestKinds[0].Should().Be(KasaMonitor.Core.Metering.RestKind.PartialData);
        r.HouseKwh[1].Should().BeApproximately(2.4, 1e-9);                        // max(2 kWh memory, 2.4 kWh live)
        r.PartialBuckets[1].Should().BeFalse();
        r.RestKinds[1].Should().Be(KasaMonitor.Core.Metering.RestKind.Negative);
        r.RestPartial.Should().BeTrue();
        r.RestClamped.Should().BeTrue();
        r.PartialBuckets[5].Should().BeFalse();                                   // no live minute: coverage unknown, not partial
    }

    [Fact]
    public void Coverage_OfTheBucketInProgress_CountsOnlyItsElapsedPart()
    {
        var week = ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 11, 1));
        // Today until 20:00 local: 20 h elapsed, the stream covered all of them.
        var h0 = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero);
        var live = Enumerable.Range(0, 20).Select(i => new PlugHourWh(Main.Id, h0.AddHours(i), 100, 60)).ToList();
        var r = HouseReportBuilder.Build(Input(week, live: live));
        r.PartialBuckets![6].Should().BeFalse();
        r.IsCurrent.Should().BeTrue();
    }

    [Fact]
    public void DayWithoutHours_WhenOnlyTheStoredDayExists()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 10, 30));
        var r = HouseReportBuilder.Build(Input(day, new[] { D(Main, "2026-10-30", 12_300) }));
        r.HouseTotalKwh.Should().BeApproximately(12.3, 1e-9);
        r.DayWithoutHours.Should().BeTrue();
        r.IsCurrent.Should().BeFalse();
    }

    [Fact]
    public void RestTotal_MarginCoversOnlyTheBucketsWithData()
    {
        // A month with a single stored day: 0.9 kWh rest is above that day's 0.36 kWh margin (not the month's 10.8 kWh).
        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));
        var r = HouseReportBuilder.Build(Input(month, new[] { D(Main, "2026-11-01", 2_000), D(Fridge, "2026-11-01", 1_100) }));
        r.RestTotalKwh.Should().BeApproximately(0.9, 1e-9);
        r.RestTotalKind.Should().Be(KasaMonitor.Core.Metering.RestKind.Positive);
        r.RestWithinMargin.Should().BeFalse();
    }

    [Fact]
    public void WithoutAnyData_TotalsAreNull_NoComparison()
    {
        var r = HouseReportBuilder.Build(Input(ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1))));
        r.HouseKwh.Should().OnlyContain(v => v == null);
        r.HouseTotalKwh.Should().BeNull();
        r.PreviousHouseTotalKwh.Should().BeNull();
        r.DeltaPct.Should().BeNull();
        r.SubMeters.Single().Kwh.Should().BeNull();
    }

    [Fact]
    public void Day_HoursFromTheLiveStream_TotalIsTheLargerOfStoredDayAndLiveSum()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 1));
        var h0 = new DateTimeOffset(2026, 10, 31, 22, 0, 0, TimeSpan.Zero); // 00:00 local
        var live = new[]
        {
            new PlugHourWh(Main.Id, h0, 400), new PlugHourWh(Fridge.Id, h0, 100),
            new PlugHourWh(Main.Id, h0.AddHours(18), 900), new PlugHourWh(Tv.Id, h0.AddHours(18), 150), new PlugHourWh(Fridge.Id, h0.AddHours(18), 50),
            new PlugHourWh(Main.Id, h0.AddHours(-2), 300), // 22:00 the day before
        };
        // The device's day row lags (synced every 6 h): 1 kWh stored, 1.3 kWh seen live → 1.3.
        var daily = new[] { D(Main, "2026-11-01", 1_000), D(Main, "2026-10-31", 5_000) };

        var r = HouseReportBuilder.Build(Input(day, daily, live));

        r.Buckets.Should().HaveCount(24);
        r.HouseKwh[0].Should().BeApproximately(0.4, 1e-9);
        r.PlugsKwh[0].Should().BeApproximately(0.1, 1e-9);
        r.RestKwh[0].Should().BeApproximately(0.3, 1e-9);
        r.HouseKwh[18].Should().BeApproximately(0.9, 1e-9);
        r.PlugsKwh[18].Should().BeApproximately(0.2, 1e-9);
        r.HouseKwh[5].Should().BeNull();
        r.HouseTotalKwh.Should().BeApproximately(1.3, 1e-9);
        r.PlugsTotalKwh.Should().BeApproximately(0.3, 1e-9);
        r.RestTotalKwh.Should().BeApproximately(1.0, 1e-9);
        r.PreviousHouseTotalKwh.Should().BeApproximately(5.0, 1e-9); // stored 5 kWh > live 0.3 kWh
        r.DeltaPct.Should().BeApproximately(-74, 1e-9);
    }

    [Fact]
    public void Day_OnTheAutumnDstDay_Has25Hours_TheRepeatedHourKeepsItsOwnValue()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 10, 25));
        var first3 = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);  // 03:00 EEST
        var second3 = new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero); // 03:00 EET
        var live = new[] { new PlugHourWh(Main.Id, first3, 200), new PlugHourWh(Main.Id, second3, 300) };

        var r = HouseReportBuilder.Build(Input(day, live: live));

        r.Buckets.Should().HaveCount(25);
        var i1 = r.Buckets.First(b => b.LocalStart.Hour == 3).Index;
        var i2 = r.Buckets.Single(b => b.RepeatedHour).Index;
        r.HouseKwh[i1].Should().BeApproximately(0.2, 1e-9);
        r.HouseKwh[i2].Should().BeApproximately(0.3, 1e-9);
        r.HouseTotalKwh.Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Year_MonthIsTheLargerOfDeviceMonthAndSumOfDays_TodayRaisedByLive()
    {
        var year = ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 11, 1));
        var today = new DateOnly(2026, 11, 1);
        var monthly = new[] { new PlugMonthWh(Main.Id, 2026, 10, 300_000), new PlugMonthWh(Fridge.Id, 2026, 10, 30_000), new PlugMonthWh(Main.Id, 2025, 5, 100_000) };
        var byMonth = new[] { new PlugMonthWh(Main.Id, 2026, 10, 250_000), new PlugMonthWh(Fridge.Id, 2026, 10, 40_000), new PlugMonthWh(Main.Id, 2026, 11, 2_000) };
        var daily = new[] { D(Main, "2026-11-01", 2_000) };
        var live = new[] { new PlugHourWh(Main.Id, new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero), 3_500) };

        var r = HouseReportBuilder.Build(Input(year, daily, live, null, monthly, byMonth));

        r.HouseKwh[9].Should().BeApproximately(300, 1e-9);   // device month 300 > days 250
        r.PlugsKwh[9].Should().BeApproximately(40, 1e-9);    // days 40 > device 30
        r.HouseKwh[10].Should().BeApproximately(3.5, 1e-9);  // today: live 3.5 kWh > stored 2 kWh
        r.HouseKwh[0].Should().BeNull();
        r.HouseTotalKwh.Should().BeApproximately(303.5, 1e-9);
        r.PreviousHouseTotalKwh.Should().BeApproximately(100, 1e-9);
    }
}

public class HeatingReportBuilderTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    private static readonly Guid Dev = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 11, 3);

    private static HeatingEnergyDay Day(string day, double heating, double dhw, double? heatLast = null, double? power = null) =>
        new(Dev, DateOnly.Parse(day), heating, dhw, 10 * heating, 10 * dhw, power, null, HeatingKinds.SourceSummary, heatLast ?? heating, dhw);

    private static HeatingEnergyPeriod Api(string kind, string purpose, string period, double value, DateTimeOffset at) =>
        new(Dev, kind, purpose, period, value, kind == HeatingKinds.Gas ? "m3" : "kWh", at);

    private static HeatingReportInput Input(ReportPeriod p, IReadOnlyList<HeatingEnergyDay>? days = null, IReadOnlyList<HeatingEnergyPeriod>? api = null,
        IReadOnlyList<HeatingCounterReading>? readings = null, IReadOnlyList<HeatingMonthSums>? months = null) =>
        new(p, Ro, Today, days ?? Array.Empty<HeatingEnergyDay>(), months ?? Array.Empty<HeatingMonthSums>(),
            api ?? Array.Empty<HeatingEnergyPeriod>(), readings ?? Array.Empty<HeatingCounterReading>(), new DateOnly(2026, 11, 1));

    [Fact]
    public void Week_GasStackedByPurpose_ClosedDaysUseTheHighest_TodayTheLast()
    {
        var week = ReportPeriod.Containing(ReportGranularity.Week, Today); // 02.11–08.11
        var days = new[]
        {
            Day("2026-11-02", 2.0, 0.5),
            // Today: highest 1.4, but the counter dropped to 0.2 after a late reset → today counts the last value.
            Day("2026-11-03", 1.4, 0.3, heatLast: 0.2),
            Day("2026-11-01", 3.0, 0.4), // previous week (Sunday)
        };

        var r = HeatingReportBuilder.Build(Input(week, days));

        var heating = r.Gas.Series.Single(s => s.Key == "heating").Values;
        var dhw = r.Gas.Series.Single(s => s.Key == "dhw").Values;
        heating[0].Should().Be(2.0);
        heating[1].Should().Be(0.2);
        dhw[1].Should().Be(0.3);
        heating[2].Should().BeNull();
        r.Gas.Total.Should().BeApproximately(3.0, 1e-9);
        r.Gas.ApiTotal.Should().BeNull();
        r.Gas.PreviousTotal.Should().BeApproximately(3.4, 1e-9);
        r.Gas.DeltaPct.Should().BeApproximately((3.0 - 3.4) / 3.4 * 100, 1e-9);
        r.Heat.Total.Should().BeApproximately(20 + 5 + 14 + 3, 1e-9);
        r.Power.Total.Should().BeNull();       // no electricity stored
        r.Power.DeltaPct.Should().BeNull();
    }

    [Fact]
    public void Month_ShowsTheBoilerTotal_WhenItDiffersByMoreThan5Percent()
    {
        var month = ReportPeriod.Containing(ReportGranularity.Month, Today);
        var at = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var days = new[] { Day("2026-11-01", 2, 0.5), Day("2026-11-02", 2, 0.5) };
        var api = new[]
        {
            Api("gas", "heating", "month", 6, at), Api("gas", "dhw", "month", 1, at),
            Api("gas", "heating", "lastmonth", 50, at), Api("gas", "dhw", "lastmonth", 10, at),
        };

        var r = HeatingReportBuilder.Build(Input(month, days, api));

        r.Gas.OwnTotal.Should().BeApproximately(5, 1e-9);
        r.Gas.ApiTotal.Should().BeApproximately(7, 1e-9);
        r.Gas.ApiDiffers.Should().BeTrue();
        r.Gas.Total.Should().BeApproximately(7, 1e-9);
        // October: no stored day, but the boiler's "last month" exists → compared.
        r.Gas.PreviousTotal.Should().BeApproximately(60, 1e-9);
        r.Gas.DeltaPct.Should().BeApproximately((7 - 60) / 60.0 * 100, 1e-9);

        // Viewing October: no bars, the boiler's total only.
        var oct = HeatingReportBuilder.Build(Input(month.Previous(), days, api));
        oct.Gas.Series.SelectMany(s => s.Values).Should().OnlyContain(v => v == null);
        oct.Gas.OwnTotal.Should().BeNull();
        oct.Gas.Total.Should().BeApproximately(60, 1e-9);
        oct.Gas.PreviousTotal.Should().BeNull();  // September: nothing
        oct.Gas.DeltaPct.Should().BeNull();
    }

    [Fact]
    public void Year_MonthsFromStoredDays_TheRestOfTheBoilerYearIsOneBar()
    {
        var year = ReportPeriod.Containing(ReportGranularity.Year, Today);
        var at = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var months = new[] { new HeatingMonthSums(2026, 11, 4.4, 1.0, null, null, null, null) };
        var api = new[] { Api("gas", "heating", "year", 300, at), Api("gas", "dhw", "year", 60, at), Api("gas", "heating", "lastyear", 500, at) };

        var r = HeatingReportBuilder.Build(Input(year, api: api, months: months));

        r.Gas.Series[0].Values[10].Should().Be(4.4);
        r.Gas.Series[0].Values[9].Should().BeNull();
        r.Gas.Remainder.Should().NotBeNull();
        r.Gas.Remainder![0].Should().BeApproximately(295.6, 1e-9);
        r.Gas.Remainder[1].Should().BeApproximately(59, 1e-9);
        r.Gas.OwnTotal.Should().BeApproximately(360, 1e-9);
        r.Gas.Total.Should().BeApproximately(360, 1e-9);
        r.Gas.PreviousTotal.Should().BeApproximately(500, 1e-9);
        r.Gas.DeltaPct.Should().BeApproximately(-28, 1e-9);
        r.Heat.Remainder.Should().BeNull();
    }

    [Fact]
    public void Day_HoursFromTheTodayCounters_ResetAfterMidnightCountsTheNewValue()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2));
        var midnight = new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero);
        var readings = new[]
        {
            new HeatingCounterReading(midnight.AddMinutes(-2), 3.0, 0.4, 0.50, 30),   // 23:58 the day before
            new HeatingCounterReading(midnight.AddMinutes(2), 3.1, 0.4, 0.52, 31),    // 00:02: not reset yet, +0.1
            new HeatingCounterReading(midnight.AddMinutes(10), 0.1, 0.0, 0.01, 1),    // 00:10: reset → +0.1
            new HeatingCounterReading(midnight.AddHours(7).AddMinutes(1), 0.3, 0.2, 0.05, 3), // 07:01: +0.2 / +0.2
            new HeatingCounterReading(midnight.AddHours(7).AddMinutes(3), null, null, null, null), // no counters (older version)
            new HeatingCounterReading(midnight.AddHours(7).AddMinutes(5), 0.5, 0.2, 0.06, 4), // 07:05: +0.2
        };
        var days = new[] { Day("2026-11-01", 3.0, 0.4) };

        var r = HeatingReportBuilder.Build(Input(day, days, readings: readings));

        r.Buckets.Should().HaveCount(24);
        var heating = r.Gas.Series.Single(s => s.Key == "heating").Values;
        var dhw = r.Gas.Series.Single(s => s.Key == "dhw").Values;
        heating[0].Should().BeApproximately(0.2, 1e-9);
        dhw[0].Should().BeApproximately(0.0, 1e-9);
        heating[7].Should().BeApproximately(0.4, 1e-9);
        dhw[7].Should().BeApproximately(0.2, 1e-9);
        heating[3].Should().BeNull();
        r.Power.Series.Should().ContainSingle().Which.Key.Should().Be("total");
        r.Power.Series[0].Values[0].Should().BeApproximately(0.02 + 0.01, 1e-9);
        // No stored day for 02.11 → Σ of the hours; the day before is stored → compared.
        r.Gas.OwnTotal.Should().BeApproximately(0.8, 1e-9);
        r.Gas.PreviousTotal.Should().BeApproximately(3.4, 1e-9);
    }

    [Fact]
    public void Day_ADropThatIsNotAResetIsIgnored_TheHigherValueStaysTheReference()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2));
        var midnight = new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero);
        var readings = new[]
        {
            new HeatingCounterReading(midnight.AddHours(14), 3.2, 0.5, null, null),                 // 14:00
            new HeatingCounterReading(midnight.AddHours(14).AddMinutes(2), 3.1, 0.5, null, null),   // correction: ignored
            new HeatingCounterReading(midnight.AddHours(14).AddMinutes(4), 3.3, 0.5, null, null),   // +0.1 over 3.2
            new HeatingCounterReading(midnight.AddHours(15), 0.1, 0.5, null, null),                 // < 20 % but 15:00: not a reset
            new HeatingCounterReading(midnight.AddHours(15).AddMinutes(2), 3.4, 0.5, null, null),   // +0.1 over 3.3
        };

        var r = HeatingReportBuilder.Build(Input(day, readings: readings));

        var heating = r.Gas.Series.Single(s => s.Key == "heating").Values;
        heating[14].Should().BeApproximately(0.1, 1e-9);   // not 3.1 + 0.2
        heating[15].Should().BeApproximately(0.1, 1e-9);   // not 0.1 + 3.3
        r.IgnoredDecreases.Should().HaveCount(2);
        r.IgnoredDecreases![0].Previous.Should().Be(3.2);
        r.IgnoredDecreases[0].Received.Should().Be(3.1);
        r.IgnoredDecreases[1].Previous.Should().Be(3.3);
    }

    [Theory]
    [InlineData(23, 50, 0.1, true)]
    [InlineData(0, 30, 0.1, true)]
    [InlineData(0, 31, 0.1, false)]   // outside the window
    [InlineData(23, 49, 0.1, false)]
    [InlineData(0, 5, 0.7, false)]    // 0.7 ≥ 20 % of 3.0: a correction
    public void IsReset_OnlyUnder20PercentAroundMidnight(int hour, int minute, double value, bool reset)
    {
        var local = new DateTime(2026, 11, 2, hour, minute, 0);
        var utc = new DateTimeOffset(local, Ro.GetUtcOffset(local));
        HeatingReportBuilder.IsReset(3.0, value, utc, Ro).Should().Be(reset);
    }

    [Fact]
    public void Day_StoredDayAndTheBoilerTodayWin_OverTheHours()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, Today);
        var at = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
        var days = new[] { Day("2026-11-03", 1.0, 0.2), Day("2026-11-02", 0.0, 0.0) };
        var api = new[] { Api("gas", "heating", "day", 1.1, at), Api("gas", "dhw", "day", 0.2, at) };

        var r = HeatingReportBuilder.Build(Input(day, days, api));

        r.Gas.OwnTotal.Should().BeApproximately(1.2, 1e-9);
        r.Gas.ApiTotal.Should().BeApproximately(1.3, 1e-9);
        r.Gas.ApiDiffers.Should().BeTrue();
        r.Gas.Total.Should().BeApproximately(1.3, 1e-9);
        r.Gas.PreviousTotal.Should().Be(0);
        r.Gas.DeltaPct.Should().BeNull();   // division by zero → no percentage
    }

    [Fact]
    public void BoilerPeriods_AreMappedByTheDayTheyWereRead()
    {
        // Read late on 31.10 (local): "month" is October, "day" is 31.10, "lastyear" is 2025.
        var at = new DateTimeOffset(2026, 10, 31, 21, 30, 0, TimeSpan.Zero); // 23:30 local
        var api = new[] { Api("gas", "heating", "month", 40, at), Api("gas", "heating", "day", 2, at), Api("gas", "heating", "lastyear", 400, at) };

        HeatingReportBuilder.Api("gas", api, ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 10, 5)), Ro).Should().Be(40);
        HeatingReportBuilder.Api("gas", api, ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1)), Ro).Should().BeNull();
        HeatingReportBuilder.Api("gas", api, ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 10, 31)), Ro).Should().Be(2);
        HeatingReportBuilder.Api("gas", api, ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 1)), Ro).Should().BeNull();
        HeatingReportBuilder.Api("gas", api, ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2025, 3, 1)), Ro).Should().Be(400);
        HeatingReportBuilder.Api("gas", api, ReportPeriod.Containing(ReportGranularity.Week, new DateOnly(2026, 10, 31)), Ro).Should().BeNull();
    }
}
