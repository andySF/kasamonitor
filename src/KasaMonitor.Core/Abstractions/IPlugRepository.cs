using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface IPlugRepository
{
    Task<Guid> UpsertFromDiscoveryAsync(DiscoveredPlug discovered, DateTimeOffset utcNow, CancellationToken ct);
    Task UpdateLastSeenAsync(Guid plugId, DateTimeOffset utcNow, CancellationToken ct);
    Task<IReadOnlyList<Plug>> ListAllAsync(CancellationToken ct);
    /// <summary>The plug with this identity (MAC + channel).</summary>
    Task<Plug?> GetByMacAsync(string mac, int channel, CancellationToken ct);

    /// <summary>Channel 0 of the device (single-channel plugs).</summary>
    Task<Plug?> GetByMacAsync(string mac, CancellationToken ct);
    Task SetAliasAsync(Guid plugId, string alias, bool isCustom, CancellationToken ct);
    Task SetArchivedAsync(Guid plugId, bool archived, CancellationToken ct);
}
