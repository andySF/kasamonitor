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

    /// <summary>
    /// Sets the role of a plug. There is at most one <see cref="PlugRole.MainMeter"/>: setting it demotes the previous
    /// main meter to <see cref="PlugRole.SubMeter"/> (same transaction).
    /// </summary>
    Task SetRoleAsync(Guid plugId, PlugRole role, CancellationToken ct);

    /// <summary>Turns the live stream (1–10 s) of one plug/channel on or off.</summary>
    Task SetStreamEnabledAsync(Guid plugId, bool enabled, CancellationToken ct);

    /// <summary>Whether the plug labels appliance events and trains the classifier (see <see cref="KasaMonitor.Core.Nilm.PlugNilmState"/>).</summary>
    Task SetNilmStateAsync(Guid plugId, KasaMonitor.Core.Nilm.PlugNilmState state, CancellationToken ct);

    /// <summary>
    /// „Un singur aparat pe priză”: whether the plug measures one appliance, its name (cleaned; empty = the alias) and
    /// category (normalised to <see cref="KasaMonitor.Core.Nilm.ApplianceCategories"/>). The name and category are kept when
    /// the flag is cleared. False when the plug does not exist.
    /// </summary>
    Task<bool> SetApplianceAsync(Guid plugId, bool single, string? name, string? category, CancellationToken ct);
}
