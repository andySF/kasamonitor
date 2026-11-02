using KasaMonitor.Core.Climate;

namespace KasaMonitor.Core.Abstractions;

/// <summary>Storage of the climate sensors and their readings (tables ClimateSensors, ClimateReadings).</summary>
public interface IClimateRepository
{
    Task<IReadOnlyList<ClimateSensor>> ListSensorsAsync(bool includeArchived, CancellationToken ct);

    /// <summary>
    /// The sensor of (source, external id); created with the given name, room and „afară” when missing (an existing
    /// sensor keeps its edited values).
    /// </summary>
    Task<ClimateSensor> GetOrAddSensorAsync(string source, string externalId, string name, string room, bool isOutdoor, CancellationToken ct);

    /// <summary>Sets LastSeenUtc and, when given, the battery.</summary>
    Task MarkSeenAsync(Guid sensorId, DateTimeOffset seenUtc, int? batteryPercent, CancellationToken ct);

    /// <summary>The fields edited in /clima. An empty name is refused.</summary>
    Task<ClimateSensor> UpdateSensorAsync(Guid sensorId, string name, string room, bool isOutdoor, CancellationToken ct);

    Task<ClimateSensor> SetArchivedAsync(Guid sensorId, bool archived, CancellationToken ct);

    /// <summary>Stores a reading; a second reading of the same sensor and second is ignored.</summary>
    Task AddReadingAsync(ClimateReading reading, CancellationToken ct);

    /// <summary>The newest stored reading of every sensor that has one.</summary>
    Task<IReadOnlyDictionary<Guid, ClimateReading>> LatestReadingsAsync(CancellationToken ct);

    /// <summary>Readings of the sensors in [from, to], oldest first, per sensor.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ClimateReading>>> ListReadingsAsync(IReadOnlyCollection<Guid> sensorIds,
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Deletes the readings older than <paramref name="cutoffUtc"/>; returns the number of rows.</summary>
    Task<int> DeleteReadingsBeforeAsync(DateTimeOffset cutoffUtc, CancellationToken ct);
}
