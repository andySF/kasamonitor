using KasaMonitor.Core.Miele;

namespace KasaMonitor.Core.Abstractions;

/// <summary>Storage of the Miele appliances, their state snapshots and their program cycles.</summary>
public interface IMieleRepository
{
    Task<IReadOnlyList<MieleDevice>> ListDevicesAsync(bool includeArchived, CancellationToken ct);

    /// <summary>
    /// Insert or update from the API's ident (type, techType, deviceName) and LastSeenUtc; Alias, PlugId and IsArchived
    /// are kept. A new appliance gets the friendly name of the Miele app (else the type text) as alias.
    /// </summary>
    Task<MieleDevice> UpsertDeviceAsync(string deviceId, MieleIdent? ident, DateTimeOffset seenUtc, CancellationToken ct);

    Task SetAliasAsync(string deviceId, string alias, CancellationToken ct);
    Task SetPlugAsync(string deviceId, Guid? plugId, CancellationToken ct);
    Task SetArchivedAsync(string deviceId, bool archived, CancellationToken ct);

    Task AddSnapshotAsync(MieleStateSnapshot snapshot, CancellationToken ct);
    Task<MieleStateSnapshot?> LatestSnapshotAsync(string deviceId, CancellationToken ct);
    Task<IReadOnlyList<MieleStateSnapshot>> ListSnapshotsAsync(string deviceId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Inserts a cycle and returns it with its id.</summary>
    Task<MieleCycle> AddCycleAsync(MieleCycle cycle, CancellationToken ct);
    Task UpdateCycleAsync(MieleCycle cycle, CancellationToken ct);
    Task<IReadOnlyList<MieleCycle>> ListOpenCyclesAsync(CancellationToken ct);
    /// <summary>The newest cycles of an appliance, newest first.</summary>
    Task<IReadOnlyList<MieleCycle>> ListCyclesAsync(string deviceId, int take, CancellationToken ct);
    Task<int> CountClosedCyclesAsync(string deviceId, CancellationToken ct);
}
