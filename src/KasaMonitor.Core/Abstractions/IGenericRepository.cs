using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;

namespace KasaMonitor.Core.Abstractions;

/// <summary>
/// A generic device (table <c>GenericDevices</c>, 0.11): one physical device of a self-describing integration (MQTT
/// Discovery), added by the user (or automatically). (Integration, ExternalId) is unique; the name follows the device
/// until the user changes it. The category is the automatic one unless overridden.
/// </summary>
public sealed record GenericDevice(
    long Id,
    string Integration,
    string ExternalId,
    string Name,
    bool NameIsCustom,
    string Room,
    string? Manufacturer,
    string? Model,
    string? Firmware,
    string? ViaDevice,
    DeviceCategory AutoCategory,
    DeviceCategory? CategoryOverride,
    bool IsArchived,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset? LastSeenUtc)
{
    public DeviceCategory Category => CategoryOverride ?? AutoCategory;
}

/// <summary>
/// A generic entity (table <c>GenericEntities</c>): its description (rewritten at every discovery message) and the raw
/// discovery payload (<see cref="DiscoveryJson"/>, for diagnostics and re-parsing after an upgrade). A removed entity
/// (empty retained payload) keeps its rows and its history.
/// </summary>
public sealed record GenericEntityRow(
    long Id,
    long DeviceId,
    string ExternalId,
    string Component,
    DeviceEntity Description,
    string DiscoveryJson,
    bool IsRemoved,
    double? LastNumber,
    string? LastText,
    DateTimeOffset? LastTsUtc);

/// <summary>What the discovery says about one entity, for the upsert.</summary>
public sealed record GenericEntityUpsert(string ExternalId, string Component, DeviceEntity Description, string DiscoveryJson);

/// <summary>One stored value (table <c>GenericReadings</c>, clustered key (EntityId, Ts)).</summary>
public sealed record GenericReading(long EntityId, DateTimeOffset TsUtc, double? Number, string? Text);

/// <summary>Storage of the generic devices, their entities, readings and hourly rollups (0.11).</summary>
public interface IGenericRepository
{
    Task<IReadOnlyList<GenericDevice>> ListDevicesAsync(bool includeArchived, CancellationToken ct);
    Task<GenericDevice?> GetDeviceAsync(long id, CancellationToken ct);

    /// <summary>Adds the device (or returns the existing one of the same integration and external id).</summary>
    Task<GenericDevice> AddDeviceAsync(string integration, string externalId, string name, string? manufacturer, string? model, string? firmware,
        string? viaDevice, string room, DeviceCategory autoCategory, DateTimeOffset nowUtc, CancellationToken ct);

    /// <summary>What the device says about itself (name only while not set by the user), its automatic category and when it was seen.</summary>
    Task UpdateDeviceInfoAsync(long id, string defaultName, string? manufacturer, string? model, string? firmware, string? viaDevice,
        DeviceCategory autoCategory, DateTimeOffset seenUtc, CancellationToken ct);

    /// <summary>The fields edited on /dispozitive: name (empty = the device's own), room, category override (null = automatic).</summary>
    Task<GenericDevice> UpdateDeviceAsync(long id, string name, string room, DeviceCategory? categoryOverride, CancellationToken ct);

    Task<GenericDevice> SetArchivedAsync(long id, bool archived, CancellationToken ct);

    /// <summary>The entities of one device (null = every device), removed ones included.</summary>
    Task<IReadOnlyList<GenericEntityRow>> ListEntitiesAsync(long? deviceId, CancellationToken ct);

    /// <summary>Inserts or rewrites the description of the device's entities (idempotent: retained configurations come back at every connection).</summary>
    Task<IReadOnlyList<GenericEntityRow>> UpsertEntitiesAsync(long deviceId, string integration, IReadOnlyList<GenericEntityUpsert> entities, CancellationToken ct);

    /// <summary>Marks entities removed by their publisher (rows and history stay).</summary>
    Task MarkEntitiesRemovedAsync(long deviceId, IReadOnlyCollection<string> externalIds, CancellationToken ct);

    /// <summary>Stores readings (a second reading of the same entity and second is ignored) and the entities' last values.</summary>
    Task AddReadingsAsync(IReadOnlyList<GenericReading> readings, CancellationToken ct);

    /// <summary>Readings of one entity in [from, to), oldest first.</summary>
    Task<IReadOnlyList<GenericReading>> ListReadingsAsync(long entityId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>The last reading of an entity before <paramref name="tsUtc"/> (the value carried into the next hour).</summary>
    Task<GenericReading?> LastReadingBeforeAsync(long entityId, DateTimeOffset tsUtc, CancellationToken ct);

    /// <summary>Upsert of hourly rows of one entity (key entity + hour).</summary>
    Task UpsertHourlyAsync(long entityId, IReadOnlyList<GenericHour> hours, CancellationToken ct);

    /// <summary>The hourly rows of the entities in [from, to), by entity and hour.</summary>
    Task<IReadOnlyList<(long EntityId, GenericHour Hour)>> ListHourlyAsync(IReadOnlyCollection<long> entityIds, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>The newest hourly row of every entity that has one.</summary>
    Task<IReadOnlyDictionary<long, DateTimeOffset>> LastHourlyAsync(CancellationToken ct);

    /// <summary>Deletes raw readings older than the cutoff, per entity and in batches on the primary key; returns the rows deleted.</summary>
    Task<int> DeleteReadingsBeforeAsync(DateTimeOffset cutoffUtc, int batchSize, CancellationToken ct);
}
