using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.Abstractions;

public interface IHistoryRepository
{
    Task UpsertDailyAsync(Guid plugId, IReadOnlyList<DailyKwh> entries, CancellationToken ct);
    Task UpsertMonthlyAsync(Guid plugId, IReadOnlyList<MonthlyKwh> entries, CancellationToken ct);
    Task<IReadOnlyList<DailyKwh>> GetDailyRangeAsync(Guid plugId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct);
    /// <summary>Daily energy of every plug in the range, in one query.</summary>
    Task<IReadOnlyList<PlugDailyKwh>> GetDailyRangeAllAsync(DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct);
    Task<IReadOnlyList<MonthlyKwh>> GetMonthlyRangeAsync(Guid plugId, int yearFromInclusive, int yearToInclusive, CancellationToken ct);

    /// <summary>Device month totals (MonthlyEnergy) of every plug in the years, in one query.</summary>
    Task<IReadOnlyList<PlugMonthWh>> GetMonthlyRangeAllAsync(int yearFromInclusive, int yearToInclusive, CancellationToken ct);

    /// <summary>Σ of the stored days (DailyEnergy) per plug and month of the years, in one grouped query.</summary>
    Task<IReadOnlyList<PlugMonthWh>> SumDailyByMonthAllAsync(int yearFromInclusive, int yearToInclusive, CancellationToken ct);

    /// <summary>The first day stored for a plug (null without any).</summary>
    Task<DateOnly?> FirstDailyDayAsync(Guid plugId, CancellationToken ct);
}
