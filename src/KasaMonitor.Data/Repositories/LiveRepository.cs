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
        // SQL Server refuses NaN and ±Infinity (SQLite stored them) and one such value would fail the whole batch, again at
        // every flush: a nullable column gets NULL, a sample without a finite power and a minute without finite values are dropped.
        var rows = samples.Where(s => double.IsFinite(s.PowerW))
            .DistinctBy(s => (s.PlugId, s.TsUtc.ToUnixTimeSeconds()))   // one row per plug and second: the unique key (PlugId, Ts)
            .ToList();
        var mins = minutes.Where(m => double.IsFinite(m.MinW) && double.IsFinite(m.AvgW) && double.IsFinite(m.MaxW)
                                      && double.IsFinite(m.EnergyWh) && double.IsFinite(m.CoveredSeconds)).ToList();
        if (rows.Count == 0 && mins.Count == 0) return;

        // One transaction for the whole batch, run by the execution strategy. If the commit fails without an answer (it
        // may have been applied), EF asks verifySucceeded before retrying: the last new sample, or the first minute with
        // its merged values, tells whether the batch is already stored — so a retry never stores it twice. Samples already
        // stored (a batch put back in the queue) are skipped by the key anyway.
        LiveSampleEntity? lastSample = null;
        LiveMinuteAggregateEntity? firstMinute = null;
        await _db.Database.CreateExecutionStrategy().ExecuteInTransactionAsync(async c =>
        {
            _db.ChangeTracker.Clear();
            lastSample = null;
            firstMinute = null;
            var stored = await StoredKeysAsync(rows, c);
            foreach (var s in rows)
            {
                var ts = s.TsUtc.ToUnixTimeSeconds();
                if (stored.Contains((s.PlugId, ts))) continue;
                lastSample = new LiveSampleEntity
                {
                    PlugId = s.PlugId,
                    Ts = ts,
                    PowerW = s.PowerW,
                    ApparentVa = DbValues.Finite(s.ApparentVa),
                    VoltageV = DbValues.Finite(s.VoltageV),
                    CurrentA = DbValues.Finite(s.CurrentA),
                    Pf = DbValues.Finite(s.Pf),
                };
                _db.LiveSamples.Add(lastSample);
            }
            foreach (var m in mins)
            {
                var ts = m.MinuteUtc.ToUnixTimeSeconds();
                var existing = await _db.LiveMinuteAggregates.FindAsync(new object?[] { m.PlugId, ts }, c);
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
                existing.AvgVoltageV = DbValues.Finite(merged.AvgVoltageV);
                existing.EnergyWh = merged.EnergyWh;
                existing.SampleCount = merged.SampleCount;
                existing.CoveredSeconds = merged.CoveredSeconds;
                firstMinute ??= existing;
            }
            await _db.SaveChangesAsync(acceptAllChangesOnSuccess: false, c);
        }, async c =>
        {
            if (lastSample is { } w)
                return await _db.LiveSamples.AsNoTracking().AnyAsync(s => s.PlugId == w.PlugId && s.Ts == w.Ts, c);
            if (firstMinute is { } m)
            {
                long minuteTs = m.MinuteTs;
                double energy = m.EnergyWh;
                int count = m.SampleCount;
                return await _db.LiveMinuteAggregates.AsNoTracking()
                    .AnyAsync(x => x.PlugId == m.PlugId && x.MinuteTs == minuteTs && x.SampleCount == count && x.EnergyWh == energy, c);
            }
            return true;
        }, ct);
        _db.ChangeTracker.Clear();
    }

    /// <summary>(PlugId, Ts) of the stored samples in the batch's time range, for the plugs of the batch.</summary>
    private async Task<HashSet<(Guid, long)>> StoredKeysAsync(IReadOnlyList<LiveSample> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var plugIds = rows.Select(s => s.PlugId).Distinct().ToList();
        long from = rows.Min(s => s.TsUtc.ToUnixTimeSeconds()), to = rows.Max(s => s.TsUtc.ToUnixTimeSeconds());
        var keys = await _db.LiveSamples.AsNoTracking()
            .Where(s => plugIds.Contains(s.PlugId) && s.Ts >= from && s.Ts <= to)
            .Select(s => new { s.PlugId, s.Ts })
            .ToListAsync(ct);
        return keys.Select(k => (k.PlugId, k.Ts)).ToHashSet();
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

    public async Task<IReadOnlyList<RelayCommand>> ListRelayCommandsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int take, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeMilliseconds(), to = toUtc.ToUnixTimeMilliseconds();
        return await _db.RelayCommands.AsNoTracking()
            .Where(r => r.Ts >= from && r.Ts < to)
            .OrderByDescending(r => r.Ts).ThenByDescending(r => r.Id).Take(take)
            .Select(r => new RelayCommand(r.Id, DateTimeOffset.FromUnixTimeMilliseconds(r.Ts), r.PlugId, r.On, r.Source, r.Result))
            .ToListAsync(ct);
    }
}
