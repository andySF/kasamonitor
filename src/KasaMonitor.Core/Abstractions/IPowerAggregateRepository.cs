using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.Abstractions;

/// <summary>
/// The power aggregates of the plugs (0.14.0): minute rows (OneMinuteAggregates, rolled from the raw readings) and hour rows
/// (OneHourAggregates, rolled from the minutes). Both are kept forever: the power chart of /history reads only these. Every
/// read is by plug and time range, on the key (PlugId, BucketStartUtc).
/// </summary>
public interface IPowerAggregateRepository
{
    /// <summary>The minute rows of a plug in [from, to), oldest first (<see cref="PowerAggregate.Minutes"/> = 1).</summary>
    Task<IReadOnlyList<PowerAggregate>> ListMinutesAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>The hour rows of a plug in [from, to), oldest first (<see cref="PowerAggregate.Minutes"/> = the minutes in the hour).</summary>
    Task<IReadOnlyList<PowerAggregate>> ListHoursAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>
    /// Writes hour rows of a plug: a new hour is added, a stored one is replaced only when the new one has at least as many
    /// minutes (<see cref="PowerHistory.Replaces"/>). Returns the rows added or replaced.
    /// </summary>
    Task<int> UpsertHoursAsync(Guid plugId, IReadOnlyList<PowerHour> hours, CancellationToken ct);

    /// <summary>The first and the last minute row of a plug; null without any.</summary>
    Task<(DateTimeOffset First, DateTimeOffset Last)?> MinuteSpanAsync(Guid plugId, CancellationToken ct);

    /// <summary>The UTC days on which a plug has at least one hour row.</summary>
    Task<IReadOnlySet<DateOnly>> HourDaysAsync(Guid plugId, CancellationToken ct);
}
