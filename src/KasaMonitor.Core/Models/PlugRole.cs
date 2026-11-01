namespace KasaMonitor.Core.Models;

/// <summary>
/// What a measured channel is in the installation. Stored as an integer in <c>Plugs.Role</c> and exported as the
/// <c>role</c> metric label (<see cref="PlugRoles.Label"/>).
/// </summary>
public enum PlugRole
{
    /// <summary>A plug (Kasa, Tapo, Shelly switch/pm1): its power and energy count towards the totals.</summary>
    Plug = 0,

    /// <summary>The whole-house meter (at most one per installation): already contains every plug behind it.</summary>
    MainMeter = 1,

    /// <summary>Any other meter channel (a circuit, a second phase, an unused clamp): never counted in the totals.</summary>
    SubMeter = 2,
}

/// <summary>Single owner of the role rules: labels, totals, defaults and the Romanian texts shown in the UI.</summary>
public static class PlugRoles
{
    public const string PlugLabel = "plug";
    public const string MainLabel = "main";
    public const string SubLabel = "sub";

    /// <summary>Value of the <c>role</c> metric label: <c>plug</c> | <c>main</c> | <c>sub</c>.</summary>
    public static string Label(PlugRole role) => role switch
    {
        PlugRole.MainMeter => MainLabel,
        PlugRole.SubMeter => SubLabel,
        _ => PlugLabel,
    };

    /// <summary>
    /// Only plugs count towards totals ("power now", "today", the "all plugs" history, the Grafana totals with
    /// <c>role="plug"</c>): a meter already contains the consumption of the plugs behind it.
    /// </summary>
    public static bool CountsInTotals(PlugRole role) => role == PlugRole.Plug;

    public static bool IsMeter(PlugRole role) => role != PlugRole.Plug;

    /// <summary>Unknown stored values (a newer version's role after a downgrade) read as <see cref="PlugRole.Plug"/>.</summary>
    public static PlugRole FromInt(int value) => Enum.IsDefined(typeof(PlugRole), value) ? (PlugRole)value : PlugRole.Plug;

    /// <summary>Short Romanian name (badge).</summary>
    public static string Name(PlugRole role) => role switch
    {
        PlugRole.MainMeter => "contor general",
        PlugRole.SubMeter => "sub-contor",
        _ => "priză",
    };

    /// <summary>Bootstrap icon of the role selector: plug / house / meter.</summary>
    public static string Icon(PlugRole role) => role switch
    {
        PlugRole.MainMeter => "bi-house-door",
        PlugRole.SubMeter => "bi-speedometer",
        _ => "bi-plug",
    };

    /// <summary>One-line Romanian explanation (tooltip and the roles help on /plugs).</summary>
    public static string Description(PlugRole role) => role switch
    {
        PlugRole.MainMeter =>
            "Contorul general al casei (unul singur): apare pe pagina Contor și în cardul „Casă”; nu intră în totalurile prizelor, iar „restul casei” = contor − prize.",
        PlugRole.SubMeter =>
            "Un alt canal de contor (un circuit, o fază, o clemă nefolosită): are graficele lui, dar nu intră în totaluri.",
        _ => "O priză: puterea și energia ei intră în totalurile din Dashboard, History „Toate” și Grafana (role=\"plug\").",
    };

    /// <summary>Live stream on by default for the main meter and the plugs, off for sub-meters (e.g. an unused clamp).</summary>
    public static bool DefaultStreamEnabled(PlugRole role) => role != PlugRole.SubMeter;

    public static readonly IReadOnlyList<PlugRole> All = new[] { PlugRole.Plug, PlugRole.MainMeter, PlugRole.SubMeter };
}
