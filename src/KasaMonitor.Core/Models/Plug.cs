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
    string? UnsupportedReason = null,
    int Channel = 0,
    PlugRole Role = PlugRole.Plug,
    bool StreamEnabled = true
)
{
    /// <summary>MAC for channel 0, "&lt;mac&gt;-&lt;channel&gt;" otherwise: the <c>mac</c> metric label and a unique display key.</summary>
    public string MacLabel => PlugChannel.MacLabel(Mac, Channel);

    public bool IsSupported => UnsupportedReason is null;

    /// <summary>Polled, exported to /metrics and shown in history: not withdrawn (archived) and on a supported protocol.</summary>
    public bool IsPolled => !IsArchived && IsSupported;

    /// <summary>Counts towards totals (only <see cref="PlugRole.Plug"/>; meters already contain the plugs behind them).</summary>
    public bool CountsInTotals => PlugRoles.CountsInTotals(Role);
}
