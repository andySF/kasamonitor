using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class SampleRepository : ISampleRepository
{
    private readonly KasaDbContext _db;
    public SampleRepository(KasaDbContext db) => _db = db;

    public async Task InsertAsync(Sample sample, CancellationToken ct)
    {
        _db.Samples.Add(MapToEntity(sample));
        await _db.SaveChangesAsync(ct);
    }

    public async Task InsertManyAsync(IReadOnlyList<Sample> samples, CancellationToken ct)
    {
        foreach (var s in samples) _db.Samples.Add(MapToEntity(s));
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Sample>> ListByPlugAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var entities = await _db.Samples
            .AsNoTracking()
            .Where(s => s.PlugId == plugId)
            .ToListAsync(ct);

        return entities
            .Where(s => s.TimestampUtc >= fromUtc && s.TimestampUtc <= toUtc)
            .OrderBy(s => s.TimestampUtc)
            .Select(Map)
            .ToList();
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var allSamples = await _db.Samples.ToListAsync(ct);
        var toDelete = allSamples
            .Where(s => s.TimestampUtc < cutoffUtc)
            .ToList();

        var count = toDelete.Count;
        if (count == 0) return 0;

        _db.Samples.RemoveRange(toDelete);
        await _db.SaveChangesAsync(ct);
        return count;
    }

    private static SampleEntity MapToEntity(Sample s) => new()
    {
        PlugId = s.PlugId,
        TimestampUtc = s.TimestampUtc,
        Online = s.Online,
        PowerMw = s.PowerMw,
        VoltageMv = s.VoltageMv,
        CurrentMa = s.CurrentMa,
        TotalWh = s.TotalWh,
        Rssi = s.Rssi,
        RelayOn = s.RelayOn,
        ErrorMessage = s.ErrorMessage
    };

    private static Sample Map(SampleEntity e) => new(
        Id: e.Id, PlugId: e.PlugId, TimestampUtc: e.TimestampUtc, Online: e.Online,
        PowerMw: e.PowerMw, VoltageMv: e.VoltageMv, CurrentMa: e.CurrentMa, TotalWh: e.TotalWh,
        Rssi: e.Rssi, RelayOn: e.RelayOn, ErrorMessage: e.ErrorMessage);
}
