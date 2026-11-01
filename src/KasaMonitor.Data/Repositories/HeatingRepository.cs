using System.Globalization;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class HeatingRepository : IHeatingRepository
{
    private readonly KasaDbContext _db;
    public HeatingRepository(KasaDbContext db) => _db = db;

    public async Task<IReadOnlyList<HeatingDevice>> ListDevicesAsync(CancellationToken ct)
    {
        var rows = await _db.HeatingDevices.AsNoTracking().ToListAsync(ct);
        return rows.OrderBy(d => d.Name, StringComparer.Ordinal).Select(ToModel).ToList();
    }

    public async Task<HeatingDevice> UpsertDeviceAsync(HeatingDevice device, CancellationToken ct)
    {
        var row = await _db.HeatingDevices.FirstOrDefaultAsync(d => d.Provider == device.Provider && d.InstallationId == device.InstallationId
            && d.GatewaySerial == device.GatewaySerial && d.DeviceId == device.DeviceId, ct);
        if (row is null)
        {
            row = new HeatingDeviceEntity
            {
                Id = device.Id == Guid.Empty ? Guid.NewGuid() : device.Id,
                Provider = device.Provider,
                InstallationId = device.InstallationId,
                GatewaySerial = device.GatewaySerial,
                DeviceId = device.DeviceId,
                Name = device.Name,
                Enabled = device.Enabled,
            };
            _db.HeatingDevices.Add(row);
        }
        row.ModelId = device.ModelId;
        if (device.LastSeenUtc is { } seen) row.LastSeenUtc = seen.ToUnixTimeSeconds();
        row.LastError = device.LastError;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return ToModel(row);
    }

    public async Task UpdateDeviceStatusAsync(Guid deviceId, DateTimeOffset? lastSeenUtc, string? lastError, CancellationToken ct)
    {
        var row = await _db.HeatingDevices.FindAsync(new object?[] { deviceId }, ct);
        if (row is null) return;
        if (lastSeenUtc is { } seen) row.LastSeenUtc = seen.ToUnixTimeSeconds();
        row.LastError = lastError;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task SaveReadingAsync(HeatingReading r, CancellationToken ct)
    {
        var ts = r.TsUtc.ToUnixTimeSeconds();
        var row = await _db.HeatingReadings.FindAsync(new object?[] { r.DeviceId, ts }, ct);
        if (row is null)
        {
            row = new HeatingReadingEntity { DeviceId = r.DeviceId, Ts = ts };
            _db.HeatingReadings.Add(row);
        }
        row.BoilerTempC = r.BoilerTempC;
        row.BoilerTargetC = r.BoilerTargetC;
        row.SupplyTempC = r.SupplyTempC;
        row.ReturnTempC = r.ReturnTempC;
        row.OutsideTempC = r.OutsideTempC;
        row.RoomTempC = r.RoomTempC;
        row.DhwTempC = r.DhwTempC;
        row.DhwTargetC = r.DhwTargetC;
        row.BurnerActive = r.BurnerActive;
        row.ModulationPct = r.ModulationPct;
        row.PumpPct = r.PumpPct;
        row.PressureBar = r.PressureBar;
        row.BurnerStarts = r.BurnerStarts;
        row.BurnerHours = r.BurnerHours;
        row.OperatingMode = r.OperatingMode;
        row.ActiveProgram = r.ActiveProgram;
        row.DhwMode = r.DhwMode;
        row.ActiveError = r.ActiveError;
        row.StatusCode = r.StatusCode;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<HeatingReading>> ListReadingsAsync(Guid deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.HeatingReadings.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.Ts >= from && r.Ts <= to)
            .OrderBy(r => r.Ts)
            .ToListAsync(ct);
        return rows.Select(r => new HeatingReading(r.DeviceId, DateTimeOffset.FromUnixTimeSeconds(r.Ts),
            r.BoilerTempC, r.BoilerTargetC, r.SupplyTempC, r.ReturnTempC, r.OutsideTempC, r.RoomTempC, r.DhwTempC, r.DhwTargetC,
            r.BurnerActive, r.ModulationPct, r.PumpPct, r.PressureBar, r.BurnerStarts, r.BurnerHours,
            r.OperatingMode, r.ActiveProgram, r.DhwMode, r.ActiveError, r.StatusCode)).ToList();
    }

    public async Task<IReadOnlyList<HeatingDayDecrease>> UpsertDailyAsync(IReadOnlyList<HeatingEnergyDay> days, CancellationToken ct)
    {
        var decreases = new List<HeatingDayDecrease>();
        if (days.Count == 0) return decreases;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var d in days)
        {
            var key = DayKey(d.Day);
            var row = await _db.HeatingEnergyDaily.FindAsync(new object?[] { d.DeviceId, key }, ct);
            if (row is null)
            {
                row = new HeatingEnergyDailyEntity { DeviceId = d.DeviceId, Day = key };
                _db.HeatingEnergyDaily.Add(row);
            }
            if (d.GasHeatingMaxM3 is { } gh && row.GasHeatingMaxM3 is { } sh && gh < sh)
                decreases.Add(new HeatingDayDecrease(d.DeviceId, d.Day, HeatingKinds.Heating, sh, row.GasHeatingLastM3, gh));
            if (d.GasDhwMaxM3 is { } gd && row.GasDhwMaxM3 is { } sd && gd < sd)
                decreases.Add(new HeatingDayDecrease(d.DeviceId, d.Day, HeatingKinds.Dhw, sd, row.GasDhwLastM3, gd));
            row.GasHeatingMaxM3 = Max(row.GasHeatingMaxM3, d.GasHeatingMaxM3);
            row.GasDhwMaxM3 = Max(row.GasDhwMaxM3, d.GasDhwMaxM3);
            row.GasHeatingLastM3 = d.GasHeatingLastM3 ?? d.GasHeatingMaxM3 ?? row.GasHeatingLastM3;
            row.GasDhwLastM3 = d.GasDhwLastM3 ?? d.GasDhwMaxM3 ?? row.GasDhwLastM3;
            row.HeatHeatingKwh = Max(row.HeatHeatingKwh, d.HeatHeatingKwh);
            row.HeatDhwKwh = Max(row.HeatDhwKwh, d.HeatDhwKwh);
            row.PowerHeatingKwh = Max(row.PowerHeatingKwh, d.PowerHeatingKwh);
            row.PowerDhwKwh = Max(row.PowerDhwKwh, d.PowerDhwKwh);
            row.Source = d.Source;
            row.UpdatedTs = now;
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return decreases;
    }

    public async Task<IReadOnlyList<HeatingEnergyDay>> ListDailyAsync(Guid deviceId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        string f = DayKey(from), t = DayKey(to);
        var rows = await _db.HeatingEnergyDaily.AsNoTracking()
            .Where(d => d.DeviceId == deviceId && string.Compare(d.Day, f) >= 0 && string.Compare(d.Day, t) <= 0)
            .ToListAsync(ct);
        return rows.OrderBy(d => d.Day, StringComparer.Ordinal).Select(ToModel).ToList();
    }

    public async Task<HeatingEnergyTotals> GetTotalsAsync(Guid deviceId, DateOnly today, CancellationToken ct)
    {
        var rows = (await _db.HeatingEnergyDaily.AsNoTracking().Where(d => d.DeviceId == deviceId).ToListAsync(ct)).Select(ToModel).ToList();
        return new HeatingEnergyTotals(
            rows.Sum(r => r.GasHeatingFor(today) ?? 0), rows.Sum(r => r.GasDhwFor(today) ?? 0),
            rows.Sum(r => r.HeatHeatingKwh ?? 0), rows.Sum(r => r.HeatDhwKwh ?? 0),
            rows.Sum(r => r.PowerHeatingKwh ?? 0), rows.Sum(r => r.PowerDhwKwh ?? 0));
    }

    public async Task<int> DeleteReadingsBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var cutoff = cutoffUtc.ToUnixTimeSeconds();
        return await _db.HeatingReadings.Where(r => r.Ts < cutoff).ExecuteDeleteAsync(ct);
    }

    public async Task UpsertPeriodsAsync(IReadOnlyList<HeatingEnergyPeriod> periods, CancellationToken ct)
    {
        foreach (var p in periods)
        {
            var row = await _db.HeatingEnergyPeriods.FindAsync(new object?[] { p.DeviceId, p.Kind, p.Purpose, p.Period }, ct);
            if (row is null)
            {
                row = new HeatingEnergyPeriodEntity { DeviceId = p.DeviceId, Kind = p.Kind, Purpose = p.Purpose, Period = p.Period };
                _db.HeatingEnergyPeriods.Add(row);
            }
            row.Value = p.Value;
            row.Unit = p.Unit;
            row.UpdatedTs = p.UpdatedUtc.ToUnixTimeSeconds();
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<HeatingEnergyPeriod>> ListPeriodsAsync(Guid deviceId, CancellationToken ct)
    {
        var rows = await _db.HeatingEnergyPeriods.AsNoTracking().Where(p => p.DeviceId == deviceId).ToListAsync(ct);
        return rows.Select(p => new HeatingEnergyPeriod(p.DeviceId, p.Kind, p.Purpose, p.Period, p.Value, p.Unit,
            DateTimeOffset.FromUnixTimeSeconds(p.UpdatedTs))).ToList();
    }

    public async Task SaveRawAsync(Guid deviceId, DateTimeOffset tsUtc, string json, CancellationToken ct)
    {
        var row = await _db.HeatingRawFeatures.FindAsync(new object?[] { deviceId }, ct);
        if (row is null)
        {
            row = new HeatingRawFeaturesEntity { DeviceId = deviceId };
            _db.HeatingRawFeatures.Add(row);
        }
        row.Ts = tsUtc.ToUnixTimeSeconds();
        row.Json = json;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<(DateTimeOffset TsUtc, string Json)?> GetRawAsync(Guid deviceId, CancellationToken ct)
    {
        var row = await _db.HeatingRawFeatures.AsNoTracking().FirstOrDefaultAsync(r => r.DeviceId == deviceId, ct);
        return row is null ? null : (DateTimeOffset.FromUnixTimeSeconds(row.Ts), row.Json);
    }

    private static double? Max(double? stored, double? incoming) =>
        incoming is not { } v ? stored : stored is { } s && s > v ? s : v;

    private static string DayKey(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static HeatingDevice ToModel(HeatingDeviceEntity d) => new(d.Id, d.Provider, d.InstallationId, d.GatewaySerial, d.DeviceId,
        d.ModelId, d.Name, d.Enabled, d.LastSeenUtc is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null, d.LastError);

    private static HeatingEnergyDay ToModel(HeatingEnergyDailyEntity d) => new(d.DeviceId,
        DateOnly.ParseExact(d.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        d.GasHeatingMaxM3, d.GasDhwMaxM3, d.HeatHeatingKwh, d.HeatDhwKwh, d.PowerHeatingKwh, d.PowerDhwKwh, d.Source,
        d.GasHeatingLastM3, d.GasDhwLastM3);
}
