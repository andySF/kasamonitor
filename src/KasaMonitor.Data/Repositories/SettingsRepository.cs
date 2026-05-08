using KasaMonitor.Core.Abstractions;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly KasaDbContext _db;
    public SettingsRepository(KasaDbContext db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var e = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return e?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var e = await _db.Settings.FindAsync(new object?[] { key }, ct);
        if (e is null) _db.Settings.Add(new SettingEntity { Key = key, Value = value });
        else e.Value = value;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct)
    {
        var rows = await _db.Settings.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(s => s.Key, s => s.Value);
    }
}
