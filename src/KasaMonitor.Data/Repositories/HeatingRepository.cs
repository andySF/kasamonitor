using System.Globalization;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Reports;
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

    public async Task<bool> SetDeviceEnabledAsync(Guid deviceId, bool enabled, CancellationToken ct)
    {
        var row = await _db.HeatingDevices.FindAsync(new object?[] { deviceId }, ct);
        if (row is null) return false;
        row.Enabled = enabled;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return true;
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
        row.GasHeatingTodayM3 = r.GasHeatingTodayM3;
        row.GasDhwTodayM3 = r.GasDhwTodayM3;
        row.PowerTodayKwh = r.PowerTodayKwh;
        row.HeatTodayKwh = r.HeatTodayKwh;
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
            r.OperatingMode, r.ActiveProgram, r.DhwMode, r.ActiveError, r.StatusCode,
            r.GasHeatingTodayM3, r.GasDhwTodayM3, r.PowerTodayKwh, r.HeatTodayKwh)).ToList();
    }

    public async Task<IReadOnlyList<HeatingCounterReading>> ListCounterReadingsAsync(Guid deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.HeatingReadings.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.Ts >= from && r.Ts <= to)
            .OrderBy(r => r.Ts)
            .Select(r => new { r.Ts, r.GasHeatingTodayM3, r.GasDhwTodayM3, r.PowerTodayKwh, r.HeatTodayKwh })
            .ToListAsync(ct);
        return rows.Select(r => new HeatingCounterReading(DateTimeOffset.FromUnixTimeSeconds(r.Ts),
            r.GasHeatingTodayM3, r.GasDhwTodayM3, r.PowerTodayKwh, r.HeatTodayKwh)).ToList();
    }

    public async Task<IReadOnlyList<KasaMonitor.Core.History.BurnerCounterHour>> ListBurnerCounterHoursAsync(Guid deviceId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.HeatingReadings.AsNoTracking()
            .Where(r => r.DeviceId == deviceId && r.Ts >= from && r.Ts <= to && (r.BurnerHours != null || r.BurnerStarts != null))
            .GroupBy(r => r.Ts / 3600)
            .Select(g => new
            {
                Hour = g.Key,
                MinHours = g.Min(r => r.BurnerHours),
                MaxHours = g.Max(r => r.BurnerHours),
                MinStarts = g.Min(r => r.BurnerStarts),
                MaxStarts = g.Max(r => r.BurnerStarts),
            })
            .ToListAsync(ct);
        return rows.OrderBy(r => r.Hour)
            .Select(r => new KasaMonitor.Core.History.BurnerCounterHour(DateTimeOffset.FromUnixTimeSeconds(r.Hour * 3600), r.MinHours, r.MaxHours, r.MinStarts, r.MaxStarts))
            .ToList();
    }

    /// <summary>One row of <see cref="ListReadingChangesAsync"/>.</summary>
    private sealed class ChangeRow
    {
        public long Ts { get; set; }
        public string? ActiveError { get; set; }
        public long? PrevTs { get; set; }
        public string? PrevError { get; set; }
    }

    public async Task<IReadOnlyList<KasaMonitor.Core.History.HeatingReadingChange>> ListReadingChangesAsync(Guid deviceId, DateTimeOffset fromUtc,
        DateTimeOffset toUtc, TimeSpan gap, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds(), gapSeconds = (long)gap.TotalSeconds;
        // DeviceId is stored as upper-case TEXT (the SQLite GUID mapping of EF).
        var device = deviceId.ToString().ToUpperInvariant();
        var rows = await _db.Database.SqlQuery<ChangeRow>($"""
            SELECT Ts, ActiveError, PrevTs, PrevError FROM (
                SELECT Ts,
                       NULLIF(TRIM(ActiveError), '') AS ActiveError,
                       LAG(Ts) OVER (ORDER BY Ts) AS PrevTs,
                       LAG(NULLIF(TRIM(ActiveError), '')) OVER (ORDER BY Ts) AS PrevError
                FROM HeatingReadings
                WHERE DeviceId = {device} AND Ts >= {from} AND Ts <= {to})
            WHERE PrevTs IS NULL OR Ts - PrevTs > {gapSeconds} OR IFNULL(ActiveError, '') <> IFNULL(PrevError, '')
            ORDER BY Ts
            """).ToListAsync(ct);
        return rows.Select(r => new KasaMonitor.Core.History.HeatingReadingChange(DateTimeOffset.FromUnixTimeSeconds(r.Ts), r.ActiveError,
            r.PrevTs is { } p ? DateTimeOffset.FromUnixTimeSeconds(p) : null, r.PrevError)).ToList();
    }

    public async Task<IReadOnlyList<HeatingMonthSums>> SumDailyByMonthAsync(Guid deviceId, int yearFrom, int yearTo, DateOnly today, CancellationToken ct)
    {
        string f = DayKey(new DateOnly(yearFrom, 1, 1)), t = DayKey(new DateOnly(yearTo, 12, 31)), now = DayKey(today);
        // One grouped query; gas counted as in HeatingEnergyDay.GasHeatingFor: the last value from today on, the highest before.
        var rows = await _db.HeatingEnergyDaily.AsNoTracking()
            .Where(d => d.DeviceId == deviceId && string.Compare(d.Day, f) >= 0 && string.Compare(d.Day, t) <= 0)
            .GroupBy(d => d.Day.Substring(0, 7))
            .Select(g => new
            {
                Month = g.Key,
                GasHeating = g.Sum(d => string.Compare(d.Day, now) >= 0 ? (d.GasHeatingLastM3 ?? d.GasHeatingMaxM3) : d.GasHeatingMaxM3),
                GasDhw = g.Sum(d => string.Compare(d.Day, now) >= 0 ? (d.GasDhwLastM3 ?? d.GasDhwMaxM3) : d.GasDhwMaxM3),
                HeatHeating = g.Sum(d => d.HeatHeatingKwh),
                HeatDhw = g.Sum(d => d.HeatDhwKwh),
                PowerHeating = g.Sum(d => d.PowerHeatingKwh),
                PowerDhw = g.Sum(d => d.PowerDhwKwh),
                GasHeatingCount = g.Count(d => d.GasHeatingMaxM3 != null),
                GasDhwCount = g.Count(d => d.GasDhwMaxM3 != null),
                HeatHeatingCount = g.Count(d => d.HeatHeatingKwh != null),
                HeatDhwCount = g.Count(d => d.HeatDhwKwh != null),
                PowerHeatingCount = g.Count(d => d.PowerHeatingKwh != null),
                PowerDhwCount = g.Count(d => d.PowerDhwKwh != null),
            })
            .ToListAsync(ct);
        // A purpose without any stored value in the month stays null (not 0).
        static double? V(double? sum, int count) => count == 0 ? null : sum ?? 0;
        return rows.Select(r => new HeatingMonthSums(
                int.Parse(r.Month[..4], CultureInfo.InvariantCulture), int.Parse(r.Month[5..7], CultureInfo.InvariantCulture),
                V(r.GasHeating, r.GasHeatingCount), V(r.GasDhw, r.GasDhwCount), V(r.HeatHeating, r.HeatHeatingCount),
                V(r.HeatDhw, r.HeatDhwCount), V(r.PowerHeating, r.PowerHeatingCount), V(r.PowerDhw, r.PowerDhwCount)))
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .ToList();
    }

    public async Task<DateOnly?> FirstDailyDayAsync(Guid deviceId, CancellationToken ct)
    {
        var first = await _db.HeatingEnergyDaily.AsNoTracking().Where(d => d.DeviceId == deviceId)
            .OrderBy(d => d.Day).Select(d => d.Day).FirstOrDefaultAsync(ct);
        return first is null ? null : DateOnly.ParseExact(first, "yyyy-MM-dd", CultureInfo.InvariantCulture);
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

    public async Task SetDailyGasAsync(Guid deviceId, DateOnly day, string purpose, double maxM3, double lastM3, CancellationToken ct)
    {
        if (purpose is not (HeatingKinds.Heating or HeatingKinds.Dhw)) throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null);
        var key = DayKey(day);
        var row = await _db.HeatingEnergyDaily.FindAsync(new object?[] { deviceId, key }, ct);
        if (row is null)
        {
            row = new HeatingEnergyDailyEntity { DeviceId = deviceId, Day = key, Source = HeatingKinds.SourceSummary };
            _db.HeatingEnergyDaily.Add(row);
        }
        if (purpose == HeatingKinds.Heating)
        {
            row.GasHeatingMaxM3 = maxM3;
            row.GasHeatingLastM3 = lastM3;
        }
        else
        {
            row.GasDhwMaxM3 = maxM3;
            row.GasDhwLastM3 = lastM3;
        }
        row.UpdatedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task SetDailyKwhAsync(Guid deviceId, DateOnly day, string kind, string purpose, double? kwh, CancellationToken ct)
    {
        if (kind is not (HeatingKinds.Heat or HeatingKinds.Power)) throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        if (purpose is not (HeatingKinds.Heating or HeatingKinds.Dhw)) throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null);
        var row = await _db.HeatingEnergyDaily.FindAsync(new object?[] { deviceId, DayKey(day) }, ct);
        if (row is null) return;
        switch (kind, purpose)
        {
            case (HeatingKinds.Heat, HeatingKinds.Heating): row.HeatHeatingKwh = kwh; break;
            case (HeatingKinds.Heat, _): row.HeatDhwKwh = kwh; break;
            case (_, HeatingKinds.Heating): row.PowerHeatingKwh = kwh; break;
            default: row.PowerDhwKwh = kwh; break;
        }
        row.UpdatedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
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
