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
        var existing = await _db.Plugs.FirstOrDefaultAsync(p => p.Mac == discovered.Info.Mac, ct);
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
                UnsupportedReason = discovered.UnsupportedReason
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

    public async Task<Plug?> GetByMacAsync(string mac, CancellationToken ct)
    {
        var e = await _db.Plugs.AsNoTracking().FirstOrDefaultAsync(p => p.Mac == mac, ct);
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
        UnsupportedReason: e.UnsupportedReason);
}
