using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class HistoryRepository : IHistoryRepository
{
    private readonly KasaDbContext _db;
    public HistoryRepository(KasaDbContext db) => _db = db;

    public async Task UpsertDailyAsync(Guid plugId, IReadOnlyList<DailyKwh> entries, CancellationToken ct)
    {
        foreach (var d in entries)
        {
            var key = $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";
            var existing = await _db.DailyEnergies.FindAsync(new object?[] { plugId, key }, ct);
            if (existing is null)
            {
                _db.DailyEnergies.Add(new DailyEnergyEntity { PlugId = plugId, Date = key, EnergyWh = d.EnergyWh });
            }
            else
            {
                existing.EnergyWh = d.EnergyWh;
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertMonthlyAsync(Guid plugId, IReadOnlyList<MonthlyKwh> entries, CancellationToken ct)
    {
        foreach (var m in entries)
        {
            var existing = await _db.MonthlyEnergies.FindAsync(new object?[] { plugId, m.Year, m.Month }, ct);
            if (existing is null)
            {
                _db.MonthlyEnergies.Add(new MonthlyEnergyEntity { PlugId = plugId, Year = m.Year, Month = m.Month, EnergyWh = m.EnergyWh });
            }
            else
            {
                existing.EnergyWh = m.EnergyWh;
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DailyKwh>> GetDailyRangeAsync(Guid plugId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct)
    {
        var fromKey = fromInclusive.ToString("yyyy-MM-dd");
        var toKey   = toInclusive.ToString("yyyy-MM-dd");
        var rows = await _db.DailyEnergies.AsNoTracking()
            .Where(d => d.PlugId == plugId && string.Compare(d.Date, fromKey) >= 0 && string.Compare(d.Date, toKey) <= 0)
            .OrderBy(d => d.Date)
            .ToListAsync(ct);
        return rows.Select(d =>
        {
            var dt = DateOnly.ParseExact(d.Date, "yyyy-MM-dd");
            return new DailyKwh(dt.Year, dt.Month, dt.Day, d.EnergyWh);
        }).ToList();
    }

    public async Task<IReadOnlyList<MonthlyKwh>> GetMonthlyRangeAsync(Guid plugId, int yearFromInclusive, int yearToInclusive, CancellationToken ct) =>
        await _db.MonthlyEnergies.AsNoTracking()
            .Where(m => m.PlugId == plugId && m.Year >= yearFromInclusive && m.Year <= yearToInclusive)
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .Select(m => new MonthlyKwh(m.Year, m.Month, m.EnergyWh))
            .ToListAsync(ct);
}
