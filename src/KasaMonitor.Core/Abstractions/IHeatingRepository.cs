using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.Abstractions;

public interface IHeatingRepository
{
    Task<IReadOnlyList<HeatingDevice>> ListDevicesAsync(CancellationToken ct);

    /// <summary>Insert or update by (Provider, InstallationId, GatewaySerial, DeviceId); returns the stored device (its Id).</summary>
    Task<HeatingDevice> UpsertDeviceAsync(HeatingDevice device, CancellationToken ct);

    /// <summary>Sets LastError and, when given, LastSeenUtc.</summary>
    Task UpdateDeviceStatusAsync(Guid deviceId, DateTimeOffset? lastSeenUtc, string? lastError, CancellationToken ct);

    Task SaveReadingAsync(HeatingReading reading, CancellationToken ct);

    Task<IReadOnlyList<HeatingReading>> ListReadingsAsync(Guid deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Only the "today" counters of the readings in the range (hours of a day report), oldest first.</summary>
    Task<IReadOnlyList<HeatingCounterReading>> ListCounterReadingsAsync(Guid deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Σ of the stored days per month of the years, in one grouped query (gas as in <see cref="HeatingEnergyDay.GasHeatingFor"/>).</summary>
    Task<IReadOnlyList<HeatingMonthSums>> SumDailyByMonthAsync(Guid deviceId, int yearFrom, int yearTo, DateOnly today, CancellationToken ct);

    /// <summary>The first stored day (null without any).</summary>
    Task<DateOnly?> FirstDailyDayAsync(Guid deviceId, CancellationToken ct);

    /// <summary>
    /// Upsert of daily rows: the highest values of the day are kept (gas <c>…MaxM3</c>, heat, electricity) and the gas
    /// <c>…LastM3</c> takes the value received. Returns the gas values lower than the stored highest of the same day.
    /// </summary>
    Task<IReadOnlyList<HeatingDayDecrease>> UpsertDailyAsync(IReadOnlyList<HeatingEnergyDay> days, CancellationToken ct);

    Task<IReadOnlyList<HeatingEnergyDay>> ListDailyAsync(Guid deviceId, DateOnly from, DateOnly to, CancellationToken ct);

    /// <summary>Gas: Σ highest of the days before <paramref name="today"/> + last value of today (and later); heat and electricity: Σ highest.</summary>
    Task<HeatingEnergyTotals> GetTotalsAsync(Guid deviceId, DateOnly today, CancellationToken ct);

    /// <summary>Deletes the readings older than <paramref name="cutoffUtc"/>; returns the number of rows.</summary>
    Task<int> DeleteReadingsBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct);

    /// <summary>Replaces the period values of the device that appear in <paramref name="periods"/>.</summary>
    Task UpsertPeriodsAsync(IReadOnlyList<HeatingEnergyPeriod> periods, CancellationToken ct);

    Task<IReadOnlyList<HeatingEnergyPeriod>> ListPeriodsAsync(Guid deviceId, CancellationToken ct);

    /// <summary>Keeps only the latest raw dump per device (overwritten).</summary>
    Task SaveRawAsync(Guid deviceId, DateTimeOffset tsUtc, string json, CancellationToken ct);

    Task<(DateTimeOffset TsUtc, string Json)?> GetRawAsync(Guid deviceId, CancellationToken ct);
}
