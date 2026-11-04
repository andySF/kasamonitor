using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface ISampleRepository
{
    Task InsertAsync(Sample sample, CancellationToken ct);
    Task InsertManyAsync(IReadOnlyList<Sample> samples, CancellationToken ct);
    Task<IReadOnlyList<Sample>> ListByPlugAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    /// <summary>The time of the plug's oldest reading with a power, or null.</summary>
    Task<DateTimeOffset?> FirstReadingTsAsync(Guid plugId, CancellationToken ct);
    Task<int> CountOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    /// <summary>Mean power (W) of the online samples with a power value per plug in [from, to), for the given plugs, in one grouped query.</summary>
    Task<IReadOnlyDictionary<Guid, double>> AveragePowerByPlugAsync(IReadOnlyCollection<Guid> plugIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Deletes samples older than the cutoff in batches of <paramref name="batchSize"/> rows (short write transactions).</summary>
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct, int batchSize = 5000);
}
