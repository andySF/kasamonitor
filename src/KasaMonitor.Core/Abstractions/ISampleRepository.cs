using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface ISampleRepository
{
    Task InsertAsync(Sample sample, CancellationToken ct);
    Task InsertManyAsync(IReadOnlyList<Sample> samples, CancellationToken ct);
    Task<IReadOnlyList<Sample>> ListByPlugAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    Task<int> CountOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    /// <summary>Deletes samples older than the cutoff in batches of <paramref name="batchSize"/> rows (short SQLite write locks).</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct, int batchSize = 5000);
}
