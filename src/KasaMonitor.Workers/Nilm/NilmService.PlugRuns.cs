using System.Runtime.CompilerServices;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers.Nilm;

/// <summary>
/// The side of a plug with a single appliance (0.13.2): its runs come from its own stored readings
/// (<see cref="PlugTimeline"/>), never from the main meter's events, and the event that is only its step seen again by the
/// main meter is marked as a shadow (<see cref="PlugShadows"/>). A replay of the stored rows and the live step are one code
/// path: the live step feeds the seconds that arrived since the last one.
/// </summary>
public sealed partial class NilmService
{
    /// <summary>How often the plugs' timelines advance (two indexed queries per plug).</summary>
    public static readonly TimeSpan PlugStepInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The timelines stop this long before now: the stream's rows are written every 10 s
    /// (<see cref="LiveStreamWorker.FlushInterval"/>), so a second is final only after that. A transition is therefore known
    /// about 50 s after it happened, usually after its event was stored: the shadow corrects the event afterwards.
    /// </summary>
    public static readonly TimeSpan PlugDataLag = TimeSpan.FromSeconds(30);

    /// <summary>A transition is checked for its shadow this long after it happened: every event up to it is stored by then.</summary>
    public static readonly TimeSpan ShadowDelay = LabelDelay + TimeSpan.FromSeconds(15);

    /// <summary>A replay checks for shadows this far back: an outage longer than that re-clusters after the backfill, which recalculates them all.</summary>
    public static readonly TimeSpan ShadowLookBack = ReclusterAfterBackfill;

    /// <summary>The seconds a timeline is fed before the time it starts from, so its first transition has its state and its level before.</summary>
    private static readonly TimeSpan PlugWarmUp = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan PlugReplayChunk = TimeSpan.FromDays(1);

    /// <summary>A plug's timeline: where it has got to, the run still open on it (stored, or about to be) and the signature of its runs.</summary>
    private sealed class PlugTrack(PlugTimeline timeline, DateTimeOffset cursor)
    {
        public PlugTimeline Timeline { get; } = timeline;
        public DateTimeOffset Cursor { get; set; } = cursor;
        public ApplianceRun? Open { get; set; }
        public long? TargetId { get; set; }
    }

    private readonly Dictionary<Guid, PlugTrack> _plugTracks = new();   // under the processing lock
    private readonly List<(Guid PlugId, PlugTransition Transition)> _transitionsToCheck = new();
    private DateTimeOffset _lastPlugStep = DateTimeOffset.MinValue;

    private static DateTimeOffset WholeSecond(DateTimeOffset t) => DateTimeOffset.FromUnixTimeSeconds(t.ToUnixTimeSeconds());

    private IReadOnlyList<ApplianceRun> OpenRuns() => _runs.Open().Concat(_plugTracks.Values.Select(t => t.Open).OfType<ApplianceRun>().Where(r => r.Id != 0)).ToList();

    /// <summary>
    /// One pass (at most every <see cref="PlugStepInterval"/>): every plug with a single appliance gets the seconds that arrived,
    /// a plug without a timeline yet (start, the flag just set) catches up from its last run, and the transitions now old
    /// enough are checked for their shadows.
    /// </summary>
    private async Task StepPlugsAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (nowUtc - _lastPlugStep < PlugStepInterval) return;
        _lastPlugStep = nowUtc;
        var single = _plugs.Where(p => p.Appliance is not null).ToList();
        foreach (var gone in _plugTracks.Keys.Where(id => single.All(p => p.Id != id)).ToList()) _plugTracks.Remove(gone);
        if (single.Count > 0)
        {
            using var scope = _scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<INilmRepository>();
            var started = false;
            foreach (var plug in single)
            {
                if (_plugTracks.TryGetValue(plug.Id, out var track))
                {
                    await FeedAsync(scope.ServiceProvider, plug, track, nowUtc, ct);
                }
                else
                {
                    await RebuildPlugRunsAsync(scope.ServiceProvider, plug, await ResumeFromAsync(repo, plug, nowUtc, ct), nowUtc, ct);
                    started = true;
                }
            }
            // Older runs from events of these signatures may just have gone: the builder must not save them again.
            if (started) _runs = new ApplianceRunBuilder(await repo.ListOpenRunsAsync(ct));
            await CheckShadowsAsync(scope.ServiceProvider, nowUtc, ct);
        }
        _openSnapshot = OpenRuns();
    }

    /// <summary>Where a plug catches up from: the start of its newest run from the plug in the window, otherwise the window.</summary>
    private async Task<DateTimeOffset> ResumeFromAsync(INilmRepository repo, Plug plug, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var window = WindowStart(nowUtc);
        var newest = window;
        foreach (var s in (await repo.ListSignaturesAsync(ct)).Where(s => s.PlugId == plug.Id))
            foreach (var r in await repo.ListRunsBySignatureAsync(s.Id, window, ct))
                if (r.FromPlug && r.StartUtc > newest) newest = r.StartUtc;
        return newest;
    }

    /// <summary>The oldest reading of the plug, stream or poll; null: nothing to build from.</summary>
    private static async Task<DateTimeOffset?> FirstPlugDataAsync(IServiceProvider sp, Guid plugId, CancellationToken ct)
    {
        var stream = await sp.GetRequiredService<ILiveRepository>().FirstSampleTsAsync(plugId, ct);
        var polled = await sp.GetRequiredService<ISampleRepository>().FirstReadingTsAsync(plugId, ct);
        return stream is { } a && polled is { } b ? (a < b ? a : b) : stream ?? polled;
    }

    /// <summary>
    /// The plug's runs from <paramref name="fromUtc"/> again, from its stored readings; what follows continues on this timeline.
    /// Nothing older than the plug's first data goes: nothing could rebuild it. Runs of all the signatures of the plug in that
    /// span are replaced by one set on one signature, so a merge or a split doubles nothing, and a second rebuild gives the same.
    /// </summary>
    private async Task RebuildPlugRunsAsync(IServiceProvider sp, Plug plug, DateTimeOffset fromUtc, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<INilmRepository>();
        if (await FirstPlugDataAsync(sp, plug.Id, ct) is not { } first)
        {
            _plugTracks[plug.Id] = new PlugTrack(new PlugTimeline(), WholeSecond(nowUtc - PlugDataLag));
            return;
        }
        var from = WholeSecond(fromUtc > first ? fromUtc : first);
        var own = (await repo.ListSignaturesAsync(ct)).Where(s => s.PlugId == plug.Id).Select(s => s.Id).ToList();

        // A run still open from before: rebuilt whole when its start has data, closed as uncertain when not.
        var open = (await repo.ListOpenRunsAsync(ct)).Where(r => r.FromPlug && own.Contains(r.SignatureId)).ToList();
        if (open.Where(r => r.StartUtc >= first).Select(r => (DateTimeOffset?)r.StartUtc).Min() is { } start && start < from) from = WholeSecond(start);
        var stale = open.Where(r => r.StartUtc < from).Select(r => r with { EndUtc = from, EstimatedWh = 0, EndReason = RunEndReason.Timeout }).ToList();
        if (stale.Count > 0) await repo.SaveRunsAsync(stale, ct);
        if (own.Count > 0) await repo.DeleteRunsAsync(own, from, ct);

        var track = new PlugTrack(new PlugTimeline(from), from - PlugWarmUp - TimeSpan.FromSeconds(1));
        _plugTracks[plug.Id] = track;
        await FeedAsync(sp, plug, track, nowUtc, ct);
    }

    /// <summary>Feeds a timeline up to <see cref="PlugDataLag"/> before now and stores the runs that opened and closed.</summary>
    private async Task FeedAsync(IServiceProvider sp, Plug plug, PlugTrack track, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var to = WholeSecond(nowUtc - PlugDataLag);
        if (track.Cursor >= to) return;
        var repo = sp.GetRequiredService<INilmRepository>();
        var changed = new List<ApplianceRun>();
        try
        {
            await foreach (var step in ReplayAsync(sp, plug.Id, track.Timeline, track.Cursor.AddSeconds(1), to, ct))
            {
                if (step.Transition is { } t && nowUtc - t.TsUtc <= ShadowLookBack) _transitionsToCheck.Add((plug.Id, t));
                if (step.Closed is { } closed && track.Open is { } running)
                {
                    changed.Add(closed.ToRun(running.Id, running.SignatureId));
                    track.Open = null;
                }
                if (step.Opened is { } opened) track.Open = opened.ToRun(0, await RunTargetAsync(repo, plug, track, ct));
            }
        }
        catch
        {
            _plugTracks.Remove(plug.Id);   // its state went past what is stored: the next pass catches up from the stored runs
            throw;
        }
        track.Cursor = to;
        var isNew = track.Open is { Id: 0 };
        if (isNew) changed.Add(track.Open!);
        if (changed.Count == 0) return;
        var saved = await repo.SaveRunsAsync(changed, ct);
        if (isNew) track.Open = saved[^1];
    }

    /// <summary>The steps of a timeline over [from, to], with the stored rows loaded a day at a time.</summary>
    private static async IAsyncEnumerable<PlugStep> ReplayAsync(IServiceProvider sp, Guid plugId, PlugTimeline timeline, DateTimeOffset fromUtc, DateTimeOffset toUtc,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var live = sp.GetRequiredService<ILiveRepository>();
        var samples = sp.GetRequiredService<ISampleRepository>();
        for (var from = fromUtc; from <= toUtc; from += PlugReplayChunk)
        {
            var until = from + PlugReplayChunk - TimeSpan.FromSeconds(1) < toUtc ? from + PlugReplayChunk - TimeSpan.FromSeconds(1) : toUtc;
            var load = from - PlugTransitions.MaxRowAge;
            var stream = (await live.ListSamplesAsync(plugId, load, until, ct)).Select(x => new PlugPowerPoint(x.TsUtc, x.PowerW)).OrderBy(p => p.TsUtc).ToList();
            var polled = (await samples.ListByPlugAsync(plugId, load, until, ct)).Where(r => r.Online && r.PowerMw is not null)
                .Select(r => new PlugPowerPoint(r.TimestampUtc, r.PowerMw!.Value / 1000.0)).OrderBy(p => p.TsUtc).ToList();
            var grid = PlugTransitions.Grid(stream, polled, from, until);
            var next = 0;
            for (var s = from; s <= until; s = s.AddSeconds(1))
            {
                double? watts = null;
                if (next < grid.Count && grid[next].TsUtc == s) watts = grid[next++].PowerW;
                var step = timeline.Add(s, watts);
                if (step != default) yield return step;
            }
        }
    }

    /// <summary>
    /// The one signature of the plug's runs (<see cref="ExclusiveSignatures.RunTarget"/>); without any, a new one made like
    /// that of a first confirmation: its centre stays empty until a confirmation sets it.
    /// </summary>
    private async Task<long> RunTargetAsync(INilmRepository repo, Plug plug, PlugTrack track, CancellationToken ct)
    {
        if (track.TargetId is { } known && _signatures.Any(s => s.Id == known)) return known;
        var own = (await repo.ListSignaturesAsync(ct)).Where(s => s.PlugId == plug.Id).ToList();
        if (own.Count > 1)
        {
            var events = await repo.ListEventsBySignaturesAsync(own.Select(s => s.Id).ToList(), WindowStart(_clock()), ct);
            track.TargetId = ExclusiveSignatures.RunTarget(own, plug.Id, events);
        }
        else if (own.Count == 1)
        {
            track.TargetId = own[0].Id;
        }
        else
        {
            var cat = ApplianceCategories.Get(plug.ApplianceCategory);
            var sig = await repo.AddSignatureAsync(new ApplianceSignature(0, plug.Appliance!, cat.Icon, cat.Key, 0, 0, 0, 0, null, SignatureLabelSource.Plug, plug.Id, false), ct);
            _signatures.Add(sig);
            track.TargetId = sig.Id;
            _log.LogInformation("NILM: signature #{Id} created for the runs of {Name}, plug {Alias}", sig.Id, sig.Name, plug.Alias);
        }
        if (_signatures.All(s => s.Id != track.TargetId)) _signatures.Add(own.First(s => s.Id == track.TargetId));
        return track.TargetId!.Value;
    }

    /// <summary>
    /// The transitions old enough, with every event up to them stored: the only candidate of a transition becomes a shadow. It
    /// was processed like any event, so it leaves its signature and the runs of that signature are built again. A run
    /// that already closed stays in the cumulative energy metric (a counter, as with every rebuild).
    /// </summary>
    private async Task CheckShadowsAsync(IServiceProvider sp, DateTimeOffset nowUtc, CancellationToken ct)
    {
        if (_transitionsToCheck.Count == 0) return;
        DateTimeOffset? oldestPending;
        lock (_pending) oldestPending = _pending.Count > 0 ? _pending.Min(e => e.TsUtc) : null;
        bool Ripe((Guid PlugId, PlugTransition Transition) x) => nowUtc - x.Transition.TsUtc >= ShadowDelay && (oldestPending is not { } p || p > x.Transition.TsUtc);
        var ripe = _transitionsToCheck.Where(Ripe).ToList();
        if (ripe.Count == 0) return;
        _transitionsToCheck.RemoveAll(x => Ripe(x));

        var repo = sp.GetRequiredService<INilmRepository>();
        var events = await repo.ListEventsAsync(ripe.Min(x => x.Transition.TsUtc) - PlugShadows.MaxLead, ripe.Max(x => x.Transition.TsUtc), ct);
        var found = PlugShadows.Find(ripe, events);
        var marked = events.Where(e => found.ContainsKey(e.Id) && e.Source != EventLabelSource.PlugShadow).ToList();
        if (marked.Count == 0) return;
        await repo.SetPlugShadowsAsync(marked.ToDictionary(e => e.Id, e => found[e.Id]), Array.Empty<long>(), ct);
        _log.LogDebug("NILM: {Count} events marked as shadows of a plug", marked.Count);

        var touched = marked.Where(e => e.SignatureId is not null).Select(e => e.SignatureId!.Value).Distinct().ToList();
        if (touched.Count == 0) return;
        await RefreshSignaturesAsync(repo, touched, ct);
        var exclusive = ExclusivePlugs;
        var fromEvents = _signatures.Where(s => touched.Contains(s.Id) && !ExclusiveSignatures.IsExclusive(s, exclusive)).Select(s => s.Id).ToList();
        if (fromEvents.Count > 0) await RebuildRunsAsync(sp, fromEvents, marked.Min(e => e.TsUtc) - ApplianceRunBuilder.MaxOpen, nowUtc, ct);
    }

    /// <summary>
    /// The shadows of the window again, where the plugs have data (the start of a re-clustering): an event that became the only
    /// candidate of a transition is marked and leaves its signature; a shadow no transition explains any more is released, without
    /// a signature, and goes through the anchors and DBSCAN like any event. The same data give the same marks.
    /// </summary>
    private async Task RecalculateShadowsAsync(IServiceProvider sp, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var repo = sp.GetRequiredService<INilmRepository>();
        var window = WindowStart(nowUtc);
        var ripeUntil = nowUtc - ShadowDelay;
        var covered = new Dictionary<Guid, DateTimeOffset>();
        var transitions = new List<(Guid PlugId, PlugTransition Transition)>();
        foreach (var plug in _plugs.Where(p => p.Appliance is not null))
        {
            if (await FirstPlugDataAsync(sp, plug.Id, ct) is not { } first) continue;
            var from = WholeSecond(window > first ? window : first);
            covered[plug.Id] = from;
            await foreach (var step in ReplayAsync(sp, plug.Id, new PlugTimeline(from), from - PlugWarmUp, WholeSecond(nowUtc - PlugDataLag), ct))
                if (step.Transition is { } t && t.TsUtc <= ripeUntil) transitions.Add((plug.Id, t));
        }

        var events = await repo.ListNewestEventsAsync(window, nowUtc, SignatureClusterer.MaxEvents, ct);
        var found = PlugShadows.Find(transitions, events);
        var exclusive = ExclusivePlugs;
        bool Unexplained(MeterEvent e) => e.LabelPlugId is not { } p || !exclusive.Contains(p)
            // The first seconds of the data cannot show a transition, whatever was there.
            || (covered.TryGetValue(p, out var from) && e.TsUtc >= from + PlugEventMatcher.LoadMargin && e.TsUtc <= ripeUntil);
        var marks = events.Where(e => found.TryGetValue(e.Id, out var plug) && (e.Source != EventLabelSource.PlugShadow || e.LabelPlugId != plug)).ToDictionary(e => e.Id, e => found[e.Id]);
        var released = events.Where(e => e.Source == EventLabelSource.PlugShadow && !found.ContainsKey(e.Id) && Unexplained(e)).Select(e => e.Id).ToList();
        await repo.SetPlugShadowsAsync(marks, released, ct);
        if (marks.Count > 0 || released.Count > 0) _log.LogInformation("NILM re-clustering: {Marked} events marked as shadows of a plug, {Released} released", marks.Count, released.Count);
    }
}
