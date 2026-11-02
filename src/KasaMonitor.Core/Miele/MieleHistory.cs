using System.Globalization;
using System.Text.Json;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Miele;

/// <summary>
/// Parses the answer of <c>GET /v1/devices/{id}/lastUsedPrograms</c> (spec <c>components.schemas.lastUsedPrograms</c>):
/// an array of at most five runs, newest first, each with <c>programId {value_raw, value_localized}</c>,
/// <c>startAt</c>/<c>endAt</c> (UTC ISO 8601), <c>waterConsumption {unit "l", value}</c>, <c>energyConsumption
/// {unit "kWh", value}</c> and <c>energyMonitoring {unit "kWh", interval "15min", data [[ts, kWh], …]}</c>; the last
/// three may be <c>null</c>. A run without a readable start is skipped; anything else unexpected becomes null.
/// </summary>
public static class MieleLastUsedProgramsParser
{
    public static IReadOnlyList<MieleProgramRun> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<MieleProgramRun>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (Time(item, "startAt") is not { } start) continue;
            var end = Time(item, "endAt") is { } e && e >= start ? e : start;
            MieleValue? program = null;
            if (item.TryGetProperty("programId", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                int? raw = p.TryGetProperty("value_raw", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var ri) ? ri : null;
                var localized = p.TryGetProperty("value_localized", out var l) && l.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(l.GetString())
                    ? l.GetString()!.Trim() : null;
                program = new MieleValue(raw, localized);
            }
            list.Add(new MieleProgramRun(program?.Raw, MieleTexts.Program(program), start, end,
                Consumption(item, "energyConsumption", "wh", 1000), Consumption(item, "waterConsumption", null, 1), Profile(item)));
        }
        return list;
    }

    private static DateTimeOffset? Time(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t : null;

    /// <summary>The <c>value</c> of a consumption object (null, negative or -32768 → null); <paramref name="divUnit"/> = a smaller unit to divide.</summary>
    private static double? Consumption(JsonElement o, string name, string? divUnit, double divisor)
    {
        if (!o.TryGetProperty(name, out var c) || c.ValueKind != JsonValueKind.Object) return null;
        if (!c.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var value)) return null;
        if (value < 0 || value == MieleApi.NoValue) return null;
        var unit = c.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        return divUnit is not null && string.Equals(unit, divUnit, StringComparison.OrdinalIgnoreCase) ? value / divisor : value;
    }

    private static IReadOnlyList<MieleEnergySlot>? Profile(JsonElement o)
    {
        if (!o.TryGetProperty("energyMonitoring", out var m) || m.ValueKind != JsonValueKind.Object) return null;
        if (!m.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var unit = m.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        var divisor = string.Equals(unit, "wh", StringComparison.OrdinalIgnoreCase) ? 1000.0 : 1.0;
        var slots = new List<MieleEnergySlot>();
        foreach (var pair in data.EnumerateArray())
        {
            if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() < 2) continue;
            var ts = pair[0];
            var kwh = pair[1];
            if (ts.ValueKind != JsonValueKind.String || kwh.ValueKind != JsonValueKind.Number || !kwh.TryGetDouble(out var k) || k < 0) continue;
            if (!DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) continue;
            slots.Add(new MieleEnergySlot(t, k / divisor));
        }
        return slots.OrderBy(s => s.StartUtc).ToList();
    }
}

/// <summary>What one history read changes in <c>MieleCycles</c> of an appliance.</summary>
/// <param name="Updates">Observed cycles merged with their API run, or cycles whose API values changed.</param>
/// <param name="Inserts">Runs without an observed cycle: new cycles with source api.</param>
/// <param name="Unchanged">Runs already stored with the same values (dedupe by <c>(DeviceId, ApiStartTs)</c>).</param>
/// <param name="Deferred">Runs matching a cycle still open: merged at a later read (never inserted as a duplicate).</param>
public sealed record MieleHistoryPlan(IReadOnlyList<MieleCycle> Updates, IReadOnlyList<MieleCycle> Inserts, int Unchanged, int Deferred);

/// <summary>
/// Unites the runs of <c>lastUsedPrograms</c> with the stored cycles of one appliance. A run already stored (same
/// <see cref="MieleCycle.ApiStartUtc"/>, to the second) only refreshes its API values. Else it is the same run as an
/// observed cycle (not yet merged) when the starts are at most <see cref="MieleApi.HistoryMatchWindow"/> apart or the two
/// overlap by at least <see cref="MieleApi.HistoryMatchOverlap"/> of the shorter one (the closest start wins); the cycle
/// then takes the API start/end, the API's energy and water in their own fields (the ecoFeedback values and the plug's
/// energy are kept as they are), the energy profile and the program if it had none, and becomes merged. A run
/// matching a cycle still open waits for the next read. Any other run becomes a new cycle with source api (no phases).
/// </summary>
public static class MieleHistoryMerge
{
    public static MieleHistoryPlan Plan(string deviceId, IReadOnlyList<MieleCycle> existing, IReadOnlyList<MieleProgramRun> runs)
    {
        var updates = new List<MieleCycle>();
        var inserts = new List<MieleCycle>();
        int unchanged = 0, deferred = 0;
        var byApiStart = existing.Where(c => c.ApiStartUtc is not null).GroupBy(c => c.ApiStartUtc!.Value).ToDictionary(g => g.Key, g => g.First());
        var taken = new HashSet<long>();
        var seen = new HashSet<DateTimeOffset>();

        foreach (var run in runs.OrderBy(r => r.StartUtc))
        {
            var key = Key(run.StartUtc);
            if (!seen.Add(key)) continue;
            if (byApiStart.TryGetValue(key, out var stored))
            {
                var refreshed = Refresh(stored, run);
                if (refreshed == stored) unchanged++;
                else updates.Add(refreshed);
                continue;
            }

            var match = existing
                .Where(c => c.ApiStartUtc is null && c.Source == MieleCycleSources.Observed && !taken.Contains(c.Id) && Matches(c, run))
                .OrderBy(c => Math.Abs((c.StartUtc - run.StartUtc).TotalSeconds))
                .FirstOrDefault();
            if (match is not null)
            {
                if (match.IsOpen)
                {
                    deferred++;
                    continue;
                }
                taken.Add(match.Id);
                updates.Add(Merge(match, run, key));
                continue;
            }

            inserts.Add(new MieleCycle(0, deviceId, key, Key(run.EndUtc), Key(run.EndUtc), run.ProgramRaw, run.Program,
                Array.Empty<MielePhaseSpan>(), null, null, null, null)
            {
                Source = MieleCycleSources.Api,
                ApiEnergyKwh = run.EnergyKwh,
                ApiWaterL = run.WaterL,
                ApiStartUtc = key,
                ApiEndUtc = Key(run.EndUtc),
                EnergyProfile = run.EnergyProfile,
            });
        }
        return new MieleHistoryPlan(updates, inserts, unchanged, deferred);
    }

    /// <summary>Same run: starts within 10 minutes, or overlap ≥ 50 % of the shorter one (an open cycle ends at its last Running).</summary>
    public static bool Matches(MieleCycle c, MieleProgramRun run)
    {
        if (Math.Abs((c.StartUtc - run.StartUtc).TotalSeconds) <= MieleApi.HistoryMatchWindow.TotalSeconds) return true;
        var cEnd = c.EndUtc ?? c.LastRunningUtc;
        var from = c.StartUtc > run.StartUtc ? c.StartUtc : run.StartUtc;
        var to = cEnd < run.EndUtc ? cEnd : run.EndUtc;
        if (to <= from) return false;
        var shorter = Math.Max(1, Math.Min((cEnd - c.StartUtc).TotalSeconds, (run.EndUtc - run.StartUtc).TotalSeconds));
        return (to - from).TotalSeconds / shorter >= MieleApi.HistoryMatchOverlap;
    }

    private static MieleCycle Merge(MieleCycle c, MieleProgramRun run, DateTimeOffset key) => c with
    {
        Source = MieleCycleSources.Merged,
        ApiStartUtc = key,
        ApiEndUtc = Key(run.EndUtc),
        ApiEnergyKwh = run.EnergyKwh,
        ApiWaterL = run.WaterL,
        ProgramRaw = c.Program is null ? run.ProgramRaw ?? c.ProgramRaw : c.ProgramRaw,
        Program = c.Program ?? run.Program,
        EnergyProfile = run.EnergyProfile ?? c.EnergyProfile,
    };

    /// <summary>A stored run read again: the API values may have been completed (energy, water, profile, end).</summary>
    private static MieleCycle Refresh(MieleCycle c, MieleProgramRun run)
    {
        var end = Key(run.EndUtc);
        var r = c;
        if (run.EnergyKwh is { } e && r.ApiEnergyKwh != e) r = r with { ApiEnergyKwh = e };
        if (run.WaterL is { } w && r.ApiWaterL != w) r = r with { ApiWaterL = w };
        if (run.EnergyProfile is { Count: > 0 } p && !SameProfile(r.EnergyProfile, p)) r = r with { EnergyProfile = p };
        if (r.ApiEndUtc != end) r = r with { ApiEndUtc = end };
        if (r.Source == MieleCycleSources.Api && r.EndUtc != end) r = r with { EndUtc = end, LastRunningUtc = end };
        if (r.Program is null && run.Program is not null) r = r with { Program = run.Program, ProgramRaw = run.ProgramRaw ?? r.ProgramRaw };
        return r;
    }

    private static bool SameProfile(IReadOnlyList<MieleEnergySlot>? a, IReadOnlyList<MieleEnergySlot> b) =>
        a is not null && a.Count == b.Count && a.Zip(b).All(x => Key(x.First.StartUtc) == Key(x.Second.StartUtc) && Math.Abs(x.First.Kwh - x.Second.Kwh) < 1e-9);

    /// <summary>Whole seconds (the table stores unix seconds; the dedupe key must compare equal after a round trip).</summary>
    public static DateTimeOffset Key(DateTimeOffset t) => DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());
}

/// <summary>
/// When the program history is read (<c>lastUsedPrograms</c>, one call per appliance): once after the first read of the
/// appliances after start or after the account was connected, then every <see cref="MieleApi.HistorySyncInterval"/>,
/// and <see cref="MieleApi.HistoryAfterCycleDelay"/> after an observed cycle ended. After 429/503 nothing is called
/// before <c>Retry-After</c> (else <see cref="MieleApi.HistoryBackoff"/>). Without a usable account it waits for the
/// next connection. Thread-safe (the recorder, the auth service and the sync loop all feed it).
/// </summary>
public sealed class MieleHistorySchedule
{
    private readonly object _sync = new();
    private readonly List<DateTimeOffset> _afterCycle = new();
    private bool _initialPending = true;
    private DateTimeOffset? _periodicDue;
    private DateTimeOffset? _blockedUntil;

    public DateTimeOffset? LastSyncUtc { get { lock (_sync) return _last; } }
    private DateTimeOffset? _last;

    /// <summary>The appliances were read (GET /devices or an SSE event): the first read after start/connection makes the sync due.</summary>
    public void DevicesRead(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            if (!_initialPending) return;
            _initialPending = false;
            _periodicDue = nowUtc;
        }
    }

    /// <summary>The account was (re)connected: the next read of the appliances makes the sync due again.</summary>
    public void Connected()
    {
        lock (_sync) _initialPending = true;
    }

    /// <summary>An observed cycle ended at <paramref name="nowUtc"/>: a read is due 2 minutes later.</summary>
    public void CycleEnded(DateTimeOffset nowUtc)
    {
        lock (_sync) _afterCycle.Add(nowUtc + MieleApi.HistoryAfterCycleDelay);
    }

    /// <summary>A read finished: the next periodic one in 6 hours; the cycle triggers up to now are served.</summary>
    public void Synced(DateTimeOffset nowUtc)
    {
        lock (_sync)
        {
            _last = nowUtc;
            _periodicDue = nowUtc + MieleApi.HistorySyncInterval;
            _afterCycle.RemoveAll(t => t <= nowUtc);
        }
    }

    /// <summary>429/503 (or a failed read): no call before <paramref name="retryAfter"/> (else 1 hour).</summary>
    public void Throttled(DateTimeOffset nowUtc, TimeSpan? retryAfter)
    {
        lock (_sync) _blockedUntil = nowUtc + (retryAfter is { } r && r > TimeSpan.Zero ? r : MieleApi.HistoryBackoff);
    }

    /// <summary>No usable token: nothing is due until the account is read again after a connection.</summary>
    public void WaitForConnection()
    {
        lock (_sync)
        {
            _initialPending = true;
            _periodicDue = null;
            _afterCycle.Clear();
        }
    }

    /// <summary>When the next read is due (never before the end of a throttling), or null while waiting for the first read.</summary>
    public DateTimeOffset? NextDue
    {
        get
        {
            lock (_sync)
            {
                DateTimeOffset? due = _periodicDue;
                foreach (var t in _afterCycle)
                    if (due is null || t < due) due = t;
                if (due is null) return null;
                return _blockedUntil is { } b && b > due ? b : due;
            }
        }
    }

    public bool IsDue(DateTimeOffset nowUtc) => NextDue is { } d && nowUtc >= d;
}

/// <summary>
/// The detail of a cycle: Miele's estimated energy profile (kWh per 15-minute slot, as average power kWh × 4000 W)
/// next to the power the linked plug measured on the same interval (minute averages of the live stream, else the
/// 10 s polls), plus the plug's energy per slot for a direct comparison.
/// </summary>
public static class MieleEnergyComparison
{
    public static readonly TimeSpan Slot = TimeSpan.FromMinutes(15);

    public sealed record Point(DateTimeOffset TsUtc, double W);

    /// <param name="MieleWh">Miele's estimate for the slot.</param>
    /// <param name="PlugWh">What the plug measured in the slot (null without plug data).</param>
    public sealed record SlotCompare(DateTimeOffset StartUtc, double MieleWh, double? PlugWh);

    /// <summary>Step points: each slot's start with its average power, plus the end of the last slot (same power).</summary>
    public static IReadOnlyList<Point> ProfilePower(IReadOnlyList<MieleEnergySlot> slots)
    {
        var list = slots.OrderBy(s => s.StartUtc).Select(s => new Point(s.StartUtc, s.Kwh * 1000 / Slot.TotalHours)).ToList();
        if (list.Count > 0) list.Add(list[^1] with { TsUtc = list[^1].TsUtc + Slot });
        return list;
    }

    /// <summary>The plug's power in [from, to]: minute averages when there are any, else the polls (offline/no power skipped).</summary>
    public static IReadOnlyList<Point> PlugPower(IReadOnlyList<LiveMinuteAggregate> minutes, IReadOnlyList<Sample> samples, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var fromMinutes = minutes.Where(m => m.MinuteUtc >= fromUtc.AddSeconds(-59) && m.MinuteUtc <= toUtc)
            .OrderBy(m => m.MinuteUtc).Select(m => new Point(m.MinuteUtc, m.AvgW)).ToList();
        if (fromMinutes.Count > 0) return fromMinutes;
        return samples.Where(s => s.Online && s.PowerMw is not null && s.TimestampUtc >= fromUtc && s.TimestampUtc <= toUtc)
            .OrderBy(s => s.TimestampUtc).Select(s => new Point(s.TimestampUtc, s.PowerMw!.Value / 1000.0)).ToList();
    }

    /// <summary>Per slot: Miele's estimate vs the plug's measured energy (Wh).</summary>
    public static IReadOnlyList<SlotCompare> Slots(IReadOnlyList<MieleEnergySlot> slots, IReadOnlyList<LiveMinuteAggregate> minutes, IReadOnlyList<Sample> samples) =>
        slots.OrderBy(s => s.StartUtc).Select(s => new SlotCompare(s.StartUtc, s.Kwh * 1000,
            MielePlugEnergy.FromMinutes(minutes, s.StartUtc, s.StartUtc + Slot) ?? MielePlugEnergy.FromSamples(samples, s.StartUtc, s.StartUtc + Slot))).ToList();
}
