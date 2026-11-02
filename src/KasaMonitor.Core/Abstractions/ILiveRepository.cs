using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.Abstractions;

/// <summary>Storage of the live stream (raw samples on change + minute aggregates) and the relay command log.</summary>
public interface ILiveRepository
{
    /// <summary>
    /// Inserts raw samples and stores minute aggregates in one transaction (one SaveChanges). An aggregate for a minute
    /// that already has a row is merged into it (<see cref="LiveMinuteAggregate.Merge"/>), never overwritten.
    /// </summary>
    Task SaveBatchAsync(IReadOnlyList<LiveSample> samples, IReadOnlyList<LiveMinuteAggregate> minutes, CancellationToken ct);

    Task<IReadOnlyList<LiveSample>> ListSamplesAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    /// <summary>Second of the oldest stored raw sample of a plug (null when it has none).</summary>
    Task<DateTimeOffset?> FirstSampleTsAsync(Guid plugId, CancellationToken ct);

    Task<IReadOnlyList<LiveMinuteAggregate>> ListMinutesAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>The minute aggregates of several plugs in [from, to] in one query (<c>PlugId IN (...)</c> + range on the key), by plug and minute.</summary>
    Task<IReadOnlyList<LiveMinuteAggregate>> ListMinutesAsync(IReadOnlyCollection<Guid> plugIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Σ energy and count of the minute aggregates per plug and UTC hour, for the given plugs, in one grouped query
    /// (minutes in [from, to); <c>PlugId IN (...)</c> + range on the primary key (PlugId, MinuteTs)).
    /// </summary>
    Task<IReadOnlyList<PlugHourWh>> SumEnergyByHourAsync(IReadOnlyCollection<Guid> plugIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    Task<int> CountSamplesOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    /// <summary>Deletes raw samples older than the cutoff, in batches; minute aggregates are never deleted.</summary>
    Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct, int batchSize = 5000);

    Task<RelayCommand> AddRelayCommandAsync(RelayCommand command, CancellationToken ct);

    /// <summary>The newest relay commands, newest first (all plugs when <paramref name="plugId"/> is null).</summary>
    Task<IReadOnlyList<RelayCommand>> ListRelayCommandsAsync(Guid? plugId, int take, CancellationToken ct);
    /// <summary>The relay commands of every plug in [from, to), newest first, at most <paramref name="take"/> (the events of /history).</summary>
    Task<IReadOnlyList<RelayCommand>> ListRelayCommandsAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, int take, CancellationToken ct);
}
