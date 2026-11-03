using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Data;
using KasaMonitor.Workers.Nilm;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace KasaMonitor.Workers.Tests;

/// <summary>
/// Runs only when <c>KASA_NILM_TESTDB</c> names a copy of a real database (with its <c>-wal</c>); skipped otherwise.
/// The file is copied again to a temporary folder, so the copy given stays untouched.
/// </summary>
public sealed class RealDbFactAttribute : FactAttribute
{
    public const string Variable = "KASA_NILM_TESTDB";

    public RealDbFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } path || !File.Exists(path))
            Skip = $"{Variable} not set (path of a copy of a real kasa.db)";
    }
}

/// <summary>
/// The database of 2 Nov 2026 (0.8.1): „Regrupează acum” (the washing machine's heater #53 + #55, the kettle's starts of
/// 11:08–11:11 local, the drum motor's pulses #65, #68–#72) and the plug labels of the washing machine's 13 events
/// (08:10–08:22 UTC) re-detected by a backfill. Writes what it found to the test output (and appends it to
/// <c>KASA_NILM_SUMMARY</c> when set).
/// </summary>
public class NilmRealDbTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1793611500);   // 2026-11-02 09:25 UTC
    private static readonly long[] KettleStarts = { 1793610491, 1793610559, 1793610604 };
    private static readonly Guid HolMic = Guid.Parse("00DC32AE-965D-470E-9987-8E6F84AF4612");

    private sealed class Copy : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), $"kasa-nilm-real-{Guid.NewGuid():N}");
        private readonly DbContextOptions<KasaDbContext> _options;

        public Copy()
        {
            Directory.CreateDirectory(_dir);
            var source = Environment.GetEnvironmentVariable(RealDbFactAttribute.Variable)!;
            var db = Path.Combine(_dir, "kasa.db");
            File.Copy(source, db);
            if (File.Exists(source + "-wal")) File.Copy(source + "-wal", db + "-wal");
            // A copy of a 0.10.x/0.11 kasa.db (SQLite, schema of the last SQLite migration): read as it is, the migrations are SQL Server's since 0.12.0.
            _options = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite($"Data Source={db}").Options;
        }

        public KasaDbContext Ctx() => new(_options);

        public NilmService Service()
        {
            var scopes = new TestScopeFactory(Ctx);
            var settings = TestSettings.Create(scopes, new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255", MlEnabled: false));
            return new NilmService(scopes, settings, new LiveStreamBroadcaster(), new PlugStateBroadcaster(), NullLogger<NilmService>.Instance,
                Path.Combine(_dir, "nilm-model.zip"), () => Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Bucharest"));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }
    }

    private async Task WriteAsync(string summary)
    {
        output.WriteLine(summary);
        if (Environment.GetEnvironmentVariable("KASA_NILM_SUMMARY") is { Length: > 0 } file) await File.AppendAllTextAsync(file, summary);
    }

    [RealDbFact]
    public async Task RegroupNow_OnTheRealDatabase_WasherOneSignature_KettleStartsToTheKettle_MotorAtMostTwo()
    {
        using var copy = new Copy();
        Dictionary<long, long?> before;
        List<(long Id, long Ts)> kettle;
        int signaturesBefore;
        using (var ctx = copy.Ctx())
        {
            before = await ctx.MeterEvents.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.SignatureId);
            kettle = (await ctx.MeterEvents.AsNoTracking().Where(e => KettleStarts.Contains(e.Ts)).Select(e => new { e.Id, e.Ts }).ToListAsync())
                .Select(x => (x.Id, x.Ts)).ToList();
            signaturesBefore = await ctx.ApplianceSignatures.CountAsync();
        }
        long[] Of(params long[] sigs) => before.Where(kv => kv.Value is { } s && sigs.Contains(s)).Select(kv => kv.Key).ToArray();
        var washer = Of(53, 55);
        var motor = Of(65, 68, 69, 70, 71, 72);
        var kitchen = Of(59, 88);

        var result = await copy.Service().RegroupNowAsync(CancellationToken.None);

        Dictionary<long, long?> after;
        Dictionary<long, (string Name, SignatureLabelSource Source, int Count, double P, double Q)> sigs;
        using (var ctx = copy.Ctx())
        {
            after = await ctx.MeterEvents.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.SignatureId);
            sigs = (await ctx.ApplianceSignatures.AsNoTracking().ToListAsync())
                .ToDictionary(s => s.Id, s => (s.Name, NilmSources.SignatureFromInt(s.LabelSource), s.Count, s.MeanDeltaP, s.MeanDeltaQ));
        }
        string Describe(long[] events)
        {
            var groups = events.GroupBy(e => after.GetValueOrDefault(e)).OrderByDescending(g => g.Count());
            return string.Join("; ", groups.Select(g => g.Key is { } s && sigs.TryGetValue(s, out var x)
                ? $"#{s} „{x.Name}” ({x.P:0} W, {x.Count} ev.): {g.Count()}"
                : $"fără semnătură: {g.Count()}"));
        }
        var merges = result.Merges ?? Array.Empty<MergeStep>();
        var training = result.Training;
        var summary = new StringBuilder()
            .AppendLine($"Regrupare: {result.Events} evenimente, {result.Anchored} la ancore, {result.Clusters} clustere, {result.Moved} mutate, {result.Merged} unite; semnături {signaturesBefore} → {result.Signatures}")
            .AppendLine(training?.Info is { } info
                ? $"Model antrenat după regrupare în {training.Duration.TotalSeconds:0.0} s: {info.Examples} evenimente etichetate, {info.Classes.Count} clase ({string.Join(", ", info.Classes)}), acuratețe {info.Folds}-fold {info.Accuracy * 100:0.0} %"
                : $"Model neantrenat: {training?.LabeledEvents} evenimente etichetate în {training?.LabeledClasses} clase")
            .AppendLine("Uniri: " + (merges.Count == 0 ? "niciuna" : string.Join("; ", merges.Select(m => $"#{m.AbsorbedId} → #{m.SurvivorId} ({m.Reason})"))))
            .AppendLine($"Mașina de spălat (#53+#55, {washer.Length} ev.): {Describe(washer)}")
            .AppendLine($"Fierbător 11:08–11:11 ({kettle.Count} porniri): {Describe(kettle.Select(k => k.Id).ToArray())}")
            .AppendLine($"Fostele #59/#88 (1,4 kW, {kitchen.Length} ev.): {Describe(kitchen)}; uniri cu #28: {merges.Count(m => m.SurvivorId == 28 || m.AbsorbedId == 28)}")
            .AppendLine($"Motorul tamburului (#65,#68–#72, {motor.Length} ev.): {Describe(motor)}")
            .AppendLine($"Perechile mici: #8 ({Of(8).Length} ev.): {Describe(Of(8))} | #11 ({Of(11).Length} ev.): {Describe(Of(11))} | #5 ({Of(5).Length} ev.): {Describe(Of(5))} | #3 ({Of(3).Length} ev.): {Describe(Of(3))}")
            .AppendLine($"Evenimente fără semnătură: înainte {before.Count(kv => kv.Value is null)}, după {after.Count(kv => kv.Value is null)} (din {after.Count})")
            .AppendLine("Semnături după: " + string.Join("; ", sigs.OrderByDescending(x => x.Value.Count)
                .Select(x => $"#{x.Key} „{x.Value.Name}” {x.Value.P:0} W ΔQ {x.Value.Q:0} {x.Value.Count} ev. ({NilmSources.Code(x.Value.Source)})")))
            .ToString();
        await WriteAsync(summary);

        washer.Select(e => after.GetValueOrDefault(e)).Distinct().Should().ContainSingle("#53 and #55 are one heater");
        kettle.Should().HaveCount(3);
        kettle.Select(k => after.GetValueOrDefault(k.Id)).Should().OnlyContain(s => s == 28);
        sigs[28].Name.Should().Be("Fierbător");
        training.Should().NotBeNull("the model is on by default: „Regrupează acum” retrains");
        merges.Should().NotContain(m => new long[] { 8, 11, 5, 3 }.Contains(m.SurvivorId) && new long[] { 8, 11, 5, 3 }.Contains(m.AbsorbedId),
            "small loads are not one component (rule d only for inductive loads ≥ 150 W)");
        motor.Select(e => after.GetValueOrDefault(e)).Where(s => s is not null).Distinct().Count().Should().BeLessThanOrEqualTo(2);
    }

    [RealDbFact]
    public async Task Backfill_WashingMachineEvents_LabelledFromThePlugsStream()
    {
        var withStream = await LabelWasherAsync(withStream: true);
        var pollsOnly = await LabelWasherAsync(withStream: false);
        withStream.Should().BeGreaterThan(0);
        pollsOnly.Should().BeGreaterThan(0);
    }

    /// <summary>Re-detects 08:05–09:25 by a backfill and returns how many of the washing machine's events „Hol mic” labelled.</summary>
    private async Task<int> LabelWasherAsync(bool withStream)
    {
        using var copy = new Copy();
        const long from = 1793606640;   // 08:04 UTC: the detector starts again here, the events from 08:05 are detected anew
        int labelledBefore;
        using (var ctx = copy.Ctx())
        {
            labelledBefore = await ctx.MeterEvents.CountAsync(e => e.Ts >= 1793607000 && e.Ts <= 1793607720 && (e.DeltaP >= 1700 || e.DeltaP <= -1700)
                                                                   && e.LabelPlugId == HolMic);
            await ctx.ApplianceRuns.Where(r => r.StartTs >= from + 60).ExecuteDeleteAsync();
            await ctx.MeterEvents.Where(e => e.Ts >= from + 60).ExecuteDeleteAsync();
            // Without the plugs' stream: only the 10-second polls are left (the main meter's samples stay).
            if (!withStream) await ctx.LiveSamples.Where(x => x.PlugId != Guid.Parse("BD464C70-2035-4160-9041-6EC4DC16DECB")).ExecuteDeleteAsync();
        }
        using (var ctx = copy.Ctx())
        {
            var settings = new Data.Repositories.SettingsRepository(ctx);
            await settings.SetAsync(SettingKeys.NilmLastProcessedTs, from.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
            await settings.SetAsync(SettingKeys.NilmLastReclusterTs, Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
                CancellationToken.None);
        }

        await copy.Service().RunOnceAsync(CancellationToken.None);

        List<(long Ts, double P, string? Label, Guid? Plug)> washer;
        using (var ctx = copy.Ctx())
            washer = (await ctx.MeterEvents.AsNoTracking()
                    .Where(e => e.Ts >= 1793607000 && e.Ts <= 1793607720 && (e.DeltaP >= 1700 || e.DeltaP <= -1700))
                    .OrderBy(e => e.Ts).Select(e => new { e.Ts, e.DeltaP, e.Label, e.LabelPlugId }).ToListAsync())
                .Select(x => (x.Ts, x.DeltaP, x.Label, x.LabelPlugId)).ToList();
        var labelled = washer.Count(e => e.Plug == HolMic);
        var summary = new StringBuilder()
            .AppendLine($"Etichetare mașina de spălat {(withStream ? "cu fluxul prizelor" : "doar cu citirile de 10 s")} (08:10–08:22 UTC, |ΔP| ≥ 1,7 kW): {washer.Count} evenimente re-detectate, {labelled} etichetate „Hol mic” (0.8.1: {labelledBefore})")
            .AppendLine(string.Join("; ", washer.Select(e => $"{DateTimeOffset.FromUnixTimeSeconds(e.Ts):HH:mm:ss} {e.P:+0;-0} W → {e.Label ?? "—"}")))
            .ToString();
        await WriteAsync(summary);

        washer.Should().HaveCount(13);
        return labelled;
    }
}
