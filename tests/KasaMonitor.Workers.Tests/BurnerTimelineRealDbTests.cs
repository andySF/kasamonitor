using System.Globalization;
using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Data;
using KasaMonitor.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Xunit.Abstractions;

namespace KasaMonitor.Workers.Tests;

/// <summary>
/// The burner timeline of 2 Nov 2026 (Romanian day) on a copy of the real database (opt-in: <c>KASA_NILM_TESTDB</c>, like
/// <see cref="NilmRealDbTests"/>): how many runs and how many are heating / hot water / unknown. Writes the runs to the
/// test output (and appends them to <c>KASA_NILM_SUMMARY</c> when set).
/// </summary>
public class BurnerTimelineRealDbTests(ITestOutputHelper output)
{
    private static readonly TimeZoneInfo Ro = TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest");

    [RealDbFact]
    public async Task November2_RunsAndTheirPurpose()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"kasa-burner-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var source = Environment.GetEnvironmentVariable(RealDbFactAttribute.Variable)!;
            var db = Path.Combine(dir, "kasa.db");
            File.Copy(source, db);
            if (File.Exists(source + "-wal")) File.Copy(source + "-wal", db + "-wal");
            var options = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite($"Data Source={db}").Options;
            await using (var migrate = new KasaDbContext(options)) await migrate.Database.MigrateAsync();

            await using var ctx = new KasaDbContext(options);
            var repo = new HeatingRepository(ctx);
            var device = (await repo.ListDevicesAsync(CancellationToken.None)).Single();
            var dayStart = new DateTimeOffset(new DateTime(2026, 11, 2), Ro.GetUtcOffset(new DateTime(2026, 11, 2)));
            var dayEnd = dayStart.AddDays(1);
            var readings = await repo.ListReadingsAsync(device.Id, dayStart.AddHours(-1), dayEnd.AddHours(1), CancellationToken.None);
            var segments = BurnerTimeline.Build(readings).Where(s => s.EndUtc > dayStart && s.StartUtc < dayEnd).ToList();

            var inDay = readings.Where(r => r.TsUtc >= dayStart && r.TsUtc < dayEnd).ToList();
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"Burner timeline 2026-11-02 (Europe/Bucharest): {inDay.Count} readings "
                + $"{Local(inDay.FirstOrDefault()?.TsUtc)}–{Local(inDay.LastOrDefault()?.TsUtc)}, cadence {BurnerTimeline.Cadence(readings).TotalSeconds:0} s, "
                + $"{segments.Count} segments: heating {Count(segments, BurnerPurpose.Heating)}, dhw {Count(segments, BurnerPurpose.Dhw)}, "
                + $"unknown {Count(segments, BurnerPurpose.Unknown)}");
            foreach (var s in segments)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {Local(s.StartUtc)}–{Local(s.EndUtc)} ({s.Duration.TotalMinutes:0} min, {s.Readings} readings"
                    + $"{(s.Ongoing ? ", ongoing" : "")}) {BurnerTimeline.Label(s.Purpose)}: mod {s.AvgModulationPct:0.0} %, gas heating {s.GasHeatingM3:0.0} "
                    + $"dhw {s.GasDhwM3:0.0} m3 | {s.Reason}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"  current purpose at the last reading: {BurnerTimeline.Current(readings)}");
            output.WriteLine(sb.ToString());
            if (Environment.GetEnvironmentVariable("KASA_NILM_SUMMARY") is { Length: > 0 } file) await File.AppendAllTextAsync(file, sb.ToString());

            inDay.Should().NotBeEmpty();
            segments.Should().NotBeEmpty("the burner ran on the morning of 2 Nov");
            segments.Should().OnlyContain(s => s.EndUtc >= s.StartUtc);
            // The run of 08:35–08:50 Romanian time: heating through the late counter (+0.6 m³ at 09:10), not "hot water".
            var lateRun = segments.Single(s => s.StartUtc == dayStart.AddHours(8).AddMinutes(35).AddSeconds(50));
            lateRun.Purpose.Should().Be(BurnerPurpose.Heating);
            lateRun.GasHeatingM3.Should().BeApproximately(0.6, 1e-9);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static int Count(IEnumerable<BurnerSegment> s, BurnerPurpose p) => s.Count(x => x.Purpose == p);

    private static string Local(DateTimeOffset? t) =>
        t is { } v ? TimeZoneInfo.ConvertTime(v, Ro).ToString("HH:mm", CultureInfo.InvariantCulture) : "—";
}
