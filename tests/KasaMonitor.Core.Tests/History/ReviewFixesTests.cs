using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Home;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.History;

/// <summary>The fixes of the 0.9.2 review (R1, R3, R7, R8, R9, R10) on the pure rules.</summary>
public class ReviewFixesTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [Fact]
    public void R1_TheBoilersHourLandsOnTheLocalHourItWasUsedIn()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 4));
        var local = day.Buckets(Ro);
        var boiler = day.Buckets(BoilerClock.DefaultTimeZone);   // starts at 01:00 Romanian time
        var values = boiler.Select(b => TimeZoneInfo.ConvertTime(b.StartUtc, Ro).Hour == 10 ? (double?)0.3 : null).ToList();

        var aligned = BucketAlign.ByStart(boiler, values, local);

        aligned[10].Should().Be(0.3, "the boiler's 09:00 CET hour is 10:00–11:00 local");
        aligned.Count(v => v is not null).Should().Be(1);
        BucketAlign.ByStart(boiler, boiler.Select(_ => (double?)1).ToList(), local)[0].Should().BeNull("00:00–01:00 local belongs to the boiler's previous day");
    }

    [Fact]
    public void R3_TheSignatureHasNoThreshold_SevenDaysAt16DegreesAreNotAtZero()
    {
        var d0 = new DateOnly(2026, 10, 1);
        var outdoor = Enumerable.Range(0, 7).ToDictionary(i => d0.AddDays(i), i => 16.0 - 0.5 * i);   // 16 … 13 °C
        var gas = outdoor.ToDictionary(kv => kv.Key, kv => 4 + 3 * Math.Max(0, 18 - kv.Value));

        var sig = EnergySignatureRule.Build(outdoor, gas, DegreeDayRule.Eurostat);

        sig.Points.Should().OnlyContain(p => p.DegreeDays > 0, "16 °C is 2 degrees under the base: x = 2, not the Eurostat 0");
        sig.Points[0].DegreeDays.Should().Be(2);
        DegreeDayRule.Eurostat.Of(16).Should().Be(0, "the totals stay Eurostat");
        sig.Fit!.Slope.Should().BeApproximately(3, 1e-9);
        sig.Fit.Intercept.Should().BeApproximately(4, 1e-9);
    }

    [Fact]
    public void R7_TheShareIsOfTheMeter_PlugsAboveItAreApart()
    {
        var p = Guid.NewGuid();
        var b = ConsumerBreakdownRule.Build(new double?[] { 1, 4 },
            new[] { new ConsumerPlug(p, "Priză", new double?[] { 1.5, 1 }, Appliance: "Boiler") },
            new Dictionary<long, IReadOnlyList<double>>(), Array.Empty<ConsumerSignature>());

        b.TotalKwh.Should().Be(5, "the meter, Σ of its bars");
        b.Rows.Single(r => r.Kind == ConsumerKind.IdentifiedPlug).Kwh.Should().Be(2, "the plug up to the meter on the first bar (1) + 1");
        b.AboveMeterKwh.Should().BeApproximately(0.5, 1e-9);
        b.Rows.Where(r => r.Kind != ConsumerKind.AboveMeter).Sum(r => r.Kwh).Should().BeApproximately(5, 1e-9);
        b.IdentifiedPct.Should().BeApproximately(40, 1e-9);
    }

    [Fact]
    public void R8_OneScalingRule_BarByBar_ForTheHomePageAndTheHistory()
    {
        var meter = new double?[] { 2, 1, null };
        var plugs = new double?[] { 0.5, 1.2, null };
        var detected = new Dictionary<long, IReadOnlyList<double>> { [1] = new[] { 1.0, 0.5, 9 }, [2] = new[] { 1.0, 0, 0 }, [3] = new[] { 0.004, 0, 0 } };

        var s = EnergyFlowRule.ScalePerBucket(meter, plugs, detected);

        // Bar 0: 1.5 available, 2 detected → × 0.75; bar 1: plugs above the meter → nothing fits; bar 2: no meter.
        s.Detected[1][0].Should().BeApproximately(0.75, 1e-9);
        s.Detected[1][1].Should().Be(0);
        s.Detected[1][2].Should().BeNull();
        s.Detected.Should().NotContainKey(3, "noise stays in „Nedetectat”");
        s.Undetected[0].Should().BeApproximately(1.5 - 1.5, 1e-9);
        s.Scaled.Should().BeTrue();

        var flows = EnergyFlowRule.Build(3, new[] { new FlowItem("p", "Priză", FlowKind.Plug, 1.7) },
            s.Detected.Select(kv => new FlowItem("sig-" + kv.Key, "#" + kv.Key, FlowKind.Detected, s.DetectedKwh(kv.Key))), s);
        flows.DetectedKwh.Should().BeApproximately(1.5, 1e-9);
        flows.UndetectedKwh.Should().BeApproximately(s.UndetectedKwh, 1e-9);
        flows.DetectedScaled.Should().BeTrue();

        var b = ConsumerBreakdownRule.Build(meter, new[] { new ConsumerPlug(Guid.NewGuid(), "Priză", plugs) }, detected,
            new[] { new ConsumerSignature(1, "Fierbător", true, null), new ConsumerSignature(2, "Necunoscut #2", false, null), new ConsumerSignature(3, "Necunoscut #3", false, null) });
        b.KwhOf(ConsumerKind.IdentifiedDetected).Should().BeApproximately(s.DetectedKwh(1), 1e-9, "the same rule as the home page");
        b.KwhOf(ConsumerKind.Undetected).Should().BeApproximately(s.UndetectedKwh, 1e-9);
    }

    [Fact]
    public void R9_DatesBefore2000AndMalformedPlugsAreIgnored()
    {
        var today = new DateOnly(2026, 11, 2);
        PeriodQuery.Parse("zi", "0001-01-01", null, null, today).Should().Be(ReportPeriod.Containing(ReportGranularity.Day, today));
        PeriodQuery.Parse("luna", "1999-12", null, null, today, ReportGranularity.Month).Should().Be(ReportPeriod.Containing(ReportGranularity.Month, today));
        PeriodQuery.Parse("interval", null, "0001-01-01", "2026-10-01", today, ReportGranularity.Month).Should().Be(ReportPeriod.Containing(ReportGranularity.Month, today));
        PeriodQuery.Parse("zi", "2000-01-01", null, null, today).Start.Should().Be(new DateOnly(2000, 1, 1));
        PeriodQuery.Plug("abc").Should().BeNull();
        PeriodQuery.Plug(null).Should().BeNull();
        var id = Guid.NewGuid();
        PeriodQuery.Plug(" " + id + " ").Should().Be(id);
    }

    [Fact]
    public void R10_TheCsvWritesTheTimeInIso_ThePageKeepsItsText()
    {
        var t = new HistoryTable(new[] { "Ziua", "kWh", "Notă" },
            new IReadOnlyList<string>[] { new[] { "luni 2 nov", "3,74", "rest în marja de eroare" } }, CsvKeys: new[] { "2026-11-02" });

        HistoryCsv.Write(t).Should().Be("Ziua;kWh;Notă\r\n2026-11-02;3,74;rest în marja de eroare\r\n");
        t.Rows[0][0].Should().Be("luni 2 nov");
    }
}
