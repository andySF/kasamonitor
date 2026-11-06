using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class PowerAggregateRepository : IPowerAggregateRepository
{
    private readonly KasaDbContext _db;
    public PowerAggregateRepository(KasaDbContext db) => _db = db;

    public async Task<IReadOnlyList<PowerAggregate>> ListMinutesAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var rows = await _db.OneMinuteAggregates.AsNoTracking()
            .Where(a => a.PlugId == plugId && a.BucketStartUtc >= fromUtc && a.BucketStartUtc < toUtc)
            .OrderBy(a => a.BucketStartUtc)
            .Select(a => new { a.BucketStartUtc, a.PAvgMw, a.PMaxMw })
            .ToListAsync(ct);
        return rows.Select(r => new PowerAggregate(r.BucketStartUtc, r.PAvgMw, r.PMaxMw, 1)).ToList();
    }

    public async Task<IReadOnlyList<PowerAggregate>> ListHoursAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var rows = await _db.OneHourAggregates.AsNoTracking()
            .Where(a => a.PlugId == plugId && a.BucketStartUtc >= fromUtc && a.BucketStartUtc < toUtc)
            .OrderBy(a => a.BucketStartUtc)
            .Select(a => new { a.BucketStartUtc, a.PAvgMw, a.PMaxMw, a.SampleCount })
            .ToListAsync(ct);
        return rows.Select(r => new PowerAggregate(r.BucketStartUtc, r.PAvgMw, r.PMaxMw, r.SampleCount)).ToList();
    }

    public async Task<int> UpsertHoursAsync(Guid plugId, IReadOnlyList<PowerHour> hours, CancellationToken ct)
    {
        if (hours.Count == 0) return 0;
        var from = hours.Min(h => h.StartUtc);
        var to = hours.Max(h => h.StartUtc);
        _db.ChangeTracker.Clear();
        var stored = (await _db.OneHourAggregates
                .Where(a => a.PlugId == plugId && a.BucketStartUtc >= from && a.BucketStartUtc <= to)
                .ToListAsync(ct))
            .ToDictionary(a => a.BucketStartUtc.UtcTicks);
        var written = 0;
        foreach (var h in hours)
        {
            if (!stored.TryGetValue(h.StartUtc.UtcTicks, out var row))
            {
                _db.OneHourAggregates.Add(new OneHourAggregateEntity
                {
                    PlugId = plugId, BucketStartUtc = h.StartUtc, PAvgMw = h.AvgMw, PMaxMw = h.MaxMw, EnergyWh = h.EnergyWh, SampleCount = h.Minutes,
                });
                written++;
            }
            else if (PowerHistory.Replaces(row.SampleCount, h.Minutes))
            {
                if (row.PAvgMw == h.AvgMw && row.PMaxMw == h.MaxMw && row.EnergyWh == h.EnergyWh && row.SampleCount == h.Minutes) continue;
                (row.PAvgMw, row.PMaxMw, row.EnergyWh, row.SampleCount) = (h.AvgMw, h.MaxMw, h.EnergyWh, h.Minutes);
                written++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return written;
    }

    public async Task<(DateTimeOffset First, DateTimeOffset Last)?> MinuteSpanAsync(Guid plugId, CancellationToken ct)
    {
        var mine = _db.OneMinuteAggregates.AsNoTracking().Where(a => a.PlugId == plugId);
        var first = await mine.OrderBy(a => a.BucketStartUtc).Select(a => (DateTimeOffset?)a.BucketStartUtc).FirstOrDefaultAsync(ct);
        if (first is null) return null;
        var last = await mine.OrderByDescending(a => a.BucketStartUtc).Select(a => a.BucketStartUtc).FirstAsync(ct);
        return (first.Value, last);
    }

    public async Task<IReadOnlySet<DateOnly>> HourDaysAsync(Guid plugId, CancellationToken ct)
    {
        var starts = await _db.OneHourAggregates.AsNoTracking().Where(a => a.PlugId == plugId).Select(a => a.BucketStartUtc).ToListAsync(ct);
        return starts.Select(s => DateOnly.FromDateTime(s.UtcDateTime)).ToHashSet();
    }
}
