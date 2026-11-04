using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class NilmRepository : INilmRepository
{
    /// <summary>Two events of the same meter closer than this (and alike) are the same event.</summary>
    public const int DuplicateSeconds = 2;

    private readonly KasaDbContext _db;
    public NilmRepository(KasaDbContext db) => _db = db;

    private static DateTimeOffset FromUnix(long s) => DateTimeOffset.FromUnixTimeSeconds(s);

    private static MeterEvent ToModel(MeterEventEntity e) => new(e.Id, e.PlugId, FromUnix(e.Ts), e.DeltaP, e.DeltaQ, e.DeltaPf,
        e.TransientRatio, e.RiseSeconds, e.PowerBeforeW, e.SignatureId, e.Label, NilmSources.EventFromInt(e.Source), e.LabelPlugId);

    private static ApplianceSignature ToModel(ApplianceSignatureEntity s) => new(s.Id, s.Name, s.Icon, s.Category, s.MeanDeltaP,
        s.MeanDeltaQ, s.MeanTransient, s.Count, s.LastSeen is { } ls ? FromUnix(ls) : null, NilmSources.SignatureFromInt(s.LabelSource),
        s.PlugId, s.Hidden, s.MinRunSeconds, s.MaxRunSeconds, s.SplitFromId);

    private static ApplianceRun ToModel(ApplianceRunEntity r) => new(r.Id, r.SignatureId, FromUnix(r.StartTs),
        r.EndTs is { } end ? FromUnix(end) : null, r.PowerW, r.EstimatedWh, r.StartEventId, r.EndEventId, r.HouseBeforeW,
        Enum.IsDefined(typeof(RunEndReason), r.EndReason) ? (RunEndReason)r.EndReason : RunEndReason.Paired);

    private static void Copy(MeterEvent m, MeterEventEntity e)
    {
        e.SignatureId = m.SignatureId;
        e.Label = m.Label;
        e.Source = (int)m.Source;
        e.LabelPlugId = m.LabelPlugId;
    }

    private static void Copy(ApplianceSignature m, ApplianceSignatureEntity s)
    {
        s.Name = m.Name;
        s.Icon = m.Icon;
        s.Category = m.Category;
        s.MeanDeltaP = m.MeanDeltaP;
        s.MeanDeltaQ = m.MeanDeltaQ;
        s.MeanTransient = m.MeanTransient;
        s.Count = m.Count;
        s.LastSeen = m.LastSeenUtc?.ToUnixTimeSeconds();
        s.LabelSource = (int)m.LabelSource;
        s.PlugId = m.PlugId;
        s.Hidden = m.Hidden;
        s.MinRunSeconds = m.MinRunSeconds;
        s.MaxRunSeconds = m.MaxRunSeconds;
        s.SplitFromId = m.SplitFromId;
    }

    private static void Copy(ApplianceRun m, ApplianceRunEntity r)
    {
        r.SignatureId = m.SignatureId;
        r.StartTs = m.StartUtc.ToUnixTimeSeconds();
        r.EndTs = m.EndUtc?.ToUnixTimeSeconds();
        r.PowerW = m.PowerW;
        r.EstimatedWh = m.EstimatedWh;
        r.StartEventId = m.StartEventId;
        r.EndEventId = m.EndEventId;
        r.HouseBeforeW = m.HouseBeforeW;
        r.EndReason = (int)m.EndReason;
    }

    public async Task<IReadOnlyList<MeterEvent>> AddEventsAsync(IReadOnlyList<MeterEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return Array.Empty<MeterEvent>();
        var plugs = events.Select(e => e.PlugId).Distinct().ToList();
        long from = events.Min(e => e.TsUtc).ToUnixTimeSeconds() - DuplicateSeconds, to = events.Max(e => e.TsUtc).ToUnixTimeSeconds() + DuplicateSeconds;
        var near = await _db.MeterEvents.AsNoTracking().Where(x => plugs.Contains(x.PlugId) && x.Ts >= from && x.Ts <= to)
            .Select(x => new { x.PlugId, x.Ts, x.DeltaP }).ToListAsync(ct);
        var known = near.Select(x => (x.PlugId, x.Ts, x.DeltaP)).ToList();
        bool Duplicate(Guid plug, long ts, double p) => known.Any(k => k.PlugId == plug && Math.Abs(k.Ts - ts) <= DuplicateSeconds
            && Math.Sign(k.DeltaP) == Math.Sign(p) && Math.Abs(Math.Abs(k.DeltaP) - Math.Abs(p)) <= SignatureClusterer.Eps(k.DeltaP, p));

        var added = new List<MeterEventEntity>();
        foreach (var e in events)
        {
            var ts = e.TsUtc.ToUnixTimeSeconds();
            if (Duplicate(e.PlugId, ts, e.DeltaP) || known.Any(k => k.PlugId == e.PlugId && k.Ts == ts)) continue;
            var entity = new MeterEventEntity
            {
                PlugId = e.PlugId,
                Ts = ts,
                DeltaP = e.DeltaP,
                DeltaQ = e.DeltaQ,
                DeltaPf = e.DeltaPf,
                TransientRatio = e.TransientRatio,
                RiseSeconds = e.RiseSeconds,
                PowerBeforeW = e.PowerBeforeW,
            };
            Copy(e, entity);
            _db.MeterEvents.Add(entity);
            added.Add(entity);
            known.Add((e.PlugId, ts, e.DeltaP));
        }
        if (added.Count == 0) return Array.Empty<MeterEvent>();
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return added.Select(ToModel).ToList();
    }

    public async Task UpdateEventAsync(MeterEvent e, CancellationToken ct)
    {
        var entity = await _db.MeterEvents.FindAsync(new object?[] { e.Id }, ct);
        if (entity is null) return;
        Copy(e, entity);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task LabelEventsAsync(IReadOnlyCollection<long> eventIds, string label, CancellationToken ct)
    {
        if (eventIds.Count == 0) return;
        var user = (int)EventLabelSource.User;
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            foreach (var chunk in eventIds.Chunk(500))
                await _db.MeterEvents.Where(e => chunk.Contains(e.Id)).ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.Source, user)
                    .SetProperty(e => e.Label, label)
                    .SetProperty(e => e.LabelPlugId, (Guid?)null), ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task SetSignaturesAsync(IReadOnlyDictionary<long, long?> signatureByEvent, CancellationToken ct)
    {
        if (signatureByEvent.Count == 0) return;
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            foreach (var group in signatureByEvent.GroupBy(kv => kv.Value))
            {
                var sig = group.Key;
                foreach (var chunk in group.Select(kv => kv.Key).Chunk(500))
                    await _db.MeterEvents.Where(e => chunk.Contains(e.Id)).ExecuteUpdateAsync(u => u.SetProperty(e => e.SignatureId, sig), ct);
            }
            await tx.CommitAsync(ct);
        });
    }

    public async Task<MeterEvent?> GetEventAsync(long id, CancellationToken ct)
    {
        var e = await _db.MeterEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return e is null ? null : ToModel(e);
    }

    public async Task<IReadOnlyDictionary<long, long?>> GetSignatureIdsAsync(IReadOnlyCollection<long> eventIds, CancellationToken ct)
    {
        var result = new Dictionary<long, long?>();
        foreach (var chunk in eventIds.Chunk(2000))
            foreach (var row in await _db.MeterEvents.AsNoTracking().Where(e => chunk.Contains(e.Id)).Select(e => new { e.Id, e.SignatureId }).ToListAsync(ct))
                result[row.Id] = row.SignatureId;
        return result;
    }

    public async Task<IReadOnlyList<MeterEvent>> ListEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.MeterEvents.AsNoTracking().Where(e => e.Ts >= from && e.Ts <= to).OrderBy(e => e.Ts).ThenBy(e => e.Id).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<MeterEvent>> ListNewestEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int max, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.MeterEvents.AsNoTracking().Where(e => e.Ts >= from && e.Ts <= to)
            .OrderByDescending(e => e.Ts).ThenByDescending(e => e.Id).Take(max).ToListAsync(ct);
        rows.Reverse();
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<MeterEvent>> ListEventsPageAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int skip, int take, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.MeterEvents.AsNoTracking().Where(e => e.Ts >= from && e.Ts <= to)
            .OrderByDescending(e => e.Ts).ThenByDescending(e => e.Id).Skip(skip).Take(take).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<MeterEvent>> ListEventsBySignaturesAsync(IReadOnlyCollection<long> signatureIds, DateTimeOffset fromUtc, CancellationToken ct)
    {
        var ids = signatureIds.ToList();
        var from = fromUtc.ToUnixTimeSeconds();
        var rows = await _db.MeterEvents.AsNoTracking()
            .Where(e => e.SignatureId != null && ids.Contains(e.SignatureId.Value) && e.Ts >= from)
            .OrderBy(e => e.Ts).ThenBy(e => e.Id).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    private IQueryable<MeterEventEntity> Labeled(IReadOnlyCollection<Guid> excludedPlugs)
    {
        int plug = (int)EventLabelSource.AutoPlug, user = (int)EventLabelSource.User;
        var excluded = excludedPlugs.ToList();
        return _db.MeterEvents.AsNoTracking().Where(e => e.Label != null && (e.Source == user
            || (e.Source == plug && (e.LabelPlugId == null || !excluded.Contains(e.LabelPlugId.Value)))));
    }

    public async Task<IReadOnlyList<MeterEvent>> ListLabeledEventsAsync(IReadOnlyCollection<Guid> excludedPlugs, int max, CancellationToken ct)
    {
        var rows = await Labeled(excludedPlugs).OrderByDescending(e => e.Ts).Take(max).ToListAsync(ct);
        rows.Reverse();
        return rows.Select(ToModel).ToList();
    }

    public async Task<(int Events, int Classes)> CountLabeledAsync(IReadOnlyCollection<Guid> excludedPlugs, CancellationToken ct)
    {
        var q = Labeled(excludedPlugs);
        return (await q.CountAsync(ct), await q.Select(e => e.Label).Distinct().CountAsync(ct));
    }

    public Task<int> CountEventsAsync(CancellationToken ct) => _db.MeterEvents.AsNoTracking().CountAsync(ct);

    public Task<int> CountEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        return _db.MeterEvents.AsNoTracking().CountAsync(e => e.Ts >= from && e.Ts <= to, ct);
    }

    public async Task<IReadOnlyList<ApplianceSignature>> ListSignaturesAsync(CancellationToken ct)
    {
        var rows = await _db.ApplianceSignatures.AsNoTracking().OrderBy(s => s.Id).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<ApplianceSignature?> GetSignatureAsync(long id, CancellationToken ct)
    {
        var s = await _db.ApplianceSignatures.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return s is null ? null : ToModel(s);
    }

    public async Task<ApplianceSignature> AddSignatureAsync(ApplianceSignature s, CancellationToken ct)
    {
        var entity = new ApplianceSignatureEntity();
        Copy(s, entity);
        _db.ApplianceSignatures.Add(entity);
        await _db.SaveChangesAsync(ct);
        if (string.IsNullOrWhiteSpace(entity.Name))
        {
            entity.Name = SignatureNaming.Unknown(entity.Id);
            await _db.SaveChangesAsync(ct);
        }
        _db.ChangeTracker.Clear();
        return ToModel(entity);
    }

    public async Task UpdateSignatureAsync(ApplianceSignature s, CancellationToken ct)
    {
        var entity = await _db.ApplianceSignatures.FindAsync(new object?[] { s.Id }, ct);
        if (entity is null) return;
        Copy(s, entity);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    public async Task DeleteSignatureAsync(long id, CancellationToken ct)
    {
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await _db.MeterEvents.Where(e => e.SignatureId == id).ExecuteUpdateAsync(u => u.SetProperty(e => e.SignatureId, (long?)null), ct);
            await _db.ApplianceRuns.Where(r => r.SignatureId == id).ExecuteDeleteAsync(ct);
            await _db.ApplianceSignatures.Where(s => s.SplitFromId == id).ExecuteUpdateAsync(u => u.SetProperty(s => s.SplitFromId, (long?)null), ct);
            await _db.ApplianceSignatures.Where(s => s.Id == id).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task<int> MoveSignatureAsync(long fromId, long intoId, CancellationToken ct)
    {
        if (fromId == intoId) return 0;
        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var moved = await _db.MeterEvents.Where(e => e.SignatureId == fromId).ExecuteUpdateAsync(u => u.SetProperty(e => e.SignatureId, (long?)intoId), ct);
            await _db.ApplianceRuns.Where(r => r.SignatureId == fromId).ExecuteUpdateAsync(u => u.SetProperty(r => r.SignatureId, intoId), ct);
            await _db.ApplianceSignatures.Where(s => s.SplitFromId == fromId).ExecuteUpdateAsync(u => u.SetProperty(s => s.SplitFromId, (long?)intoId), ct);
            await _db.ApplianceSignatures.Where(s => s.Id == fromId).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            return moved;
        });
    }

    public async Task MoveRunsAsync(IReadOnlyList<RunMove> moves, CancellationToken ct)
    {
        if (moves.Count == 0) return;
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await MoveRunsCoreAsync(moves, ct);
            await tx.CommitAsync(ct);
        });
    }

    private async Task MoveRunsCoreAsync(IReadOnlyList<RunMove> moves, CancellationToken ct)
    {
        var user = (int)EventLabelSource.User;
        var rule = (int)EventLabelSource.DurationRule;
        foreach (var group in moves.GroupBy(m => (m.TargetId, m.Label)))
        {
            var (target, label) = group.Key;
            foreach (var chunk in group.Chunk(500))
            {
                var events = chunk.SelectMany(m => new[] { m.StartEventId, m.EndEventId }).ToList();
                await _db.MeterEvents.Where(e => events.Contains(e.Id) && e.Source != user).ExecuteUpdateAsync(u => u
                    .SetProperty(e => e.SignatureId, (long?)target)
                    .SetProperty(e => e.Source, rule)
                    .SetProperty(e => e.Label, label), ct);
                var runs = chunk.Where(m => m.RunId != 0).Select(m => m.RunId).ToList();
                if (runs.Count > 0)
                    await _db.ApplianceRuns.Where(r => runs.Contains(r.Id)).ExecuteUpdateAsync(u => u.SetProperty(r => r.SignatureId, target), ct);
            }
        }
    }

    public async Task<ApplianceSignature> ApplyDurationSplitAsync(ApplianceSignature source, ApplianceSignature target, IReadOnlyList<ApplianceRun> runs, CancellationToken ct) =>
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var s = await _db.ApplianceSignatures.FindAsync(new object?[] { source.Id }, ct) ?? throw new InvalidOperationException($"Signature {source.Id} does not exist.");
            Copy(source, s);
            var t = target.Id == 0 ? null : await _db.ApplianceSignatures.FindAsync(new object?[] { target.Id }, ct);
            if (t is null)
            {
                t = new ApplianceSignatureEntity();
                _db.ApplianceSignatures.Add(t);
            }
            Copy(target, t);
            await _db.SaveChangesAsync(ct);
            await MoveRunsCoreAsync(runs.Select(r => new RunMove(r.Id, r.StartEventId, r.EndEventId!.Value, source.Id, t.Id, t.Name)).ToList(), ct);
            await tx.CommitAsync(ct);
            var stored = ToModel(t);
            _db.ChangeTracker.Clear();
            return stored;
        });

    public async Task<IReadOnlyList<ApplianceSignature>> RecomputeSignaturesAsync(IReadOnlyCollection<long> ids, DateTimeOffset windowFromUtc, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<ApplianceSignature>();
        var list = ids.ToList();
        var from = windowFromUtc.ToUnixTimeSeconds();
        var stats = await _db.MeterEvents.AsNoTracking()
            .Where(e => e.SignatureId != null && list.Contains(e.SignatureId.Value) && e.Ts >= from)
            .GroupBy(e => e.SignatureId!.Value)
            .Select(g => new
            {
                Id = g.Key,
                Count = g.Count(),
                P = g.Average(e => e.DeltaP >= 0 ? e.DeltaP : -e.DeltaP),
                Q = g.Average(e => e.DeltaP >= 0 ? e.DeltaQ : -e.DeltaQ),
                // Only switch-ons whose transient is known (TransientRule): rise 0 s or ratio > 1.5.
                OnT = g.Sum(e => e.DeltaP > 0 && (e.RiseSeconds < TransientRule.MaxRiseSeconds || e.TransientRatio > TransientRule.MinInrushRatio) ? e.TransientRatio : 0),
                On = g.Sum(e => e.DeltaP > 0 && (e.RiseSeconds < TransientRule.MaxRiseSeconds || e.TransientRatio > TransientRule.MinInrushRatio) ? 1 : 0),
                Last = g.Max(e => e.Ts),
            })
            .ToDictionaryAsync(x => x.Id, ct);

        var entities = await _db.ApplianceSignatures.Where(s => list.Contains(s.Id)).ToListAsync(ct);
        foreach (var s in entities)
        {
            if (stats.TryGetValue(s.Id, out var st))
            {
                s.Count = st.Count;
                s.MeanDeltaP = st.P;
                s.MeanDeltaQ = st.Q;
                s.MeanTransient = st.On > 0 ? st.OnT / st.On : 0;
                s.LastSeen = Math.Max(s.LastSeen ?? 0, st.Last);   // only the window is read (index SignatureId, Ts)
            }
            else
            {
                s.Count = 0;
            }
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return entities.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<SignaturePlugShare>> PlugSharesAsync(IReadOnlyCollection<long> ids, DateTimeOffset windowFromUtc,
        IReadOnlyCollection<Guid> excludedPlugs, CancellationToken ct)
    {
        if (ids.Count == 0) return Array.Empty<SignaturePlugShare>();
        var list = ids.ToList();
        var excluded = excludedPlugs.ToList();
        var from = windowFromUtc.ToUnixTimeSeconds();
        var plug = (int)EventLabelSource.AutoPlug;
        var totals = await _db.MeterEvents.AsNoTracking()
            .Where(e => e.SignatureId != null && list.Contains(e.SignatureId.Value) && e.Ts >= from)
            .GroupBy(e => e.SignatureId!.Value).Select(g => new { Id = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var byPlug = await _db.MeterEvents.AsNoTracking()
            .Where(e => e.SignatureId != null && list.Contains(e.SignatureId.Value) && e.Ts >= from && e.Source == plug
                        && e.LabelPlugId != null && !excluded.Contains(e.LabelPlugId.Value))
            .GroupBy(e => new { Sig = e.SignatureId!.Value, Plug = e.LabelPlugId!.Value })
            .Select(g => new { g.Key.Sig, g.Key.Plug, N = g.Count(), Label = g.Max(e => e.Label) })
            .ToListAsync(ct);
        var bySignature = byPlug.ToLookup(x => x.Sig);
        return list.Select(id =>
        {
            var top = bySignature[id].OrderByDescending(x => x.N).FirstOrDefault();
            return new SignaturePlugShare(id, totals.GetValueOrDefault(id), top?.Plug, top?.Label, top?.N ?? 0);
        }).ToList();
    }

    public async Task<IReadOnlyList<ApplianceRun>> SaveRunsAsync(IReadOnlyList<ApplianceRun> runs, CancellationToken ct)
    {
        if (runs.Count == 0) return Array.Empty<ApplianceRun>();
        var ids = runs.Where(r => r.Id != 0).Select(r => r.Id).ToList();
        var existing = await _db.ApplianceRuns.Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
        var entities = new List<ApplianceRunEntity>();
        foreach (var run in runs)
        {
            if (run.Id == 0 || !existing.TryGetValue(run.Id, out var entity))
            {
                entity = new ApplianceRunEntity();
                _db.ApplianceRuns.Add(entity);
            }
            Copy(run, entity);
            entities.Add(entity);
        }
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return entities.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<ApplianceRun>> ListOpenRunsAsync(CancellationToken ct)
    {
        var rows = await _db.ApplianceRuns.AsNoTracking().Where(r => r.EndTs == null).OrderBy(r => r.StartTs).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<ApplianceRun>> ListRunsBySignatureAsync(long signatureId, DateTimeOffset fromUtc, CancellationToken ct)
    {
        var from = fromUtc.ToUnixTimeSeconds();
        var rows = await _db.ApplianceRuns.AsNoTracking()
            .Where(r => r.SignatureId == signatureId && (r.EndTs == null || r.EndTs >= from)).OrderBy(r => r.StartTs).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<IReadOnlyList<ApplianceRun>> ListRunsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        long from = fromUtc.ToUnixTimeSeconds(), to = toUtc.ToUnixTimeSeconds();
        var rows = await _db.ApplianceRuns.AsNoTracking()
            .Where(r => r.StartTs < to && (r.EndTs == null || r.EndTs >= from)).OrderBy(r => r.StartTs).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<int> DeleteRunsAsync(IReadOnlyCollection<long>? signatureIds, DateTimeOffset fromUtc, CancellationToken ct)
    {
        var from = fromUtc.ToUnixTimeSeconds();
        var q = _db.ApplianceRuns.Where(r => r.StartTs >= from);
        if (signatureIds is not null)
        {
            var ids = signatureIds.ToList();
            q = q.Where(r => ids.Contains(r.SignatureId));
        }
        return await q.ExecuteDeleteAsync(ct);
    }

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        var cutoff = cutoffUtc.ToUnixTimeSeconds();
        var runs = await BatchDelete.RunAsync(() => _db.ApplianceRuns.Where(r => r.StartTs < cutoff && r.EndTs != null && r.EndTs < cutoff).OrderBy(r => r.StartTs), ct);
        var events = await BatchDelete.RunAsync(() => _db.MeterEvents.Where(e => e.Ts < cutoff).OrderBy(e => e.Ts), ct);
        return runs + events;
    }
}
