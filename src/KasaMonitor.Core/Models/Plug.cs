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
    bool StreamEnabled = true,
    KasaMonitor.Core.Nilm.PlugNilmState NilmState = KasaMonitor.Core.Nilm.PlugNilmState.Auto,
    bool SingleAppliance = false,
    string? ApplianceName = null,
    string? ApplianceCategory = null
)
{
    /// <summary>MAC for channel 0, "&lt;mac&gt;-&lt;channel&gt;" otherwise: the <c>mac</c> metric label and a unique display key.</summary>
    public string MacLabel => PlugChannel.MacLabel(Mac, Channel);

    public bool IsSupported => UnsupportedReason is null;

    /// <summary>Polled, exported to /metrics and shown in history: not withdrawn (archived) and on a supported protocol.</summary>
    public bool IsPolled => !IsArchived && IsSupported;

    /// <summary>Counts towards totals (only <see cref="PlugRole.Plug"/>; meters already contain the plugs behind them).</summary>
    public bool CountsInTotals => PlugRoles.CountsInTotals(Role);

    /// <summary>Its changes label appliance events and train the classifier (a polled plug, not excluded from learning).</summary>
    public bool TeachesNilm => IsPolled && Role == PlugRole.Plug && !KasaMonitor.Core.Nilm.PlugNilmStates.IsIgnored(NilmState);

    /// <summary>
    /// „Un singur aparat pe priză” (<see cref="SingleAppliance"/>): the name of that appliance — <see cref="ApplianceName"/>,
    /// or the alias when none was given; null for a plug with several (or unknown) consumers.
    /// </summary>
    public string? Appliance => PlugAppliance.Name(SingleAppliance, ApplianceName, Alias);

    /// <summary>The name shown wherever the plug stands for an appliance (home page, Sankey): <see cref="Appliance"/>, else the alias.</summary>
    public string DisplayName => Appliance ?? Alias;

    /// <summary>
    /// Its labels never train the classifier: excluded from learning (<see cref="KasaMonitor.Core.Nilm.PlugNilmState"/>), or a
    /// single-appliance plug, whose events the plug already explains completely (they still get the plug's label and signature).
    /// </summary>
    public bool ExcludedFromTraining => KasaMonitor.Core.Nilm.PlugNilmStates.IsIgnored(NilmState) || SingleAppliance;
}

/// <summary>Single owner of the „un singur aparat pe priză” rules (plug model, live state, API, UI).</summary>
public static class PlugAppliance
{
    /// <summary>The appliance name in effect: the given name (cleaned), else the alias; null when the plug is not a single-appliance one.</summary>
    public static string? Name(bool single, string? applianceName, string alias)
    {
        if (!single) return null;
        var name = KasaMonitor.Core.Nilm.SignatureNaming.CleanName(applianceName);
        return name.Length > 0 ? name : alias;
    }
}
