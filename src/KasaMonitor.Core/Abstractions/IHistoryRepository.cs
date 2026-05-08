using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface IHistoryRepository
{
    Task UpsertDailyAsync(Guid plugId, IReadOnlyList<DailyKwh> entries, CancellationToken ct);
    Task UpsertMonthlyAsync(Guid plugId, IReadOnlyList<MonthlyKwh> entries, CancellationToken ct);
    Task<IReadOnlyList<DailyKwh>> GetDailyRangeAsync(Guid plugId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct);
    Task<IReadOnlyList<MonthlyKwh>> GetMonthlyRangeAsync(Guid plugId, int yearFromInclusive, int yearToInclusive, CancellationToken ct);
}
