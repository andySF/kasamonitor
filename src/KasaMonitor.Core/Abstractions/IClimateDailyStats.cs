using KasaMonitor.Core.History;

namespace KasaMonitor.Core.Abstractions;

/// <summary>
/// Daily climate statistics of sensors (min / time-weighted mean / max per local day), the input of /history's climate
/// section, its heating degree days and the energy signature: <c>ClimateDailyStore</c> reads the stored days
/// (<c>ClimateDaily</c>) and computes only today and the days not stored yet from the raw readings
/// (<c>RawClimateDailyStats</c>).
/// </summary>
public interface IClimateDailyStats
{
    /// <summary>The days [from, to] (local, <paramref name="tz"/>) of every given sensor that has a value on them.</summary>
    Task<IReadOnlyList<ClimateDayStats>> GetAsync(IReadOnlyCollection<Guid> sensorIds, DateOnly from, DateOnly to, TimeZoneInfo tz, CancellationToken ct);
}
