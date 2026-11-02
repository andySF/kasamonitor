using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class LiveRepository : ILiveRepository
{
    private readonly KasaDbContext _db;
    public LiveRepository(KasaDbContext db) => _db = db;

    public async Task SaveBatchAsync(IReadOnlyList<LiveSample> samples, IReadOnlyList<LiveMinuteAggregate> minutes, CancellationToken ct)
    {
        if (samples.Count == 0 && minutes.Count == 0) return;
        foreach (var s in samples)
            _db.LiveSamples.Add(new LiveSampleEntity
            {
                PlugId = s.PlugId,
                Ts = s.TsUtc.ToUnixTimeSeconds(),
                PowerW = s.PowerW,
                ApparentVa = s.ApparentVa,
                VoltageV = s.VoltageV,
                CurrentA = s.CurrentA,
                Pf = s.Pf,
            });
        foreach (var m in minutes)
        {
            var ts = m.MinuteUtc.ToUnixTimeSeconds();
            var existing = await _db.LiveMinuteAggregates.FindAsync(new object?[] { m.PlugId, ts }, ct);
            var merged = m;
            if (existing is null)
            {
                existing = new LiveMinuteAggregateEntity { PlugId = m.PlugId, MinuteTs = ts };
                _db.LiveMinuteAggregates.Add(existing);
            }
            else
            {
                // A second part of the same minute (restart mid-minute, late sample): combine, never overwrite.
                merged = new LiveMinuteAggregate(m.PlugId, m.MinuteUtc, existing.MinW, existing.AvgW, existing.MaxW,
                    existing.AvgVoltageV, existing.EnergyWh, existing.SampleCount, existing.CoveredSeconds).Merge(m);
            }
            existing.MinW = merged.MinW;
            existing.AvgW = merged.AvgW;
            existing.MaxW = merged.MaxW;
            existing.AvgVoltageV = merged.AvgVoltageV;
            existing.EnergyWh = merged.EnergyWh;
            existing.SampleCount = merged.SampleCount;
            existing.CoveredSeconds = merged.CoveredSeconds;
        }
        // One SaveChanges = one transaction for the whole batch.
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<IReadOnlyList<LiveSample>> ListSamplesAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        return await _db.LiveSamples.AsNoTracking()
            .Where(s => s.PlugId == plugId && s.Ts >= from && s.Ts <= to)
            .OrderBy(s => s.Ts)
            .Select(s => new LiveSample(s.PlugId, DateTimeOffset.FromUnixTimeSeconds(s.Ts), s.PowerW, s.ApparentVa, s.VoltageV, s.CurrentA, s.Pf, null))
            .ToListAsync(ct);
    }

    public async Task<DateTimeOffset?> FirstSampleTsAsync(Guid plugId, CancellationToken ct)
    {
        var first = await _db.LiveSamples.AsNoTracking().Where(s => s.PlugId == plugId).OrderBy(s => s.Ts).Select(s => (long?)s.Ts).FirstOrDefaultAsync(ct);
        return first is { } ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null;
    }

    public async Task<IReadOnlyList<PlugHourWh>> SumEnergyByHourAsync(IReadOnlyCollection<Guid> plugIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (plugIds.Count == 0) return Array.Empty<PlugHourWh>();
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var ids = plugIds.ToList();
        var rows = await _db.LiveMinuteAggregates.AsNoTracking()
            .Where(m => ids.Contains(m.PlugId) && m.MinuteTs >= from && m.MinuteTs < to)
            .GroupBy(m => new { m.PlugId, Hour = m.MinuteTs / 3600 })
            .Select(g => new { g.Key.PlugId, g.Key.Hour, Wh = g.Sum(m => m.EnergyWh), Minutes = g.Count() })
            .ToListAsync(ct);
        return rows.Select(r => new PlugHourWh(r.PlugId, DateTimeOffset.FromUnixTimeSeconds(r.Hour * 3600), r.Wh, r.Minutes)).ToList();
    }

    public async Task<IReadOnlyList<LiveMinuteAggregate>> ListMinutesAsync(IReadOnlyCollection<Guid> plugIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (plugIds.Count == 0) return Array.Empty<LiveMinuteAggregate>();
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var ids = plugIds.ToList();
        return await _db.LiveMinuteAggregates.AsNoTracking()
            .Where(m => ids.Contains(m.PlugId) && m.MinuteTs >= from && m.MinuteTs <= to)
            .OrderBy(m => m.PlugId).ThenBy(m => m.MinuteTs)
            .Select(m => new LiveMinuteAggregate(m.PlugId, DateTimeOffset.FromUnixTimeSeconds(m.MinuteTs), m.MinW, m.AvgW, m.MaxW,
                m.AvgVoltageV, m.EnergyWh, m.SampleCount, m.CoveredSeconds))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LiveMinuteAggregate>> ListMinutesAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        return await _db.LiveMinuteAggregates.AsNoTracking()
            .Where(m => m.PlugId == plugId && m.MinuteTs >= from && m.MinuteTs <= to)
            .OrderBy(m => m.MinuteTs)
            .Select(m => new LiveMinuteAggregate(m.PlugId, DateTimeOffset.FromUnixTimeSeconds(m.MinuteTs), m.MinW, m.AvgW, m.MaxW,
                m.AvgVoltageV, m.EnergyWh, m.SampleCount, m.CoveredSeconds))
            .ToListAsync(ct);
    }

    public async Task<int> CountSamplesOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var cutoff = cutoffUtc.ToUnixTimeSeconds();
        return await _db.LiveSamples.AsNoTracking().CountAsync(s => s.Ts < cutoff, ct);
    }

    public async Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct, int batchSize = 5000)
    {
        var cutoff = cutoffUtc.ToUnixTimeSeconds();
        var plugIds = await _db.LiveSamples.AsNoTracking().Select(s => s.PlugId).Distinct().ToListAsync(ct);
        var total = 0;
        foreach (var plugId in plugIds)
        {
            while (true)
            {
                // Per plug, so the (PlugId, Ts) index serves the range; each batch is its own short transaction.
                var deleted = await _db.LiveSamples
                    .Where(s => s.PlugId == plugId && s.Ts < cutoff)
                    .OrderBy(s => s.Ts)
                    .Take(batchSize)
                    .ExecuteDeleteAsync(ct);
                total += deleted;
                if (deleted < batchSize) break;
                await Task.Yield();
            }
        }
        return total;
    }

    public async Task<RelayCommand> AddRelayCommandAsync(RelayCommand command, CancellationToken ct)
    {
        var e = new RelayCommandEntity
        {
            Ts = command.TsUtc.ToUnixTimeMilliseconds(),
            PlugId = command.PlugId,
            On = command.On,
            Source = command.Source,
            Result = command.Result,
        };
        _db.RelayCommands.Add(e);
        await _db.SaveChangesAsync(ct);
        return command with { Id = e.Id };
    }

    public async Task<IReadOnlyList<RelayCommand>> ListRelayCommandsAsync(Guid? plugId, int take, CancellationToken ct)
    {
        var q = _db.RelayCommands.AsNoTracking();
        if (plugId is { } id) q = q.Where(r => r.PlugId == id);
        return await q.OrderByDescending(r => r.Ts).ThenByDescending(r => r.Id).Take(take)
            .Select(r => new RelayCommand(r.Id, DateTimeOffset.FromUnixTimeMilliseconds(r.Ts), r.PlugId, r.On, r.Source, r.Result))
            .ToListAsync(ct);
    }
}
