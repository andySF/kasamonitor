using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class EntityPrefsRepository : IEntityPrefsRepository
{
    private readonly KasaDbContext _db;
    public EntityPrefsRepository(KasaDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, EntityPrefs>> ListAsync(string keyPrefix, CancellationToken ct)
    {
        // Ordinal prefix match on the primary key (a range, not LIKE: the keys contain ':' and '/', never wildcards).
        var upper = keyPrefix + '\uffff';
        var rows = await _db.EntityPrefs.AsNoTracking()
            .Where(p => string.Compare(p.Key, keyPrefix) >= 0 && string.Compare(p.Key, upper) < 0)
            .ToListAsync(ct);
        return rows.Where(r => r.Key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToDictionary(r => r.Key, ToModel, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyDictionary<string, EntityPrefs>> ListAllAsync(CancellationToken ct)
    {
        var rows = await _db.EntityPrefs.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, ToModel, StringComparer.Ordinal);
    }

    public async Task SaveAsync(EntityPrefs prefs, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prefs.EntityKey) || prefs.EntityKey.Length > 450) throw new ArgumentException("Cheia entității lipsește sau e prea lungă.", nameof(prefs));
        var e = await _db.EntityPrefs.FindAsync(new object?[] { prefs.EntityKey }, ct);
        if (prefs.IsEmpty)
        {
            if (e is not null) _db.EntityPrefs.Remove(e);
        }
        else
        {
            if (e is null) _db.EntityPrefs.Add(e = new EntityPrefEntity { Key = prefs.EntityKey });
            e.Visible = prefs.Visible;
            e.InHistory = prefs.InHistory;
            e.InMetrics = prefs.InMetrics;
            e.NameOverride = string.IsNullOrWhiteSpace(prefs.NameOverride) ? null : GenericRepository.Cut(prefs.NameOverride.Trim(), 200);
            e.PrecisionOverride = prefs.PrecisionOverride is { } p ? Math.Clamp(p, 0, 6) : null;
            e.ClassOverride = prefs.ClassOverride?.ToString();
            e.UnitOverride = string.IsNullOrWhiteSpace(prefs.UnitOverride) ? null : GenericRepository.Cut(prefs.UnitOverride.Trim(), 32);
            e.CommandsEnabled = prefs.CommandsEnabled;
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private static EntityPrefs ToModel(EntityPrefEntity e) => new(e.Key, e.Visible, e.InHistory, e.InMetrics, e.NameOverride, e.PrecisionOverride,
        Enum.TryParse<DeviceClass>(e.ClassOverride, out var c) ? c : null, e.UnitOverride, e.CommandsEnabled);
}
