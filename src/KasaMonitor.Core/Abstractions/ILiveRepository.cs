using KasaMonitor.Core.Models;

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
    Task<IReadOnlyList<LiveMinuteAggregate>> ListMinutesAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    Task<int> CountSamplesOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    /// <summary>Deletes raw samples older than the cutoff, in batches; minute aggregates are never deleted.</summary>
    Task<int> DeleteSamplesOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct, int batchSize = 5000);

    Task<RelayCommand> AddRelayCommandAsync(RelayCommand command, CancellationToken ct);

    /// <summary>The newest relay commands, newest first (all plugs when <paramref name="plugId"/> is null).</summary>
    Task<IReadOnlyList<RelayCommand>> ListRelayCommandsAsync(Guid? plugId, int take, CancellationToken ct);
}
