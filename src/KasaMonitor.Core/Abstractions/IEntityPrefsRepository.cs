using KasaMonitor.Core.Devices.Entities;

namespace KasaMonitor.Core.Abstractions;

/// <summary>Storage of the per-entity preferences (table <c>EntityPrefs</c>, 0.11), keyed by the stable entity key.</summary>
public interface IEntityPrefsRepository
{
    /// <summary>The preferences of the entities whose key starts with <paramref name="keyPrefix"/> (one device: „plug:{id}/”).</summary>
    Task<IReadOnlyDictionary<string, EntityPrefs>> ListAsync(string keyPrefix, CancellationToken ct);

    /// <summary>Every stored preference (the metrics and the history read them once per refresh).</summary>
    Task<IReadOnlyDictionary<string, EntityPrefs>> ListAllAsync(CancellationToken ct);

    /// <summary>Stores the preferences of one entity; an empty one (<see cref="EntityPrefs.IsEmpty"/>) deletes the row.</summary>
    Task SaveAsync(EntityPrefs prefs, CancellationToken ct);
}
