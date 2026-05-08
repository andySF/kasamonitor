using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface ISampleRepository
{
    Task InsertAsync(Sample sample, CancellationToken ct);
    Task InsertManyAsync(IReadOnlyList<Sample> samples, CancellationToken ct);
    Task<IReadOnlyList<Sample>> ListByPlugAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);
}
