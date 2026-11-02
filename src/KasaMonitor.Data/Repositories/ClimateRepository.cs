using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.History;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class ClimateRepository : IClimateRepository
{
    private readonly KasaDbContext _db;
    public ClimateRepository(KasaDbContext db) => _db = db;

    public async Task<IReadOnlyList<ClimateSensor>> ListSensorsAsync(bool includeArchived, CancellationToken ct)
    {
        var rows = await _db.ClimateSensors.AsNoTracking().Where(s => includeArchived || !s.IsArchived).ToListAsync(ct);
        return rows.OrderBy(s => s.IsOutdoor).ThenBy(s => s.Room, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id).Select(ToModel).ToList();
    }

    public async Task<ClimateSensor> GetOrAddSensorAsync(string source, string externalId, string name, string room, bool isOutdoor, CancellationToken ct)
    {
        var e = await _db.ClimateSensors.FirstOrDefaultAsync(s => s.Source == source && s.ExternalId == externalId, ct);
        if (e is null)
        {
            e = new ClimateSensorEntity
            {
                Id = Guid.NewGuid(),
                Source = source,
                ExternalId = externalId,
                Name = string.IsNullOrWhiteSpace(name) ? externalId : name.Trim(),
                Room = room.Trim(),
                IsOutdoor = isOutdoor,
            };
            _db.ClimateSensors.Add(e);
            await _db.SaveChangesAsync(ct);
        }
        var model = ToModel(e);
        _db.ChangeTracker.Clear();
        return model;
    }

    public async Task MarkSeenAsync(Guid sensorId, DateTimeOffset seenUtc, int? batteryPercent, CancellationToken ct)
    {
        var e = await _db.ClimateSensors.FindAsync(new object?[] { sensorId }, ct) ?? throw new KeyNotFoundException("Climate sensor not found");
        e.LastSeenUtc = seenUtc.ToUnixTimeSeconds();
        if (batteryPercent is not null) e.BatteryPercent = batteryPercent;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<ClimateSensor> UpdateSensorAsync(Guid sensorId, string name, string room, bool isOutdoor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Numele senzorului nu poate fi gol.", nameof(name));
        return await ChangeAsync(sensorId, e =>
        {
            e.Name = name.Trim();
            e.Room = (room ?? "").Trim();
            e.IsOutdoor = isOutdoor;
        }, ct);
    }

    public Task<ClimateSensor> SetArchivedAsync(Guid sensorId, bool archived, CancellationToken ct) =>
        ChangeAsync(sensorId, e => e.IsArchived = archived, ct);

    private async Task<ClimateSensor> ChangeAsync(Guid sensorId, Action<ClimateSensorEntity> change, CancellationToken ct)
    {
        var e = await _db.ClimateSensors.FindAsync(new object?[] { sensorId }, ct) ?? throw new KeyNotFoundException("Climate sensor not found");
        change(e);
        await _db.SaveChangesAsync(ct);
        var model = ToModel(e);
        _db.ChangeTracker.Clear();
        return model;
    }

    public async Task AddReadingAsync(ClimateReading r, CancellationToken ct)
    {
        var ts = r.TsUtc.ToUnixTimeSeconds();
        if (await _db.ClimateReadings.AsNoTracking().AnyAsync(x => x.SensorId == r.SensorId && x.Ts == ts, ct)) return;
        _db.ClimateReadings.Add(new ClimateReadingEntity
        {
            SensorId = r.SensorId,
            Ts = ts,
            TemperatureC = r.TemperatureC,
            HumidityPercent = r.HumidityPercent,
            BatteryPercent = r.BatteryPercent,
        });
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyDictionary<Guid, ClimateReading>> LatestReadingsAsync(CancellationToken ct)
    {
        // One row per sensor: the newest Ts (primary key (SensorId, Ts) makes the max per sensor an index lookup).
        var latest = await _db.ClimateReadings.AsNoTracking()
            .GroupBy(r => r.SensorId)
            .Select(g => new { SensorId = g.Key, Ts = g.Max(r => r.Ts) })
            .ToListAsync(ct);
        var result = new Dictionary<Guid, ClimateReading>();
        foreach (var l in latest)
        {
            var row = await _db.ClimateReadings.AsNoTracking().FirstAsync(r => r.SensorId == l.SensorId && r.Ts == l.Ts, ct);
            result[l.SensorId] = ToModel(row);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ClimateReading>>> ListReadingsAsync(IReadOnlyCollection<Guid> sensorIds,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var result = new Dictionary<Guid, IReadOnlyList<ClimateReading>>();
        foreach (var id in sensorIds.Distinct())
        {
            var rows = await _db.ClimateReadings.AsNoTracking()
                .Where(r => r.SensorId == id && r.Ts >= from && r.Ts <= to)
                .OrderBy(r => r.Ts)
                .ToListAsync(ct);
            result[id] = rows.Select(ToModel).ToList();
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> FirstReadingsAsync(CancellationToken ct)
    {
        var rows = await _db.ClimateReadings.AsNoTracking().GroupBy(r => r.SensorId).Select(g => new { g.Key, Ts = g.Min(r => r.Ts) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => DateTimeOffset.FromUnixTimeSeconds(r.Ts));
    }

    public async Task UpsertDailyAsync(IReadOnlyList<ClimateDayStats> days, CancellationToken ct)
    {
        if (days.Count == 0) return;
        var keys = days.Select(d => (d.SensorId, DayKey(d.Day))).ToHashSet();
        var sensors = keys.Select(k => k.SensorId).Distinct().ToList();
        var dayKeys = keys.Select(k => k.Item2).Distinct().ToList();
        var existing = (await _db.ClimateDaily.Where(x => sensors.Contains(x.SensorId) && dayKeys.Contains(x.Day)).ToListAsync(ct))
            .ToDictionary(x => (x.SensorId, x.Day));
        foreach (var d in days)
        {
            var key = (d.SensorId, DayKey(d.Day));
            if (!existing.TryGetValue(key, out var row))
            {
                row = new ClimateDailyEntity { SensorId = d.SensorId, Day = key.Item2 };
                _db.ClimateDaily.Add(row);
                existing[key] = row;
            }
            row.MinC = d.Stats.MinC;
            row.AvgC = d.Stats.AvgC;
            row.MaxC = d.Stats.MaxC;
            row.MinHumidity = d.Stats.MinHumidity;
            row.AvgHumidity = d.Stats.AvgHumidity;
            row.MaxHumidity = d.Stats.MaxHumidity;
            row.Count = d.Stats.Count;
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<ClimateDayStats>> ListDailyAsync(IReadOnlyCollection<Guid> sensorIds, DateOnly from, DateOnly to, CancellationToken ct)
    {
        string f = DayKey(from), t = DayKey(to);
        var ids = sensorIds.Distinct().ToList();
        var rows = await _db.ClimateDaily.AsNoTracking()
            .Where(x => ids.Contains(x.SensorId) && string.Compare(x.Day, f) >= 0 && string.Compare(x.Day, t) <= 0)
            .ToListAsync(ct);
        return rows.Select(x => new ClimateDayStats(x.SensorId, ParseDay(x.Day),
                new ClimateStats(x.MinC, x.AvgC, x.MaxC, x.MinHumidity, x.AvgHumidity, x.MaxHumidity, x.Count)))
            .OrderBy(x => x.SensorId).ThenBy(x => x.Day).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, DateOnly>> LastDailyDaysAsync(CancellationToken ct)
    {
        var rows = await _db.ClimateDaily.AsNoTracking().GroupBy(x => x.SensorId).Select(g => new { g.Key, Day = g.Max(x => x.Day) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => ParseDay(r.Day!));
    }

    private static string DayKey(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public Task<int> DeleteReadingsBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var cutoff = cutoffUtc.ToUnixTimeSeconds();
        return _db.ClimateReadings.Where(r => r.Ts < cutoff).ExecuteDeleteAsync(ct);
    }

    private static ClimateSensor ToModel(ClimateSensorEntity e) => new(e.Id, e.Source, e.ExternalId, e.Name, e.Room, e.IsOutdoor, e.IsArchived,
        e.LastSeenUtc is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null, e.BatteryPercent);

    private static ClimateReading ToModel(ClimateReadingEntity e) =>
        new(e.SensorId, DateTimeOffset.FromUnixTimeSeconds(e.Ts), e.TemperatureC, e.HumidityPercent, e.BatteryPercent);
}
