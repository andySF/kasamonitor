using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class GenericRepository : IGenericRepository
{
    /// <summary>R10 (round 2): only finite numbers reach the database (SQL Server refuses NaN / ±∞ in a float column).</summary>
    internal static double? Finite(double? v) => v is { } x && double.IsFinite(x) ? x : null;

    /// <summary>A text cut to its column's length (the lengths of <see cref="KasaDbContext"/>).</summary>
    internal static string Cut(string text, int max) => text.Length <= max ? text : text[..max];

    internal static string? CutOrNull(string? text, int max) => text is null ? null : Cut(text, max);

    private readonly KasaDbContext _db;
    public GenericRepository(KasaDbContext db) => _db = db;

    public async Task<IReadOnlyList<GenericDevice>> ListDevicesAsync(bool includeArchived, CancellationToken ct)
    {
        var rows = await _db.GenericDevices.AsNoTracking().Where(d => includeArchived || !d.IsArchived).OrderBy(d => d.Id).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<GenericDevice?> GetDeviceAsync(long id, CancellationToken ct) =>
        await _db.GenericDevices.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } e ? ToModel(e) : null;

    public async Task<GenericDevice> AddDeviceAsync(string integration, string externalId, string name, string? manufacturer, string? model, string? firmware,
        string? viaDevice, string room, DeviceCategory autoCategory, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var e = await _db.GenericDevices.FirstOrDefaultAsync(d => d.Integration == integration && d.ExternalId == externalId, ct);
        if (e is null)
        {
            e = new GenericDeviceEntity
            {
                Integration = integration,
                ExternalId = externalId,
                Name = Cut(string.IsNullOrWhiteSpace(name) ? externalId : name.Trim(), 200),
                Room = Cut(room.Trim(), 100),
                Manufacturer = CutOrNull(manufacturer, 200),
                Model = CutOrNull(model, 200),
                Firmware = CutOrNull(firmware, 200),
                ViaDevice = CutOrNull(viaDevice, 200),
                AutoCategory = autoCategory.ToString(),
                FirstSeenUtc = nowUtc.ToUnixTimeSeconds(),
                LastSeenUtc = nowUtc.ToUnixTimeSeconds(),
            };
            _db.GenericDevices.Add(e);
            await _db.SaveChangesAsync(ct);
        }
        var model1 = ToModel(e);
        _db.ChangeTracker.Clear();
        return model1;
    }

    public async Task UpdateDeviceInfoAsync(long id, string defaultName, string? manufacturer, string? model, string? firmware, string? viaDevice,
        DeviceCategory autoCategory, DateTimeOffset seenUtc, CancellationToken ct)
    {
        var e = await _db.GenericDevices.FindAsync(new object?[] { id }, ct);
        if (e is null) return;
        if (!e.NameIsCustom && !string.IsNullOrWhiteSpace(defaultName)) e.Name = Cut(defaultName.Trim(), 200);
        e.Manufacturer = CutOrNull(manufacturer, 200) ?? e.Manufacturer;
        e.Model = CutOrNull(model, 200) ?? e.Model;
        e.Firmware = CutOrNull(firmware, 200) ?? e.Firmware;
        e.ViaDevice = CutOrNull(viaDevice, 200) ?? e.ViaDevice;
        e.AutoCategory = autoCategory.ToString();
        e.LastSeenUtc = seenUtc.ToUnixTimeSeconds();
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<GenericDevice> UpdateDeviceAsync(long id, string name, string room, DeviceCategory? categoryOverride, CancellationToken ct)
    {
        if (categoryOverride is { } c && !GenericClassifier.AllowedOverrides.Contains(c))
            throw new ArgumentException($"Categoria „{DeviceCategories.Name(c)}” nu se poate alege pentru un dispozitiv generic.", nameof(categoryOverride));
        return await ChangeAsync(id, e =>
        {
            var custom = !string.IsNullOrWhiteSpace(name);
            if (custom) e.Name = Cut(name.Trim(), 200);
            e.NameIsCustom = custom;
            e.Room = Cut((room ?? "").Trim(), 100);
            e.CategoryOverride = categoryOverride?.ToString();
        }, ct);
    }

    public Task<GenericDevice> SetArchivedAsync(long id, bool archived, CancellationToken ct) => ChangeAsync(id, e => e.IsArchived = archived, ct);

    private async Task<GenericDevice> ChangeAsync(long id, Action<GenericDeviceEntity> change, CancellationToken ct)
    {
        var e = await _db.GenericDevices.FindAsync(new object?[] { id }, ct) ?? throw new KeyNotFoundException("Dispozitivul generic nu există.");
        change(e);
        await _db.SaveChangesAsync(ct);
        var model = ToModel(e);
        _db.ChangeTracker.Clear();
        return model;
    }

    public async Task<IReadOnlyList<GenericEntityRow>> ListEntitiesAsync(long? deviceId, CancellationToken ct)
    {
        var rows = await _db.GenericEntities.AsNoTracking().Where(e => deviceId == null || e.DeviceId == deviceId).OrderBy(e => e.Id).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<GenericEntityRow>> UpsertEntitiesAsync(long deviceId, string integration, IReadOnlyList<GenericEntityUpsert> entities, CancellationToken ct)
    {
        var ids = entities.Select(e => e.ExternalId).ToList();
        var existing = await _db.GenericEntities.Where(e => e.Integration == integration && ids.Contains(e.ExternalId)).ToDictionaryAsync(e => e.ExternalId, ct);
        foreach (var u in entities)
        {
            if (!existing.TryGetValue(u.ExternalId, out var e))
            {
                e = new GenericEntityEntity { Integration = integration, ExternalId = u.ExternalId };
                _db.GenericEntities.Add(e);
                existing[u.ExternalId] = e;
            }
            var d = u.Description;
            e.DeviceId = deviceId;
            e.Component = Cut(u.Component, 32);
            e.Name = Cut(d.Name, 200);
            e.Kind = d.Kind.ToString();
            e.DataType = d.DataType.ToString();
            e.Class = d.Class.ToString();
            e.ClassRaw = CutOrNull(d.ClassRaw, 64);
            e.Unit = CutOrNull(d.Unit, 32);
            e.StateClass = d.StateClass?.ToString();
            e.Precision = d.Precision;
            e.Category = d.Category.ToString();
            e.Writable = d.Writable;
            e.Problem = CutOrNull(d.Problem, 500);
            e.StateClassInferred = d.StateClassInferred;
            e.DiscoveryJson = u.DiscoveryJson;
            e.IsRemoved = false;
        }
        await _db.SaveChangesAsync(ct);
        var result = existing.Values.Select(ToModel).ToList();
        _db.ChangeTracker.Clear();
        return result;
    }

    public async Task MarkEntitiesRemovedAsync(long deviceId, IReadOnlyCollection<string> externalIds, CancellationToken ct)
    {
        if (externalIds.Count == 0) return;
        var ids = externalIds.ToList();
        await _db.GenericEntities.Where(e => e.DeviceId == deviceId && ids.Contains(e.ExternalId))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsRemoved, true), ct);
    }

    public async Task AddReadingsAsync(IReadOnlyList<GenericReading> readings, CancellationToken ct)
    {
        if (readings.Count == 0) return;
        // One row per entity and second: the last value of a second wins within the batch, an existing row is kept.
        var rows = readings.GroupBy(r => (r.EntityId, Ts: r.TsUtc.ToUnixTimeSeconds())).Select(g => g.Last()).ToList();
        var entityIds = rows.Select(r => r.EntityId).Distinct().ToList();
        var minTs = rows.Min(r => r.TsUtc.ToUnixTimeSeconds());
        var taken = (await _db.GenericReadings.AsNoTracking().Where(r => entityIds.Contains(r.EntityId) && r.Ts >= minTs)
            .Select(r => new { r.EntityId, r.Ts }).ToListAsync(ct)).Select(r => (r.EntityId, r.Ts)).ToHashSet();
        foreach (var r in rows)
        {
            var ts = r.TsUtc.ToUnixTimeSeconds();
            if (!taken.Add((r.EntityId, ts))) continue;
            _db.GenericReadings.Add(new GenericReadingEntity { EntityId = r.EntityId, Ts = ts, Value = Finite(r.Number), Text = CutOrNull(r.Text, 255) });
        }
        var latest = rows.GroupBy(r => r.EntityId).Select(g => g.OrderBy(r => r.TsUtc).Last()).ToList();
        var entities = await _db.GenericEntities.Where(e => entityIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        foreach (var l in latest)
        {
            if (!entities.TryGetValue(l.EntityId, out var e)) continue;
            var ts = l.TsUtc.ToUnixTimeSeconds();
            if (e.LastTs is { } last && last > ts) continue;
            e.LastNumber = Finite(l.Number);
            e.LastText = CutOrNull(l.Text, 255);
            e.LastTs = ts;
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<GenericReading>> ListReadingsAsync(long entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.GenericReadings.AsNoTracking().Where(r => r.EntityId == entityId && r.Ts >= from && r.Ts < to).OrderBy(r => r.Ts).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<GenericReading?> LastReadingBeforeAsync(long entityId, DateTimeOffset tsUtc, CancellationToken ct)
    {
        var ts = tsUtc.ToUnixTimeSeconds();
        var row = await _db.GenericReadings.AsNoTracking().Where(r => r.EntityId == entityId && r.Ts < ts).OrderByDescending(r => r.Ts).FirstOrDefaultAsync(ct);
        return row is null ? null : ToModel(row);
    }

    public async Task UpsertHourlyAsync(long entityId, IReadOnlyList<GenericHour> hours, CancellationToken ct)
    {
        if (hours.Count == 0) return;
        var keys = hours.Select(h => h.HourUtc.ToUnixTimeSeconds()).ToList();
        var existing = await _db.GenericHourly.Where(h => h.EntityId == entityId && keys.Contains(h.HourTs)).ToDictionaryAsync(h => h.HourTs, ct);
        foreach (var h in hours)
        {
            var ts = h.HourUtc.ToUnixTimeSeconds();
            if (!existing.TryGetValue(ts, out var e))
            {
                e = new GenericHourlyEntity { EntityId = entityId, HourTs = ts };
                _db.GenericHourly.Add(e);
                existing[ts] = e;
            }
            e.Min = Finite(h.Min);
            e.Avg = Finite(h.Avg);
            e.Max = Finite(h.Max);
            e.Last = Finite(h.Last);
            e.Count = h.Count;
            e.Delta = Finite(h.Delta);
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<(long EntityId, GenericHour Hour)>> ListHourlyAsync(IReadOnlyCollection<long> entityIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (entityIds.Count == 0) return Array.Empty<(long, GenericHour)>();
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var ids = entityIds.Distinct().ToList();
        var rows = await _db.GenericHourly.AsNoTracking().Where(h => ids.Contains(h.EntityId) && h.HourTs >= from && h.HourTs < to)
            .OrderBy(h => h.EntityId).ThenBy(h => h.HourTs).ToListAsync(ct);
        return rows.Select(h => (h.EntityId, new GenericHour(DateTimeOffset.FromUnixTimeSeconds(h.HourTs), h.Min, h.Avg, h.Max, h.Last, h.Count, h.Delta))).ToList();
    }

    public async Task<IReadOnlyDictionary<long, DateTimeOffset>> LastHourlyAsync(CancellationToken ct)
    {
        var rows = await _db.GenericHourly.AsNoTracking().GroupBy(h => h.EntityId).Select(g => new { g.Key, Ts = g.Max(h => h.HourTs) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => DateTimeOffset.FromUnixTimeSeconds(r.Ts));
    }

    public async Task<int> DeleteReadingsBeforeAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken ct)
    {
        var cutoff = cutoffUtc.ToUnixTimeSeconds();
        var entityIds = await _db.GenericEntities.AsNoTracking().Select(e => e.Id).ToListAsync(ct);
        var total = 0;
        foreach (var id in entityIds)
        {
            // Range on the primary key (EntityId, Ts), in batches: no index on Ts is needed.
            while (true)
            {
                var batch = await _db.GenericReadings.Where(r => r.EntityId == id && r.Ts < cutoff).OrderBy(r => r.Ts).Take(batchSize)
                    .Select(r => r.Ts).ToListAsync(ct);
                if (batch.Count == 0) break;
                var last = batch[^1];
                total += await _db.GenericReadings.Where(r => r.EntityId == id && r.Ts <= last).ExecuteDeleteAsync(ct);
                if (batch.Count < batchSize) break;
            }
        }
        return total;
    }

    private static T Parse<T>(string? s, T fallback) where T : struct, Enum => Enum.TryParse<T>(s, out var v) ? v : fallback;

    private static GenericDevice ToModel(GenericDeviceEntity e) => new(e.Id, e.Integration, e.ExternalId, e.Name, e.NameIsCustom, e.Room,
        e.Manufacturer, e.Model, e.Firmware, e.ViaDevice, Parse(e.AutoCategory, DeviceCategory.Generic),
        Enum.TryParse<DeviceCategory>(e.CategoryOverride, out var c) ? c : null, e.IsArchived,
        DateTimeOffset.FromUnixTimeSeconds(e.FirstSeenUtc), e.LastSeenUtc is { } s ? DateTimeOffset.FromUnixTimeSeconds(s) : null);

    private static GenericEntityRow ToModel(GenericEntityEntity e) => new(e.Id, e.DeviceId, e.ExternalId, e.Component,
        new DeviceEntity("", e.Name, Parse(e.Kind, EntityKind.Unsupported), Parse(e.DataType, EntityDataType.Text), Parse(e.Class, DeviceClass.None),
            e.ClassRaw, e.Unit, Enum.TryParse<StateClass>(e.StateClass, out var sc) ? sc : null, e.Precision, e.Writable,
            Parse(e.Category, EntityCategory.Primary), null, e.Problem, e.StateClassInferred),
        e.DiscoveryJson, e.IsRemoved, e.LastNumber, e.LastText, e.LastTs is { } t ? DateTimeOffset.FromUnixTimeSeconds(t) : null);

    private static GenericReading ToModel(GenericReadingEntity r) => new(r.EntityId, DateTimeOffset.FromUnixTimeSeconds(r.Ts), r.Value, r.Text);
}
