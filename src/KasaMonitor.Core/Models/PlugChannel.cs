using System.Text.RegularExpressions;

namespace KasaMonitor.Core.Models;

/// <summary>Channel identity and "meter vs. plug" classification shared by the UI, /metrics and the Grafana dashboard.</summary>
public static partial class PlugChannel
{
    /// <summary>"d885ac0b743c" for channel 0, "d885ac0b743c-1" for channel 1 (the <c>mac</c> metric label).</summary>
    public static string MacLabel(string mac, int channel) => channel == 0 ? mac : $"{mac}-{channel}";

    /// <summary>
    /// Model prefixes of whole-installation energy meters (Shelly EM Gen3 "S3EM-…", Shelly Pro EM / Pro 3EM "SPEM-…").
    /// A meter on the main panel already includes the plugs behind it, so totals ("power now", "today", the
    /// "all plugs" history) leave meters out. The Grafana dashboard uses the same regex on the <c>model</c> label:
    /// <c>model!~"S3EM.*|SPEM.*"</c> (deploy/grafana/provisioning/dashboards/kasamonitor.json).
    /// </summary>
    public const string MeterModelRegex = "S3EM.*|SPEM.*";

    [GeneratedRegex("^(?:" + MeterModelRegex + ")$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MeterModel();

    public static bool IsMeter(string? model) => !string.IsNullOrWhiteSpace(model) && MeterModel().IsMatch(PlugModelName.Short(model));

    /// <summary>Plugs whose power/energy count towards totals (everything except energy meters).</summary>
    public static bool CountsInTotals(string? model) => !IsMeter(model);
}
