using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
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
