using KasaMonitor.Core.Nilm;

namespace KasaMonitor.Core.Abstractions;

/// <summary>Per signature, how many events (in a window) were labelled by its most frequent plug.</summary>
public sealed record SignaturePlugShare(long SignatureId, int Events, Guid? TopPlugId, string? TopLabel, int TopPlugEvents);

/// <summary>Storage of the appliance detection: events, signatures and runs.</summary>
public interface INilmRepository
{
    /// <summary>
    /// Stores new events in one transaction and returns the stored ones (with ids). An event is skipped as a duplicate
    /// when the same meter already has an event within ±2 s, in the same direction, with |ΔP| within the clustering
    /// tolerance (a second re-processed after a restart, possibly shifted by a second).
    /// </summary>
    Task<IReadOnlyList<MeterEvent>> AddEventsAsync(IReadOnlyList<MeterEvent> events, CancellationToken ct);

    /// <summary>Updates the signature and the label of an event.</summary>
    Task UpdateEventAsync(MeterEvent e, CancellationToken ct);

    /// <summary>Sets the signature of many events at once (re-clustering).</summary>
    Task SetSignaturesAsync(IReadOnlyDictionary<long, long?> signatureByEvent, CancellationToken ct);

    Task<MeterEvent?> GetEventAsync(long id, CancellationToken ct);

    /// <summary>The current signature of each given event (events that no longer exist are missing).</summary>
    Task<IReadOnlyDictionary<long, long?>> GetSignatureIdsAsync(IReadOnlyCollection<long> eventIds, CancellationToken ct);
    Task<IReadOnlyList<MeterEvent>> ListEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>The newest <paramref name="max"/> events of the range, oldest first.</summary>
    Task<IReadOnlyList<MeterEvent>> ListNewestEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int max, CancellationToken ct);

    /// <summary>One page of the range, newest first.</summary>
    Task<IReadOnlyList<MeterEvent>> ListEventsPageAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int skip, int take, CancellationToken ct);

    /// <summary>Events of the given signatures from <paramref name="fromUtc"/>, oldest first.</summary>
    Task<IReadOnlyList<MeterEvent>> ListEventsBySignaturesAsync(IReadOnlyCollection<long> signatureIds, DateTimeOffset fromUtc, CancellationToken ct);

    /// <summary>
    /// The newest <paramref name="max"/> events labelled by a plug or by the user (the training set), leaving out the
    /// labels of the excluded plugs.
    /// </summary>
    Task<IReadOnlyList<MeterEvent>> ListLabeledEventsAsync(IReadOnlyCollection<Guid> excludedPlugs, int max, CancellationToken ct);

    /// <summary>Number of labelled events and of distinct labels (same filter as <see cref="ListLabeledEventsAsync"/>).</summary>
    Task<(int Events, int Classes)> CountLabeledAsync(IReadOnlyCollection<Guid> excludedPlugs, CancellationToken ct);

    Task<int> CountEventsAsync(CancellationToken ct);
    Task<int> CountEventsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    Task<IReadOnlyList<ApplianceSignature>> ListSignaturesAsync(CancellationToken ct);
    Task<ApplianceSignature?> GetSignatureAsync(long id, CancellationToken ct);

    /// <summary>Adds a signature; an empty name becomes "Necunoscut #&lt;id&gt;".</summary>
    Task<ApplianceSignature> AddSignatureAsync(ApplianceSignature s, CancellationToken ct);

    Task UpdateSignatureAsync(ApplianceSignature s, CancellationToken ct);

    /// <summary>Deletes a signature and its runs; its events lose the signature.</summary>
    Task DeleteSignatureAsync(long id, CancellationToken ct);

    /// <summary>
    /// Merge: in one transaction, every event and every run of <paramref name="fromId"/> moves to <paramref name="intoId"/>
    /// and <paramref name="fromId"/> is deleted. Returns the number of events moved.
    /// </summary>
    Task<int> MoveSignatureAsync(long fromId, long intoId, CancellationToken ct);

    /// <summary>
    /// Recomputes, with SQL aggregates over the events since <paramref name="windowFromUtc"/>, the count, means and
    /// LastSeen (the newer of the stored one and the window's newest event) of the given signatures and returns them.
    /// </summary>
    Task<IReadOnlyList<ApplianceSignature>> RecomputeSignaturesAsync(IReadOnlyCollection<long> ids, DateTimeOffset windowFromUtc, CancellationToken ct);

    /// <summary>Per signature: events since <paramref name="windowFromUtc"/> and the plug that labelled most of them (SQL GROUP BY).</summary>
    Task<IReadOnlyList<SignaturePlugShare>> PlugSharesAsync(IReadOnlyCollection<long> ids, DateTimeOffset windowFromUtc,
        IReadOnlyCollection<Guid> excludedPlugs, CancellationToken ct);

    /// <summary>Inserts the runs with Id 0 and updates the others, in one transaction; returns them with their ids.</summary>
    Task<IReadOnlyList<ApplianceRun>> SaveRunsAsync(IReadOnlyList<ApplianceRun> runs, CancellationToken ct);

    Task<IReadOnlyList<ApplianceRun>> ListOpenRunsAsync(CancellationToken ct);

    /// <summary>The runs of one signature not ended before <paramref name="fromUtc"/> (open ones included), oldest first.</summary>
    Task<IReadOnlyList<ApplianceRun>> ListRunsBySignatureAsync(long signatureId, DateTimeOffset fromUtc, CancellationToken ct);

    /// <summary>Runs overlapping [fromUtc, toUtc): started before the end and not ended before the start.</summary>
    Task<IReadOnlyList<ApplianceRun>> ListRunsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Deletes the runs of the given signatures (all signatures when null) that started at or after <paramref name="fromUtc"/>.</summary>
    Task<int> DeleteRunsAsync(IReadOnlyCollection<long>? signatureIds, DateTimeOffset fromUtc, CancellationToken ct);

    /// <summary>Retention: deletes events and runs older than the cutoff (signatures stay).</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);
}
