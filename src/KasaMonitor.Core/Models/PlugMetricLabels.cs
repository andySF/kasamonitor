namespace KasaMonitor.Core.Models;

/// <summary>
/// Single owner of the per-plug metric labels, shared by the <c>/metrics</c> exporter (<c>kasa_*</c>) and the Prometheus
/// remote write of the live stream (<c>kasa_live_*</c>): alias (MAC label when empty), mac (&lt;mac&gt;-&lt;channel&gt; for
/// channels ≥ 1), model (short), protocol, role (plug | main | sub).
/// </summary>
public static class PlugMetricLabels
{
    public static readonly string[] Names = { "alias", "mac", "model", "protocol", "role" };

    public static string[] Values(string alias, string macLabel, string model, string protocol, PlugRole role) => new[]
    {
        string.IsNullOrEmpty(alias) ? macLabel : alias,
        macLabel,
        PlugModelName.Short(model),
        string.IsNullOrEmpty(protocol) ? PlugProtocols.Legacy : protocol,
        PlugRoles.Label(role),
    };

    public static string[] For(PlugLiveState s) => Values(s.Alias, s.MacLabel, s.Model, s.Protocol, s.Role);

    public static string[] For(Plug p) => Values(p.Alias, p.MacLabel, p.Model, p.Protocol, p.Role);
}
