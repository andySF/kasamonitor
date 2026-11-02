using System.Diagnostics;
using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data;
using KasaMonitor.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace KasaMonitor.Workers.Tests;

/// <summary>/history's sections other than electricity (0.9.2) on a real SQLite database.</summary>
public class HistoryServiceTests : IDisposable
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");
    // 12.11.2026 12:00 local.
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly ReportPeriod November = ReportPeriod.Containing(ReportGranularity.Month, new DateOnly(2026, 11, 1));

    private readonly SqliteConnection _conn = new("Filename=:memory:");
    private readonly TestScopeFactory _scopes;
    private readonly ITestOutputHelper _out;

    public HistoryServiceTests(ITestOutputHelper output)
    {
        _out = output;
        _conn.Open();
        using (var ctx = Create()) ctx.Database.EnsureCreated();
        _scopes = new TestScopeFactory(Create);
    }

    public void Dispose() => _conn.Dispose();

    private KasaDbContext Create() => new(new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(_conn).Options);

    private HistoryService Service(bool raw = false) =>
        new(_scopes, new EnergyReportService(_scopes, Ro, () => Now),
            raw ? new RawClimateDailyStats(_scopes) : new ClimateDailyStore(_scopes, () => Now), detectedRuns: DetectedFromDb, clock: () => Now);

    /// <summary>The detected appliances as NilmService reads them: not hidden, not plug-confirmed, with their runs.</summary>
    private async Task<(IReadOnlyDictionary<long, KasaMonitor.Core.Nilm.ApplianceSignature>, IReadOnlyList<KasaMonitor.Core.Nilm.ApplianceRun>, DateTimeOffset)> DetectedFromDb(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        using var ctx = Create();
        var repo = new NilmRepository(ctx);
        var sigs = (await repo.ListSignaturesAsync(ct)).Where(s => !s.Hidden && !s.ConfirmedByPlug).ToDictionary(s => s.Id);
        var runs = (await repo.ListRunsAsync(from, to, ct)).Where(r => sigs.ContainsKey(r.SignatureId)).ToList();
        return (sigs, runs, Now);
    }

    private ClimateDailyWorker DailyWorker(DateTimeOffset? now = null) => new(_scopes, Ro, clock: () => now ?? Now);

    private static double OutdoorMean(int day) => 12 - 0.8 * day;

    /// <summary>Gas of 1–10 November: 5 kWh base + 3 kWh per degree day of the outdoor mean, as m³ at the default 10.55 kWh/m³.</summary>
    private async Task<Guid> SeedBoilerAsync()
    {
        using var ctx = Create();
        var repo = new HeatingRepository(ctx);
        var dev = await repo.UpsertDeviceAsync(new HeatingDevice(Guid.Empty, "viessmann", "1", "g", "0", "E3", "Vitodens", true, null, null), Ct);
        var days = Enumerable.Range(1, 10).Select(d =>
        {
            var m3 = (5 + 3 * DegreeDayRule.Eurostat.Of(OutdoorMean(d))) / ViessmannApi.DefaultKwhPerM3;
            return new HeatingEnergyDay(dev.Id, new DateOnly(2026, 11, d), m3, 0.2, 10, 1, null, null, HeatingKinds.SourceSummary, m3, 0.2);
        }).ToList();
        await repo.UpsertDailyAsync(days, Ct);

        // Readings every 10 min on 9–10 November (UTC); from 23:00Z on the 9th (the boiler's 10 November) the burner hours
        // rise 0.05 and the starts 1 per reading; an error F4 for one reading at 12:00Z on the 10th.
        var boilerDay = new DateTimeOffset(2026, 11, 9, 23, 0, 0, TimeSpan.Zero);
        for (var t = new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero); t < new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero); t = t.AddMinutes(10))
        {
            var steps = t < boilerDay ? 0 : (int)((t - boilerDay).TotalMinutes / 10);
            var error = t == new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero) ? "F4" : null;
            await repo.SaveReadingAsync(new HeatingReading(dev.Id, t, 50, 50, 50, 40, 5, 21, 48, 50, steps % 3 == 1, 30, 50, 1.5,
                1000 + steps, 100 + 0.05 * steps, "heating", "normal", "eco", error, null), Ct);
        }
        return dev.Id;
    }

    private async Task<(Guid Room, Guid Outdoor)> SeedClimateAsync()
    {
        using var ctx = Create();
        var repo = new ClimateRepository(ctx);
        var outdoor = await repo.GetOrAddSensorAsync(ClimateSources.OpenMeteo, "outdoor", "Afară", "Afară", true, Ct);
        var room = await repo.GetOrAddSensorAsync(ClimateSources.Govee, "AA:BB:CC:DD:EE:FF:00:01", "Senzor living", "Living", false, Ct);
        for (var d = 1; d <= 10; d++)
        {
            var start = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, d)).StartUtc(Ro);
            for (var t = start; t < start.AddDays(1); t = t.AddMinutes(30))
            {
                await repo.AddReadingAsync(new ClimateReading(outdoor.Id, t, OutdoorMean(d), 80, null), Ct);
                await repo.AddReadingAsync(new ClimateReading(room.Id, t, t.Hour < 12 ? 20 : 22, 45, null), Ct);
            }
        }
        return (room.Id, outdoor.Id);
    }

    [Fact]
    public async Task Gas_BurnerHoursAndStartsPerBoilerDay_TimelinePerLocalDay()
    {
        await SeedBoilerAsync();
        var svc = Service();

        var g = (await svc.GetGasAsync(November, Ct))!;

        g.DeviceName.Should().Be("Vitodens");
        var nov10 = g.Report.Buckets.ToList().FindIndex(b => b.Day == new DateOnly(2026, 11, 10));
        g.Burner[nov10].Hours.Should().BeApproximately(0.05 * 143, 1e-6, "the boiler's 10 November: 23:00Z on the 9th to 22:50Z");
        g.Burner[nov10].Starts.Should().Be(143);
        g.Burner[nov10 - 1].Hours.Should().Be(0);
        g.Report.Gas.Total.Should().BeGreaterThan(0);
        g.Timeline.Should().HaveCount(12, "the local days of November up to today");
        g.Timeline![9].Segments.Should().NotBeEmpty();
        (g.Timeline[9].HeatingMinutes + g.Timeline[9].DhwMinutes + g.Timeline[9].UnknownMinutes).Should().BeGreaterThan(0);
        g.HourProfileM3.Should().HaveCount(24);
        (await svc.GetGasAsync(ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 1, 1)), Ct))!.Timeline.Should().BeNull("a year has no raw timeline");
    }

    [Fact]
    public async Task Climate_DaysPerSensor_DegreeDays_AndTheEnergySignatureFromTenDays()
    {
        await SeedBoilerAsync();
        var (room, outdoor) = await SeedClimateAsync();
        var svc = Service();

        var c = await svc.GetClimateAsync(November, Ct);

        c.Outdoor!.Id.Should().Be(outdoor);
        var living = c.Sensors.Single(s => s.Sensor.Id == room);
        living.Buckets[0].MinC.Should().Be(20);
        living.Buckets[0].MaxC.Should().Be(22);
        living.Buckets[0].AvgC.Should().BeApproximately(21, 0.1);
        living.Buckets[11].AvgC.Should().BeNull("no reading on the 12th");
        c.DegreeDays[2].Should().BeApproximately(18 - OutdoorMean(3), 1e-6);
        // The last reading of the 10th still holds for half an hour after midnight: the 11th has that mean too.
        c.DegreeDaysTotal.Should().BeApproximately(Enumerable.Range(1, 10).Sum(d => 18 - OutdoorMean(d)) + (18 - OutdoorMean(10)), 1e-6);
        c.SignaturePeriod.Should().Be(ReportPeriod.Between(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 12)));
        c.Signature!.Enough.Should().BeTrue();
        c.Signature.Points.Should().HaveCount(10);
        c.Signature.Fit!.Slope.Should().BeApproximately(3, 1e-6);
        c.Signature.Fit.Intercept.Should().BeApproximately(5, 1e-6);
        c.Signature.Fit.RSquared.Should().BeApproximately(1, 1e-9);

        // A week with only 3 days of data: the 30 days ending with it are used, still 10 points.
        var week = await svc.GetClimateAsync(ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 5)), Ct);
        week.SignaturePeriod.Should().Be(ReportPeriod.Between(new DateOnly(2026, 10, 7), new DateOnly(2026, 11, 5)));
        week.Signature!.Points.Should().HaveCount(5);
        week.Signature.Enough.Should().BeFalse();
        week.Signature.FirstDay.Should().Be(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public async Task MieleAndEvents_OfThePeriod()
    {
        await SeedBoilerAsync();
        Guid plug;
        using (var ctx = Create())
        {
            var miele = new MieleRepository(ctx);
            await miele.UpsertDeviceAsync("000123", null, Now, Ct);
            var s = new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
            await miele.AddCycleAsync(new MieleCycle(0, "000123", s, s.AddMinutes(90), s.AddMinutes(90), 1, "Bumbac", Array.Empty<MielePhaseSpan>(), 0.9, 45, 1100, "end"), Ct);
            await miele.AddCycleAsync(new MieleCycle(0, "000123", s.AddDays(-10), s.AddDays(-10).AddMinutes(60), s, 1, "Bumbac", Array.Empty<MielePhaseSpan>(), 0.9, 45, null, "end"), Ct);
            plug = Guid.NewGuid();
            ctx.Plugs.Add(new KasaMonitor.Data.Entities.PlugEntity { Id = plug, Mac = "AA:BB:CC:00:00:09", IpAddress = "192.168.1.9", Alias = "Boiler electric", Protocol = "legacy",
                FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch });
            await ctx.SaveChangesAsync();
            await new LiveRepository(ctx).AddRelayCommandAsync(new RelayCommand(0, s, plug, false, RelayCommandSources.Ui, RelayCommand.Ok), Ct);
        }
        var svc = Service();

        var m = await svc.GetMieleAsync(November, Ct);
        m.Cycles.Should().ContainSingle("the October cycle is outside");
        m.Cycles[0].PlugKwh.Should().BeApproximately(1.1, 1e-9);
        m.Programs.Should().ContainSingle().Which.Program.Should().Be("Bumbac");

        var e = await svc.GetEventsAsync(November, Ct);
        e.Events.Should().Contain(x => x.Source == HistoryEventSource.Relay && x.Subject == "Boiler electric" && x.Text == "oprită din interfață");
        e.Events.Should().Contain(x => x.Source == HistoryEventSource.Boiler && x.Text == "eroare F4");
        e.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task Sections_AreComputedOnce_AndShared()
    {
        await SeedBoilerAsync();
        var svc = Service();
        var a = svc.GetGasAsync(November, Ct);
        var b = svc.GetGasAsync(November, Ct);
        (await a).Should().BeSameAs(await b);
        (await svc.GetGasAsync(November, Ct)).Should().BeSameAs(await a, "a period in progress stays 10 minutes");
    }

    /// <summary>
    /// Phase 2 decision: a year of raw climate readings (3 sensors, one reading every 10 minutes ≈ 157 000 rows, denser than
    /// the real cadence of „on change or every 30 min”) must stay under the 300 ms budget, or the daily table is needed.
    /// The measured time is written to the test output (the 0.9.2 report).
    /// </summary>
    [Fact]
    public async Task Climate_AYearOfRawReadings_Measured()
    {
        var year = ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 1, 1));
        var ids = new List<Guid>();
        using (var ctx = Create())
        {
            var repo = new ClimateRepository(ctx);
            ids.Add((await repo.GetOrAddSensorAsync(ClimateSources.OpenMeteo, "o", "Afară", "Afară", true, Ct)).Id);
            ids.Add((await repo.GetOrAddSensorAsync(ClimateSources.Govee, "a", "A", "Living", false, Ct)).Id);
            ids.Add((await repo.GetOrAddSensorAsync(ClimateSources.Govee, "b", "B", "Dormitor", false, Ct)).Id);
        }
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "BEGIN";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO ClimateReadings (SensorId, Ts, TemperatureC, HumidityPercent, BatteryPercent) VALUES ($s, $t, $v, 50, NULL)";
            var ps = cmd.CreateParameter(); ps.ParameterName = "$s"; cmd.Parameters.Add(ps);
            var pt = cmd.CreateParameter(); pt.ParameterName = "$t"; cmd.Parameters.Add(pt);
            var pv = cmd.CreateParameter(); pv.ParameterName = "$v"; cmd.Parameters.Add(pv);
            cmd.Prepare();
            var start = year.StartUtc(Ro).ToUnixTimeSeconds();
            var end = Now.ToUnixTimeSeconds();
            var rows = 0;
            foreach (var id in ids)
            {
                for (var t = start; t < end; t += 600)
                {
                    ps.Value = id.ToString().ToUpperInvariant();
                    pt.Value = t;
                    pv.Value = 10 + 10 * Math.Sin(t / 86400.0);
                    cmd.ExecuteNonQuery();
                    rows++;
                }
            }
            cmd.Parameters.Clear();
            cmd.CommandText = "COMMIT";
            cmd.ExecuteNonQuery();
            _out.WriteLine($"rows: {rows}");
        }
        var sw = Stopwatch.StartNew();
        var raw = await Service(raw: true).GetClimateAsync(year, Ct);
        sw.Stop();
        _out.WriteLine($"climate, year of raw readings: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var written = await DailyWorker().FillAsync(Ct);
        sw.Stop();
        _out.WriteLine($"ClimateDaily fill: {written} rows in {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        var c = await Service().GetClimateAsync(year, Ct);
        sw.Stop();
        _out.WriteLine($"climate, year from ClimateDaily (+ today raw): {sw.ElapsedMilliseconds} ms");

        c.Sensors.Should().HaveCount(3);
        c.Sensors[0].Buckets.Take(10).Should().OnlyContain(b => b.AvgC != null, "January to October have readings");
        for (var i = 0; i < 12; i++)
        {
            if (raw.Sensors[1].Buckets[i].AvgC is { } expected)
                c.Sensors[1].Buckets[i].AvgC.Should().BeApproximately(expected, 1e-9, "the stored days give the raw answer");
            else
                c.Sensors[1].Buckets[i].AvgC.Should().BeNull();
        }
        c.DegreeDaysTotal.Should().BeApproximately(raw.DegreeDaysTotal!.Value, 1e-9);
        sw.ElapsedMilliseconds.Should().BeLessThan(20_000);
    }

    /// <summary>
    /// The boiler's events over a year of readings (one every 2 minutes ≈ 263 000 rows, with an error and a gap): measured
    /// (the 0.9.2 report, review R6), and the events found.
    /// </summary>
    [Fact]
    public async Task Events_AYearOfBoilerReadings_Measured()
    {
        Guid dev;
        using (var ctx = Create())
            dev = (await new HeatingRepository(ctx).UpsertDeviceAsync(new HeatingDevice(Guid.Empty, "viessmann", "1", "g", "0", "E3", "Vitodens", true, null, null), Ct)).Id;
        var year = ReportPeriod.Containing(ReportGranularity.Year, new DateOnly(2026, 1, 1));
        var start = year.StartUtc(Ro).ToUnixTimeSeconds();
        var end = Now.ToUnixTimeSeconds();
        var errorAt = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var gapFrom = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var rows = 0;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "BEGIN";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "INSERT INTO HeatingReadings (DeviceId, Ts, ActiveError) VALUES ($d, $t, $e)";
            var pd = cmd.CreateParameter(); pd.ParameterName = "$d"; pd.Value = dev.ToString().ToUpperInvariant(); cmd.Parameters.Add(pd);
            var pt = cmd.CreateParameter(); pt.ParameterName = "$t"; cmd.Parameters.Add(pt);
            var pe = cmd.CreateParameter(); pe.ParameterName = "$e"; cmd.Parameters.Add(pe);
            cmd.Prepare();
            for (var t = start; t < end; t += 120)
            {
                if (t >= gapFrom && t < gapFrom + 3600) continue;
                pt.Value = t;
                pe.Value = t >= errorAt && t < errorAt + 600 ? "F4" : DBNull.Value;
                cmd.ExecuteNonQuery();
                rows++;
            }
            cmd.Parameters.Clear();
            cmd.CommandText = "COMMIT";
            cmd.ExecuteNonQuery();
        }
        var sw = Stopwatch.StartNew();
        var e = await Service().GetEventsAsync(year, Ct);
        sw.Stop();
        _out.WriteLine($"boiler readings: {rows}; events of the year: {sw.ElapsedMilliseconds} ms");
        e.Events.Where(x => x.Source == HistoryEventSource.Boiler).Select(x => x.Text).Should().BeEquivalentTo(new[] { "eroare F4", "fără citiri (deconectată sau aplicația oprită)" });
        e.Events.Single(x => x.Text == "eroare F4").EndUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(errorAt + 600));
        e.Events.Single(x => x.Text.StartsWith("fără citiri")).EndUtc.Should().Be(DateTimeOffset.FromUnixTimeSeconds(gapFrom + 3600));
    }

    [Fact]
    public async Task ClimateDaily_FillsTheFinishedDays_EmptyOnesToo_ThenOnlyTheNewDay()
    {
        var (room, _) = await SeedClimateAsync();   // readings 1–10 November (local)

        (await DailyWorker().FillAsync(Ct)).Should().Be(2 * 11, "1–11 November for both sensors: the 11th holds the last reading for half an hour");
        (await DailyWorker().FillAsync(Ct)).Should().Be(2 * ClimateDailyWorker.RecomputeDays, "nothing new: only the last 3 finished days again");
        (await DailyWorker(Now.AddDays(1)).FillAsync(Ct)).Should().Be(2 * ClimateDailyWorker.RecomputeDays, "after midnight: 12 November (empty) and the 2 days before");

        // A reading stored late (the boiler's source catching up) for a day already written reaches it at the next fill.
        var tenth = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 10)).StartUtc(Ro);
        using (var c1 = Create())
            await new ClimateRepository(c1).AddReadingAsync(new ClimateReading(room, tenth.AddHours(12).AddMinutes(10), 30, 45, null), Ct);
        await DailyWorker(Now.AddDays(1)).FillAsync(Ct);
        using (var c2 = Create())
            (await new ClimateRepository(c2).ListDailyAsync(new[] { room }, new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 10), Ct))
                .Single().Stats.MaxC.Should().Be(30);

        using var ctx = Create();
        var days = await new ClimateRepository(ctx).ListDailyAsync(new[] { room }, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 12), Ct);
        days.Should().HaveCount(12);
        days[0].Stats.Should().BeEquivalentTo(new { MinC = 20.0, MaxC = 22.0, MinHumidity = 45.0 });
        days[0].Stats.AvgC.Should().BeApproximately(21, 0.1);
        days[0].Stats.Count.Should().Be(49, "48 readings of the day + the one at the next midnight (ClimateAggregation counts both ends)");
        days[11].Stats.Count.Should().Be(0);
        days[11].Stats.AvgC.Should().BeNull();
    }

    [Fact]
    public async Task ClimateDailyStore_StoredDays_DaysNotStoredYetAndToday_FromTheRawReadings()
    {
        var (room, outdoor) = await SeedClimateAsync();
        await DailyWorker(Now.AddDays(-5)).FillAsync(Ct);   // stores up to 6 November only
        using (var ctx = Create())
        {
            var start = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 12)).StartUtc(Ro);
            await new ClimateRepository(ctx).AddReadingAsync(new ClimateReading(room, start.AddHours(1), 19, 40, null), Ct);
        }

        var store = new ClimateDailyStore(_scopes, () => Now);
        var days = await store.GetAsync(new[] { room, outdoor }, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 12), Ro, Ct);
        var raw = await new RawClimateDailyStats(_scopes).GetAsync(new[] { room, outdoor }, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 12), Ro, Ct);

        days.Select(d => (d.SensorId, d.Day)).Should().BeEquivalentTo(raw.Select(d => (d.SensorId, d.Day)), "the same days, stored or not");
        days.Should().Contain(d => d.SensorId == room && d.Day == new DateOnly(2026, 11, 12) && d.Stats.AvgC == 19, "today from the raw readings");
        foreach (var d in days)
            d.Stats.AvgC.Should().BeApproximately(raw.Single(r => r.SensorId == d.SensorId && r.Day == d.Day).Stats.AvgC!.Value, 1e-9);
        ClimateDailyStore.Runs(new[] { new DateOnly(2026, 1, 3), new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 9) })
            .Should().Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3)), (new DateOnly(2026, 1, 9), new DateOnly(2026, 1, 9)));
        DailyWorker().NextWait(Now).Should().Be(ClimateDailyWorker.MaxWait, "midnight is 12 h away: at most an hour");
    }

    [Fact]
    public async Task Consumers_NamingASignature_ShowsAtTheNextLoad_NotAfterTheCache()
    {
        Guid main = Guid.NewGuid();
        using (var ctx = Create())
        {
            ctx.Plugs.Add(new KasaMonitor.Data.Entities.PlugEntity
            {
                Id = main, Mac = "AA:BB:CC:00:02:01", IpAddress = "192.168.1.21", Alias = "Contor", Protocol = "shelly", Role = (int)PlugRole.MainMeter,
                FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch,
            });
            ctx.DailyEnergies.Add(new KasaMonitor.Data.Entities.DailyEnergyEntity { PlugId = main, Date = "2026-11-05", EnergyWh = 10_000 });
            await ctx.SaveChangesAsync();
        }
        KasaMonitor.Core.Nilm.ApplianceSignature sig;
        using (var ctx = Create())
        {
            var repo = new NilmRepository(ctx);
            sig = await repo.AddSignatureAsync(new KasaMonitor.Core.Nilm.ApplianceSignature(0, "", "bi-question-circle", "other", 2000, 0, 1, 0, null,
                KasaMonitor.Core.Nilm.SignatureLabelSource.Cluster, null, false), Ct);
            var start = ReportPeriod.Containing(ReportGranularity.Day, new DateOnly(2026, 11, 5)).StartUtc(Ro);
            await repo.SaveRunsAsync(new[] { new KasaMonitor.Core.Nilm.ApplianceRun(0, sig.Id, start.AddHours(8), start.AddHours(9), 2000, 2000, 1, 2) }, Ct);
        }
        var svc = Service();
        var before = await svc.GetDetectedAsync(November, Ct);
        before.Breakdown.Unidentified!.Kwh.Should().BeApproximately(2, 1e-9);
        before.Breakdown.IdentifiedPct.Should().Be(0);

        using (var ctx = Create())
            await new NilmRepository(ctx).UpdateSignatureAsync(sig with { Name = "Fierbător", LabelSource = KasaMonitor.Core.Nilm.SignatureLabelSource.User }, Ct);

        var after = await svc.GetDetectedAsync(November, Ct);
        after.Breakdown.Rows.Should().Contain(r => r.Name == "Fierbător" && r.Kind == ConsumerKind.IdentifiedDetected);
        after.Breakdown.IdentifiedPct.Should().BeApproximately(20, 1e-9, "2 of the meter's 10 kWh, at once (the labels are in the cache key)");
    }

    [Fact]
    public async Task Climate_TodayIsNotInTheDegreeDays_ItIsShownAsPartial()
    {
        await SeedBoilerAsync();
        var (_, outdoor) = await SeedClimateAsync();
        using (var ctx = Create())
            await new ClimateRepository(ctx).AddReadingAsync(new ClimateReading(outdoor, Now.AddHours(-2), 5, 80, null), Ct);

        var c = await Service().GetClimateAsync(November, Ct);

        c.PartialDay.Should().Be(new DateOnly(2026, 11, 12));
        c.PartialDegreeDays.Should().BeGreaterThan(0);
        c.DegreeDays[11].Should().BeNull("today's bar holds no degree days until the day ends");
        c.DegreeDaysTotal.Should().BeApproximately(Enumerable.Range(1, 10).Sum(d => 18 - OutdoorMean(d)) + (18 - OutdoorMean(10)), 1e-6);
    }

    [Fact]
    public async Task Consumers_ASingleAppliancePlug_IsIdentifiedUnderItsAppliancesName()
    {
        Guid main = Guid.NewGuid(), boiler = Guid.NewGuid(), hall = Guid.NewGuid();
        using (var ctx = Create())
        {
            void Add(Guid id, string alias, PlugRole role, int n, bool single = false, string? appliance = null) =>
                ctx.Plugs.Add(new KasaMonitor.Data.Entities.PlugEntity
                {
                    Id = id, Mac = $"AA:BB:CC:00:01:{n:X2}", IpAddress = "192.168.1." + n, Alias = alias, Protocol = "shelly", Role = (int)role,
                    FirstSeenUtc = DateTimeOffset.UnixEpoch, LastSeenUtc = DateTimeOffset.UnixEpoch, SingleAppliance = single, ApplianceName = appliance,
                });
            Add(main, "Contor", PlugRole.MainMeter, 1);
            Add(boiler, "Priză baie", PlugRole.Plug, 2, true, "Boiler electric");
            Add(hall, "Priză hol", PlugRole.Plug, 3);
            foreach (var (id, wh) in new[] { (main, 10_000L), (boiler, 4_000L), (hall, 1_000L) })
                ctx.DailyEnergies.Add(new KasaMonitor.Data.Entities.DailyEnergyEntity { PlugId = id, Date = "2026-11-05", EnergyWh = wh });
            await ctx.SaveChangesAsync();
        }

        var d = await Service().GetDetectedAsync(November, Ct);

        var b = d.Breakdown;
        b.Rows.Should().Contain(r => r.Name == "Boiler electric" && r.Kind == ConsumerKind.IdentifiedPlug && Math.Abs(r.Kwh - 4) < 1e-9);
        b.Rows.Should().NotContain(r => r.Name == "Priză baie", "the appliance's name, not the plug's alias");
        b.Rows.Single(r => r.Kind == ConsumerKind.OtherPlugs).Members!.Single().Name.Should().Be("Priză hol");
        b.IdentifiedPct.Should().BeApproximately(40, 1e-9);
    }
}
