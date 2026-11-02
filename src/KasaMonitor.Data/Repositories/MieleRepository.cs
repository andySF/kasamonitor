using System.Text.Json;
using System.Text.Json.Serialization;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class MieleRepository : IMieleRepository
{
    private readonly KasaDbContext _db;
    public MieleRepository(KasaDbContext db) => _db = db;

    public async Task<IReadOnlyList<MieleDevice>> ListDevicesAsync(bool includeArchived, CancellationToken ct)
    {
        var rows = await _db.MieleDevices.AsNoTracking().Where(d => includeArchived || !d.IsArchived).ToListAsync(ct);
        return rows.OrderBy(d => d.Alias, StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Id, StringComparer.Ordinal).Select(ToModel).ToList();
    }

    public async Task<MieleDevice> UpsertDeviceAsync(string deviceId, MieleIdent? ident, DateTimeOffset seenUtc, CancellationToken ct)
    {
        var e = await _db.MieleDevices.FindAsync(new object?[] { deviceId }, ct);
        if (e is null)
        {
            e = new MieleDeviceEntity { Id = deviceId };
            _db.MieleDevices.Add(e);
        }
        if (ident is not null)
        {
            if (ident.TypeRaw is not null || e.TypeText.Length == 0)
            {
                e.TypeRaw = ident.TypeRaw ?? e.TypeRaw;
                e.TypeText = MieleTexts.TypeText(e.TypeRaw, ident.TypeLocalized);
            }
            if (ident.TechType is { } tech) e.TechType = tech;
            if (ident.DeviceName is { } name) e.DeviceName = name;
        }
        if (e.TypeText.Length == 0) e.TypeText = MieleTexts.TypeText(e.TypeRaw, null);
        if (e.Alias.Length == 0) e.Alias = e.DeviceName.Length > 0 ? e.DeviceName : e.TypeText;
        e.LastSeenUtc = seenUtc.ToUnixTimeSeconds();
        await _db.SaveChangesAsync(ct);
        return ToModel(e);
    }

    public Task SetAliasAsync(string deviceId, string alias, CancellationToken ct) =>
        UpdateAsync(deviceId, e => e.Alias = alias.Trim(), ct);

    public Task SetPlugAsync(string deviceId, Guid? plugId, CancellationToken ct) =>
        UpdateAsync(deviceId, e => e.PlugId = plugId, ct);

    public Task SetArchivedAsync(string deviceId, bool archived, CancellationToken ct) =>
        UpdateAsync(deviceId, e => e.IsArchived = archived, ct);

    private async Task UpdateAsync(string deviceId, Action<MieleDeviceEntity> change, CancellationToken ct)
    {
        var e = await _db.MieleDevices.FindAsync(new object?[] { deviceId }, ct) ?? throw new KeyNotFoundException("Miele device not found");
        change(e);
        await _db.SaveChangesAsync(ct);
    }

    public async Task AddSnapshotAsync(MieleStateSnapshot s, CancellationToken ct)
    {
        var v = s.Values;
        _db.MieleStateSnapshots.Add(new MieleStateSnapshotEntity
        {
            Ts = s.TsUtc.ToUnixTimeSeconds(),
            DeviceId = s.DeviceId,
            StatusRaw = v.StatusRaw,
            StatusText = v.StatusText,
            ProgramTypeRaw = v.ProgramTypeRaw,
            ProgramTypeText = v.ProgramTypeText,
            ProgramIdRaw = v.ProgramIdRaw,
            ProgramIdText = v.ProgramIdText,
            PhaseRaw = v.PhaseRaw,
            PhaseText = v.PhaseText,
            RemainingSec = v.RemainingSec,
            ElapsedSec = v.ElapsedSec,
            StartTimeSec = v.StartTimeSec,
            TargetTempC = v.TargetTempC,
            SpinRpm = v.SpinRpm,
            DryingStepRaw = v.DryingStepRaw,
            DoorOpen = v.DoorOpen,
            SignalFailure = v.SignalFailure,
            SignalInfo = v.SignalInfo,
            EcoEnergyKwh = v.EcoEnergyKwh,
            EcoWaterL = v.EcoWaterL,
            EcoEnergyForecast = v.EcoEnergyForecast,
            EcoWaterForecast = v.EcoWaterForecast,
            RemoteFullControl = v.RemoteFullControl,
            RemoteSmartGrid = v.RemoteSmartGrid,
            RemoteMobileStart = v.RemoteMobileStart,
        });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<MieleStateSnapshot?> LatestSnapshotAsync(string deviceId, CancellationToken ct)
    {
        var e = await _db.MieleStateSnapshots.AsNoTracking().Where(s => s.DeviceId == deviceId)
            .OrderByDescending(s => s.Ts).ThenByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        return e is null ? null : ToModel(e);
    }

    public async Task<IReadOnlyList<MieleStateSnapshot>> ListSnapshotsAsync(string deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.MieleStateSnapshots.AsNoTracking().Where(s => s.DeviceId == deviceId && s.Ts >= from && s.Ts <= to)
            .OrderBy(s => s.Ts).ThenBy(s => s.Id).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<MieleCycle> AddCycleAsync(MieleCycle cycle, CancellationToken ct)
    {
        var e = new MieleCycleEntity();
        Fill(e, cycle);
        _db.MieleCycles.Add(e);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return cycle with { Id = e.Id };
    }

    public async Task UpdateCycleAsync(MieleCycle cycle, CancellationToken ct)
    {
        var e = await _db.MieleCycles.FindAsync(new object?[] { cycle.Id }, ct) ?? throw new KeyNotFoundException("Miele cycle not found");
        Fill(e, cycle);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<MieleCycle>> ListOpenCyclesAsync(CancellationToken ct)
    {
        var rows = await _db.MieleCycles.AsNoTracking().Where(c => c.EndTs == null).OrderBy(c => c.StartTs).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<MieleCycle>> ListCyclesAsync(string deviceId, int take, CancellationToken ct)
    {
        var rows = await _db.MieleCycles.AsNoTracking().Where(c => c.DeviceId == deviceId)
            .OrderByDescending(c => c.StartTs).ThenByDescending(c => c.Id).Take(Math.Max(1, take)).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public Task<int> CountClosedCyclesAsync(string deviceId, CancellationToken ct) =>
        _db.MieleCycles.AsNoTracking().CountAsync(c => c.DeviceId == deviceId && c.EndTs != null, ct);

    private static MieleDevice ToModel(MieleDeviceEntity e) => new(e.Id, e.TypeRaw, e.TypeText, e.TechType, e.DeviceName,
        e.Alias, e.PlugId, e.IsArchived, e.LastSeenUtc is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null);

    private static MieleStateSnapshot ToModel(MieleStateSnapshotEntity e) => new(DateTimeOffset.FromUnixTimeSeconds(e.Ts), e.DeviceId,
        new MieleStateValues(e.StatusRaw, e.StatusText, e.ProgramTypeRaw, e.ProgramTypeText, e.ProgramIdRaw, e.ProgramIdText,
            e.PhaseRaw, e.PhaseText, e.RemainingSec, e.ElapsedSec, e.StartTimeSec, e.TargetTempC, e.SpinRpm, e.DryingStepRaw,
            e.DoorOpen, e.SignalFailure, e.SignalInfo, e.EcoEnergyKwh, e.EcoWaterL, e.EcoEnergyForecast, e.EcoWaterForecast,
            e.RemoteFullControl, e.RemoteSmartGrid, e.RemoteMobileStart));

    private static void Fill(MieleCycleEntity e, MieleCycle c)
    {
        e.DeviceId = c.DeviceId;
        e.StartTs = c.StartUtc.ToUnixTimeSeconds();
        e.EndTs = c.EndUtc?.ToUnixTimeSeconds();
        e.LastRunningTs = c.LastRunningUtc.ToUnixTimeSeconds();
        e.ProgramRaw = c.ProgramRaw;
        e.Program = c.Program;
        e.PhasesJson = PhasesToJson(c.Phases);
        e.EnergyKwh = c.EnergyKwh;
        e.WaterL = c.WaterL;
        e.PlugEnergyWh = c.PlugEnergyWh;
        e.EndReason = c.EndReason;
    }

    private static MieleCycle ToModel(MieleCycleEntity e) => new(e.Id, e.DeviceId, DateTimeOffset.FromUnixTimeSeconds(e.StartTs),
        e.EndTs is { } end ? DateTimeOffset.FromUnixTimeSeconds(end) : null, DateTimeOffset.FromUnixTimeSeconds(e.LastRunningTs),
        e.ProgramRaw, e.Program, PhasesFromJson(e.PhasesJson), e.EnergyKwh, e.WaterL, e.PlugEnergyWh, e.EndReason);

    private sealed record PhaseRow(
        [property: JsonPropertyName("phaseRaw")] int? PhaseRaw,
        [property: JsonPropertyName("phaseText")] string PhaseText,
        [property: JsonPropertyName("fromTs")] long FromTs,
        [property: JsonPropertyName("toTs")] long? ToTs);

    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string PhasesToJson(IReadOnlyList<MielePhaseSpan> phases) =>
        JsonSerializer.Serialize(phases.Select(p => new PhaseRow(p.PhaseRaw, p.PhaseText, p.FromUtc.ToUnixTimeSeconds(), p.ToUtc?.ToUnixTimeSeconds())), Json);

    public static IReadOnlyList<MielePhaseSpan> PhasesFromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<MielePhaseSpan>();
        try
        {
            var rows = JsonSerializer.Deserialize<List<PhaseRow>>(json, Json) ?? new();
            return rows.Select(r => new MielePhaseSpan(r.PhaseRaw, r.PhaseText ?? "", DateTimeOffset.FromUnixTimeSeconds(r.FromTs),
                r.ToTs is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null)).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<MielePhaseSpan>();
        }
    }
}
