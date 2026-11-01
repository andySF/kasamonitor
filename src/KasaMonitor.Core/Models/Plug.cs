using System.Net;

namespace KasaMonitor.Core.Models;

public sealed record Plug(
    Guid Id,
    string Mac,
    IPAddress IpAddress,
    string Alias,
    bool AliasIsCustom,
    string Model,
    string HwVer,
    string SwVer,
    string OemId,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    bool IsArchived,
    string? Notes,
    string Protocol = PlugProtocols.Legacy,
    string? UnsupportedReason = null
)
{
    public bool IsSupported => UnsupportedReason is null;
}
