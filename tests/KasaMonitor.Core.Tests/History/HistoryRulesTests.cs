using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Core.Reports;
using Xunit;

namespace KasaMonitor.Core.Tests.History;

/// <summary>The pure rules of /history (0.9.2): degree days, the regression, burner counters, climate days, Miele per program, detected appliances, events, CSV.</summary>
public class HistoryRulesTests
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    // ------------------------------------------------------------------ degree days and the energy signature

    [Theory]
    [InlineData(5.0, 13.0)]
    [InlineData(15.0, 3.0)]      // at the threshold: still counted
    [InlineData(15.1, 0.0)]      // above it: no heating day
    [InlineData(-4.5, 22.5)]
    public void DegreeDays_Eurostat_Base18_Threshold15(double mean, double expected) =>
        DegreeDayRule.Eurostat.Of(mean).Should().BeApproximately(expected, 1e-9);

    [Fact]
    public void DegreeDays_AnotherBase_KeepsTheThresholdThreeDegreesUnder()
    {
        var r = DegreeDayRule.ForBase(20);
        r.ThresholdC.Should().Be(17);
        r.Of(10).Should().Be(10);
        r.Of(17.5).Should().Be(0);
    }

    [Fact]
    public void Regression_OnAnExactLine_GivesItsCoefficients_AndR2One()
    {
        var pts = Enumerable.Range(0, 10).Select(i => ((double)i, 4.0 + 2.5 * i)).ToList();
        var fit = LinearRegression.Fit(pts)!;
        fit.Slope.Should().BeApproximately(2.5, 1e-9);
        fit.Intercept.Should().BeApproximately(4.0, 1e-9);
        fit.RSquared.Should().BeApproximately(1, 1e-12);
        fit.Count.Should().Be(10);
        fit.At(3).Should().BeApproximately(11.5, 1e-9);
    }

    [Fact]
    public void Regression_WithNoise_MatchesTheTextbookValues()
    {
        // x = 1..5, y = 2, 4, 5, 4, 5: slope 0.6, intercept 2.2, R² = 0.6.
        var fit = LinearRegression.Fit(new[] { (1.0, 2.0), (2.0, 4.0), (3.0, 5.0), (4.0, 4.0), (5.0, 5.0) })!;
        fit.Slope.Should().BeApproximately(0.6, 1e-9);
        fit.Intercept.Should().BeApproximately(2.2, 1e-9);
        fit.RSquared.Should().BeApproximately(0.6, 1e-9);
    }

    [Fact]
    public void Regression_WithoutALine_IsNull()
    {
        LinearRegression.Fit(new[] { (1.0, 2.0) }).Should().BeNull("one point");
        LinearRegression.Fit(new[] { (3.0, 2.0), (3.0, 5.0), (3.0, 1.0) }).Should().BeNull("every x the same");
    }

    [Fact]
    public void Signature_NeedsSevenDaysWithBothValues_FromTheFirstSuchDay()
    {
        var d0 = new DateOnly(2026, 11, 1);
        var outdoor = Enumerable.Range(0, 10).ToDictionary(i => d0.AddDays(i), i => 10.0 - i);   // 10 … 1 °C
        // Gas: 5 kWh base + 3 kWh per degree day, only on 6 days first.
        var gas = Enumerable.Range(2, 6).ToDictionary(i => d0.AddDays(i), i => 5 + 3 * DegreeDayRule.Eurostat.Of(10.0 - i));

        var six = EnergySignatureRule.Build(outdoor, gas, DegreeDayRule.Eurostat);
        six.Points.Should().HaveCount(6);
        six.Enough.Should().BeFalse();
        six.Fit.Should().BeNull();
        six.FirstDay.Should().Be(d0.AddDays(2));

        gas[d0.AddDays(9)] = 5 + 3 * DegreeDayRule.Eurostat.Of(1);
        var seven = EnergySignatureRule.Build(outdoor, gas, DegreeDayRule.Eurostat);
        seven.Enough.Should().BeTrue();
        seven.Fit!.Slope.Should().BeApproximately(3, 1e-9);
        seven.Fit.Intercept.Should().BeApproximately(5, 1e-9);
        seven.Fit.RSquared.Should().BeApproximately(1, 1e-9);
        seven.Points.Select(p => p.Day).Should().BeInAscendingOrder();
    }

    // ------------------------------------------------------------------ burner counters

    [Fact]
    public void BurnerCounters_EachHourAddsItsRiseSinceTheHourBefore_ThePeriodStartIsAReference()
    {
        var day = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 2));
        var b = day.Buckets(Ro);
        var h0 = b[0].StartUtc;
        var hours = new[]
        {
            new BurnerCounterHour(h0.AddHours(-1), 99.0, 100.0, 499, 500),      // before the day: the reference only
            new BurnerCounterHour(h0, 100.2, 100.5, 501, 502),                  // +0.5 h (from 100), +2 starts
            new BurnerCounterHour(h0.AddHours(3), 100.6, 101.0, 503, 505),      // +0.5 h across the gap, +3 starts
            new BurnerCounterHour(h0.AddHours(5), 2.0, 2.4, 10, 12),           // counter replaced: only its own rise
        };

        var r = BurnerCounters.PerBucket(hours, b);

        r[0].Hours.Should().BeApproximately(0.5, 1e-9);
        r[0].Starts.Should().Be(2);
        r[1].Hours.Should().BeNull();
        r[3].Hours.Should().BeApproximately(0.5, 1e-9);
        r[3].Starts.Should().Be(3);
        r[5].Hours.Should().BeApproximately(0.4, 1e-9);
        r[5].Starts.Should().Be(2);
    }

    [Fact]
    public void BurnerCounters_DaysOfAMonth_SumTheirHours()
    {
        var month = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));
        var b = month.Buckets(Ro);
        var hours = Enumerable.Range(0, 48).Select(i => new BurnerCounterHour(b[0].StartUtc.AddHours(i), 10 + 0.1 * i, 10 + 0.1 * (i + 1), 100 + i, 101 + i)).ToList();
        var r = BurnerCounters.PerBucket(hours, b);
        r[0].Hours.Should().BeApproximately(2.4, 1e-9);
        r[1].Hours.Should().BeApproximately(2.4, 1e-9);
        r[0].Starts.Should().Be(24);
        r[2].Hours.Should().BeNull();
    }

    // ------------------------------------------------------------------ climate days

    [Fact]
    public void ClimateWindows_AreTheSameAsTheStatsOfEachDay_AndCombineToAMonth()
    {
        var days = ReportPeriod.Between(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 3)).Buckets(Ro);
        var points = new List<ClimatePoint>();
        for (var t = days[0].StartUtc.AddMinutes(-20); t < days[^1].EndUtc; t = t.AddMinutes(25))
            points.Add(new ClimatePoint(t, 18 + (t - days[0].StartUtc).TotalHours / 10, 50));
        var windows = days.Select(d => (d.StartUtc, d.EndUtc)).ToList();

        var fast = ClimateWindows.Stats(points.AsEnumerable().Reverse().ToList(), windows);

        for (var i = 0; i < days.Count; i++)
        {
            var slow = ClimateAggregation.Stats(points, days[i].StartUtc, days[i].EndUtc);
            fast[i].AvgC.Should().BeApproximately(slow.AvgC!.Value, 1e-9);
            fast[i].MinC.Should().Be(slow.MinC);
            fast[i].MaxC.Should().Be(slow.MaxC);
        }
        var month = ClimateWindows.Combine(fast.Append(ClimateStats.Empty));
        month.MinC.Should().Be(fast[0].MinC);
        month.MaxC.Should().Be(fast[2].MaxC);
        month.AvgC.Should().BeApproximately(fast.Average(f => f.AvgC!.Value), 1e-9);
        ClimateWindows.Combine(new[] { ClimateStats.Empty }).Should().Be(ClimateStats.Empty);
        ClimateWindows.Stats(Array.Empty<ClimatePoint>(), windows).Should().OnlyContain(s => s.AvgC == null);
    }

    // ------------------------------------------------------------------ Miele per program

    private static MieleCycle Cycle(int id, string? program, double minutes, double? apiKwh, double? plugWh, double? water, bool open = false)
    {
        var start = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero).AddHours(id);
        return new MieleCycle(id, "dev", start, open ? null : start.AddMinutes(minutes), start, null, program, Array.Empty<MielePhaseSpan>(),
            apiKwh, water, plugWh, null);
    }

    [Fact]
    public void MielePrograms_MeansAndTotals_PlugEnergyFirst_OpenCyclesLeftOut()
    {
        var rows = new[]
        {
            MieleCycleRow.From(Cycle(1, "Bumbac", 120, 1.0, 1200, 50), "Mașina"),
            MieleCycleRow.From(Cycle(2, "Bumbac", 100, 0.8, null, 40), "Mașina"),
            MieleCycleRow.From(Cycle(3, "Eco", 200, 0.5, 600, null), "Mașina"),
            MieleCycleRow.From(Cycle(4, null, 30, null, null, null), "Mașina"),
            MieleCycleRow.From(Cycle(5, "Eco", 0, 0.5, null, null, open: true), "Mașina"),
        };

        var p = MieleProgramStats.ByProgram(rows);

        p.Select(x => x.Program).Should().Equal("Bumbac", "Eco", MieleProgramStats.UnknownProgram);
        var cotton = p[0];
        cotton.Cycles.Should().Be(2);
        cotton.MeanMinutes.Should().BeApproximately(110, 1e-9);
        cotton.TotalKwh.Should().BeApproximately(1.2 + 0.8, 1e-9, "the plug's 1.2 kWh, else Miele's 0.8");
        cotton.MeanWaterL.Should().BeApproximately(45, 1e-9);
        p[1].Cycles.Should().Be(1, "the open cycle is not a finished run");
        p[2].MeanKwh.Should().BeNull();
        rows[0].BestKwh.Should().BeApproximately(1.2, 1e-9);
        rows[0].MieleKwh.Should().Be(1.0);
    }

    // ------------------------------------------------------------------ detected appliances

    [Fact]
    public void DetectedAppliances_EnergyInThePeriod_RunsStartedInIt_StartHours()
    {
        var from = new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero);   // 2 Nov 00:00 local
        var to = from.AddDays(1);
        var runs = new[]
        {
            new ApplianceRun(1, 7, from.AddHours(-1), from.AddHours(1), 1000, 2000, 1, 2),          // half inside, started before
            new ApplianceRun(2, 7, from.AddHours(6), from.AddHours(6.5), 1000, 500, 3, 4),          // 06:00 local
            new ApplianceRun(3, 7, from.AddHours(18), from.AddHours(19), 1000, 1000, 5, 6),         // 18:00 local
            new ApplianceRun(4, 9, from.AddHours(3), from.AddHours(4), 100, 100, 7, 8),             // not a detected one
        };

        var rows = DetectedApplianceStats.Build(runs, new Dictionary<long, string> { [7] = "Fierbător" }, from, to, to, Ro);

        var k = rows.Should().ContainSingle().Subject;
        k.Kwh.Should().BeApproximately(1.0 + 0.5 + 1.0, 1e-9);
        k.Runs.Should().Be(2);
        k.MeanMinutes.Should().BeApproximately(45, 1e-9);
        k.StartsByHour[6].Should().Be(1);
        k.StartsByHour[18].Should().Be(1);
        k.StartsByHour.Sum().Should().Be(2);
    }

    // ------------------------------------------------------------------ consumers' breakdown

    [Fact]
    public void Consumers_IdentifiedOnTheirOwn_AnonymousGrouped_Undetected_AddUpToTheMeter()
    {
        var fridge = Guid.NewGuid();
        var tv = Guid.NewGuid();
        var meter = new double?[] { 10, null, 4 };
        var plugs = new[] { new ConsumerPlug(fridge, "Priză frigider", new double?[] { 2, null, 1 }), new ConsumerPlug(tv, "Priză TV", new double?[] { 1, null, 1 }) };
        var detected = new Dictionary<long, IReadOnlyList<double>>
        {
            [1] = new[] { 3.0, 0, 1.5 },     // named by the user
            [2] = new[] { 1.0, 0, 1.0 },     // anonymous
            [3] = new[] { 0.5, 0, 0.5 },     // anonymous
            [4] = new[] { 0.001, 0, 0.0 },   // noise: stays in „Nedetectat”
        };
        var sigs = new[]
        {
            new ConsumerSignature(1, "Fierbător", true, null), new ConsumerSignature(2, "Necunoscut #2", false, null),
            new ConsumerSignature(3, "Necunoscut #3", false, null), new ConsumerSignature(4, "Necunoscut #4", false, null),
            new ConsumerSignature(9, "Frigider", false, fridge),    // confirmed by the fridge's plug
        };

        var b = ConsumerBreakdownRule.Build(meter, plugs, detected, sigs);

        b.Rows.Select(r => (r.Name, r.Kind)).Should().Equal(
            ("Fierbător", ConsumerKind.IdentifiedDetected), ("Frigider", ConsumerKind.IdentifiedPlug), ("Alte prize (1)", ConsumerKind.OtherPlugs),
            ("Neidentificați (2 semnături)", ConsumerKind.Unidentified), ("Nedetectat", ConsumerKind.Undetected));
        // Bucket 0: 10 − 3 (plugs) = 7 available ≥ 4.5 detected → as estimated; nedetectat 2.5.
        // Bucket 2: 4 − 2 = 2 available < 3 detected → scaled by 2/3; nedetectat 0.
        b.Rows[0].Buckets[0].Should().BeApproximately(3, 1e-9);
        b.Rows[0].Buckets[2].Should().BeApproximately(1.0, 1e-9);
        b.Rows[0].Buckets[1].Should().BeNull("no meter data in that bar");
        b.Rows[4].Buckets[0].Should().BeApproximately(2.5, 1e-9);
        b.Rows[4].Buckets[2].Should().BeApproximately(0, 1e-9);
        b.TotalKwh.Should().BeApproximately(14, 1e-9);
        b.Rows.Sum(r => r.Kwh).Should().BeApproximately(b.TotalKwh, 1e-9);
        b.Unidentified!.Members!.Select(m => m.Name).Should().Equal("Necunoscut #2", "Necunoscut #3");
        b.IdentifiedKwh.Should().BeApproximately(3 + 1 + 3, 1e-9);
        b.IdentifiedPct.Should().BeApproximately(50, 1e-9);
    }

    [Fact]
    public void Consumers_ASingleAppliancePlug_IsIdentifiedUnderItsAppliancesName()
    {
        var boiler = Guid.NewGuid();
        var other = Guid.NewGuid();
        var b = ConsumerBreakdownRule.Build(new double?[] { 10 },
            new[] { new ConsumerPlug(boiler, "Priză baie", new double?[] { 4 }, Appliance: "Boiler electric"), new ConsumerPlug(other, "Priză hol", new double?[] { 1 }) },
            new Dictionary<long, IReadOnlyList<double>>(), Array.Empty<ConsumerSignature>());

        b.Rows[0].Should().BeEquivalentTo(new { Name = "Boiler electric", Kind = ConsumerKind.IdentifiedPlug, Kwh = 4.0 });
        b.Rows[1].Kind.Should().Be(ConsumerKind.OtherPlugs);
        b.Rows[1].Members!.Single().Name.Should().Be("Priză hol");
        b.IdentifiedPct.Should().BeApproximately(40, 1e-9);
        b.Rows.Sum(r => r.Kwh).Should().BeApproximately(10, 1e-9, "„Alte prize” stays a group so the rows add up to the meter");
    }

    [Fact]
    public void Consumers_PlugsAboveTheMeter_NothingDetectedFits_TheTotalIsTheMeter()
    {
        var p = Guid.NewGuid();
        var b = ConsumerBreakdownRule.Build(new double?[] { 1 }, new[] { new ConsumerPlug(p, "Priză", new double?[] { 1.5 }) },
            new Dictionary<long, IReadOnlyList<double>> { [1] = new[] { 0.4 } }, new[] { new ConsumerSignature(1, "Necunoscut #1", false, null) });
        b.TotalKwh.Should().BeApproximately(1, 1e-9, "the meter (0.9.2 review R7)");
        b.AboveMeterKwh.Should().BeApproximately(0.5, 1e-9, "the plugs above the meter, apart");
        b.KwhOf(ConsumerKind.Unidentified).Should().Be(0);
        b.KwhOf(ConsumerKind.Undetected).Should().Be(0);
        b.IdentifiedPct.Should().Be(0);
    }

    // ------------------------------------------------------------------ events

    [Fact]
    public void Events_BoilerErrorsAndGaps_MieleDisconnections_Relay()
    {
        var t = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        var states = new[]
        {
            new HeatingReadingState(t, null),
            new HeatingReadingState(t.AddMinutes(2), "F4"),
            new HeatingReadingState(t.AddMinutes(4), "F4"),
            new HeatingReadingState(t.AddMinutes(6), " "),
            new HeatingReadingState(t.AddMinutes(60), null),       // 54 minutes without readings
            new HeatingReadingState(t.AddMinutes(62), "F9"),       // still on at the end
        };
        var boiler = HistoryEvents.Boiler(states, "Vitodens").OrderBy(e => e.TsUtc).ToList();
        boiler.Should().HaveCount(3);
        boiler[0].Should().BeEquivalentTo(new { TsUtc = t.AddMinutes(2), EndUtc = (DateTimeOffset?)t.AddMinutes(6), Text = "eroare F4", Problem = true });
        boiler[1].Text.Should().StartWith("fără citiri");
        boiler[1].EndUtc.Should().Be(t.AddMinutes(60));
        boiler[2].Should().BeEquivalentTo(new { Text = "eroare F9", EndUtc = (DateTimeOffset?)null });

        MieleStateValues V(int status, bool? signal) => new(status, null, null, null, null, null, null, null, null, null, null, null, null, null, null,
            signal, null, null, null, null, null, null, null, null);
        var snaps = new[]
        {
            new MieleStateSnapshot(t, "d", V(MieleTexts.StatusOff, false)),
            new MieleStateSnapshot(t.AddHours(1), "d", V(MieleTexts.StatusNotConnected, null)),
            new MieleStateSnapshot(t.AddHours(2), "d", V(MieleTexts.StatusNotConnected, null)),
            new MieleStateSnapshot(t.AddHours(3), "d", V(MieleTexts.StatusOff, false)),
        };
        var miele = HistoryEvents.Miele(snaps, "Mașina").Should().ContainSingle().Subject;
        miele.TsUtc.Should().Be(t.AddHours(1));
        miele.EndUtc.Should().Be(t.AddHours(3));
        miele.Text.Should().Be("neconectat");

        var plug = Guid.NewGuid();
        var relay = HistoryEvents.Relay(new[]
        {
            new RelayCommand(1, t, plug, true, RelayCommandSources.Ui, RelayCommand.Ok),
            new RelayCommand(2, t, Guid.NewGuid(), false, RelayCommandSources.Ui, "timeout"),
        }, new Dictionary<Guid, string> { [plug] = "Boiler" }).ToList();
        relay[0].Should().BeEquivalentTo(new { Subject = "Boiler", Text = "pornită din interfață", Problem = false });
        relay[1].Should().BeEquivalentTo(new { Subject = "priză ștearsă", Problem = true });
        relay[1].Text.Should().Contain("eșuat: timeout");
    }

    // ------------------------------------------------------------------ CSV

    [Fact]
    public void Csv_SemicolonSeparated_QuotesWhatNeedsIt_CrLf_Utf8WithBom()
    {
        var table = new HistoryTable(new[] { "Ziua", "Casă kWh", "Notă" },
            new IReadOnlyList<string>[] { new[] { "luni 2 nov", "12,5", "a; b" }, new[] { "marți 3 nov", "", "zis \"x\"" } });

        HistoryCsv.Write(table).Should().Be("Ziua;Casă kWh;Notă\r\nluni 2 nov;12,5;\"a; b\"\r\nmarți 3 nov;;\"zis \"\"x\"\"\"\r\n");
        var bytes = HistoryCsv.Bytes(table);
        bytes.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Should().StartWith("Ziua;Casă");
        HistoryCsv.Field("linia1\nlinia2").Should().Be("\"linia1\nlinia2\"");
    }
}
