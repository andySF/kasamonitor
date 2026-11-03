using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace KasaMonitor.Data.Repositories;

public sealed class PlugRepository : IPlugRepository
{
    private readonly KasaDbContext _db;
    public PlugRepository(KasaDbContext db) => _db = db;

    public async Task<Guid> UpsertFromDiscoveryAsync(DiscoveredPlug discovered, DateTimeOffset utcNow, CancellationToken ct)
    {
        var existing = await _db.Plugs.FirstOrDefaultAsync(p => p.Mac == discovered.Info.Mac && p.Channel == discovered.Channel, ct);
        if (existing is null)
        {
            var entity = new PlugEntity
            {
                Id = Guid.NewGuid(),
                Mac = discovered.Info.Mac,
                IpAddress = discovered.IpAddress.ToString(),
                Alias = discovered.Info.Alias,
                AliasIsCustom = false,
                Model = discovered.Info.Model,
                HwVer = discovered.Info.HwVer,
                SwVer = discovered.Info.SwVer,
                OemId = discovered.Info.OemId,
                FirstSeenUtc = utcNow,
                LastSeenUtc = utcNow,
                IsArchived = false,
                Protocol = discovered.Protocol,
                UnsupportedReason = discovered.UnsupportedReason,
                Channel = discovered.Channel,
                Role = (int)discovered.Role,
                StreamEnabled = PlugRoles.DefaultStreamEnabled(discovered.Role)
            };
            _db.Plugs.Add(entity);
            await _db.SaveChangesAsync(ct);
            return entity.Id;
        }

        existing.IpAddress = discovered.IpAddress.ToString();
        existing.LastSeenUtc = utcNow;
        if (existing.UnsupportedReason is null && !discovered.IsSupported)
        {
            // An unsupported answer (e.g. a stray UDP 20002 reply) never downgrades a plug that is being polled.
            await _db.SaveChangesAsync(ct);
            return existing.Id;
        }

        existing.Protocol = discovered.Protocol;
        existing.UnsupportedReason = discovered.UnsupportedReason;
        existing.Model = discovered.Info.Model;
        existing.HwVer = discovered.Info.HwVer;
        existing.SwVer = discovered.Info.SwVer;
        existing.OemId = discovered.Info.OemId;
        if (!existing.AliasIsCustom)
            existing.Alias = discovered.Info.Alias;
        await _db.SaveChangesAsync(ct);
        return existing.Id;
    }

    public async Task UpdateLastSeenAsync(Guid plugId, DateTimeOffset utcNow, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.LastSeenUtc = utcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Plug>> ListAllAsync(CancellationToken ct) =>
        await _db.Plugs.AsNoTracking().Select(p => Map(p)).ToListAsync(ct);

    public Task<Plug?> GetByMacAsync(string mac, CancellationToken ct) => GetByMacAsync(mac, 0, ct);

    public async Task<Plug?> GetByMacAsync(string mac, int channel, CancellationToken ct)
    {
        var e = await _db.Plugs.AsNoTracking().FirstOrDefaultAsync(p => p.Mac == mac && p.Channel == channel, ct);
        return e is null ? null : Map(e);
    }

    public async Task SetAliasAsync(Guid plugId, string alias, bool isCustom, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.Alias = alias;
        existing.AliasIsCustom = isCustom;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetArchivedAsync(Guid plugId, bool archived, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.IsArchived = archived;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetRoleAsync(Guid plugId, PlugRole role, CancellationToken ct)
    {
        // The explicit transaction runs inside the execution strategy (retried as a whole on a transient error); a retry
        // starts from a clean change tracker, otherwise the demotion of the previous main meter (rolled back, but still
        // tracked as saved) would not be written again and the unique index would refuse the new one.
        await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
            if (existing is null) return;
            if (role == PlugRole.MainMeter)
            {
                // At most one main meter (also a partial unique index): demote the previous one first, in its own statement.
                var previous = await _db.Plugs.Where(p => p.Role == (int)PlugRole.MainMeter && p.Id != plugId).ToListAsync(ct);
                foreach (var p in previous)
                {
                    p.Role = (int)PlugRole.SubMeter;
                    p.StreamEnabled = PlugRoles.DefaultStreamEnabled(PlugRole.SubMeter);
                }
                await _db.SaveChangesAsync(ct);
            }
            if (existing.Role != (int)role)
            {
                existing.Role = (int)role;
                existing.StreamEnabled = PlugRoles.DefaultStreamEnabled(role);   // a new role starts with its default stream setting
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task SetStreamEnabledAsync(Guid plugId, bool enabled, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.StreamEnabled = enabled;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetNilmStateAsync(Guid plugId, KasaMonitor.Core.Nilm.PlugNilmState state, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.NilmIgnored = (int)state;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetApplianceAsync(Guid plugId, bool single, string? name, string? category, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return false;
        var clean = KasaMonitor.Core.Nilm.SignatureNaming.CleanName(name);
        existing.SingleAppliance = single;
        existing.ApplianceName = clean.Length > 0 ? clean : null;
        existing.ApplianceCategory = KasaMonitor.Core.Nilm.ApplianceCategories.Normalize(category);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static Plug Map(PlugEntity e) => new(
        Id: e.Id,
        Mac: e.Mac,
        IpAddress: IPAddress.TryParse(e.IpAddress, out var ip) ? ip : IPAddress.None,
        Alias: e.Alias,
        AliasIsCustom: e.AliasIsCustom,
        Model: e.Model,
        HwVer: e.HwVer,
        SwVer: e.SwVer,
        OemId: e.OemId,
        FirstSeenUtc: e.FirstSeenUtc,
        LastSeenUtc: e.LastSeenUtc,
        IsArchived: e.IsArchived,
        Notes: e.Notes,
        Protocol: e.Protocol,
        UnsupportedReason: e.UnsupportedReason,
        Channel: e.Channel,
        Role: PlugRoles.FromInt(e.Role),
        StreamEnabled: e.StreamEnabled,
        NilmState: KasaMonitor.Core.Nilm.PlugNilmStates.FromInt(e.NilmIgnored),
        SingleAppliance: e.SingleAppliance,
        ApplianceName: e.ApplianceName,
        ApplianceCategory: e.ApplianceCategory);
}
