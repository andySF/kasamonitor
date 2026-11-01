using System.Globalization;
using System.Text;
using KasaMonitor.Core.Nilm;
using KasaMonitor.Workers.Nilm;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// Appliance detection (NILM) on /metrics, refreshed from <see cref="NilmService.Metrics"/> right before every scrape.
/// Only detected appliances that the user named or that have ≥ 10 events in the window are exported
/// (<see cref="NilmService.IsExported"/>); appliances measured by a plug are not (they have their <c>kasa_*</c> series).
/// The <c>appliance</c> label is the normalised name (<see cref="Label"/>). Series of appliances that are no longer
/// exported (renamed, hidden, merged, deleted) are removed from the registry. Counters start again at a restart
/// (events: from the stored count).
/// </summary>
public static class NilmMetrics
{
    public const int MaxLabelLength = 40;

    public static readonly string[] PowerLabels = { "appliance", "category", "source" };
    public static readonly string[] EnergyLabels = { "appliance", "category" };

    public static readonly KasaMetrics.MetricInfo PowerInfo = new("kasa_appliance_power_w", "gauge", "W",
        "Estimated power of the detected appliances running now (NILM, from the main meter; not the plug-measured ones).");
    public static readonly KasaMetrics.MetricInfo EnergyInfo = new("kasa_appliance_energy_wh_total", "counter", "Wh",
        "Estimated energy of the finished runs of each detected appliance (NILM, since start).");
    public static readonly KasaMetrics.MetricInfo EventsInfo = new("kasa_nilm_events_total", "counter", "events",
        "Steady-state steps of the main meter recorded as appliance events.");
    public static readonly KasaMetrics.MetricInfo UnidentifiedInfo = new("kasa_nilm_unidentified_wh_total", "counter", "Wh",
        "House energy not explained by the plugs or the detected appliances (cumulative since start).");

    public static readonly IReadOnlyList<KasaMetrics.MetricInfo> Catalog = new[] { PowerInfo, EnergyInfo, EventsInfo, UnidentifiedInfo };

    private static readonly Gauge Power = Metrics.CreateGauge(PowerInfo.Name, PowerInfo.Help, PowerLabels);
    private static readonly Counter Energy = Metrics.CreateCounter(EnergyInfo.Name, EnergyInfo.Help, EnergyLabels);
    private static readonly Counter Events = Metrics.CreateCounter(EventsInfo.Name, EventsInfo.Help);
    private static readonly Counter Unidentified = Metrics.CreateCounter(UnidentifiedInfo.Name, UnidentifiedInfo.Help);

    private static readonly object Sync = new();
    private static readonly HashSet<string> PowerSeries = new();
    private static readonly Dictionary<string, double> EnergySeries = new();

    /// <summary>
    /// The <c>appliance</c> label: diacritics removed, characters other than letters, digits, space, '_' and '-' dropped,
    /// spaces collapsed, at most <see cref="MaxLabelLength"/> characters ("Necunoscut #12" → "Necunoscut 12").
    /// </summary>
    public static string Label(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-') sb.Append(c);
            else if (c == ' ' && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }
        var label = sb.ToString().Trim();
        if (label.Length > MaxLabelLength) label = label[..MaxLabelLength].TrimEnd();
        return label.Length == 0 ? "appliance" : label;
    }

    public static void Update(NilmMetricSnapshot snapshot)
    {
        lock (Sync)
        {
            var power = new Dictionary<string, (string[] Labels, double W)>();
            var energy = new Dictionary<string, (string[] Labels, double Wh)>();
            foreach (var a in snapshot.Appliances)
            {
                var name = Label(a.Name);
                var e = new[] { name, a.Category };
                var ek = string.Join('\u0001', e);
                energy[ek] = (e, (energy.TryGetValue(ek, out var old) ? old.Wh : 0) + a.EnergyWh);
                if (a.PowerW is not { } w) continue;
                var p = new[] { name, a.Category, NilmSources.Code(a.Source) };
                var pk = string.Join('\u0001', p);
                power[pk] = (p, (power.TryGetValue(pk, out var op) ? op.W : 0) + w);
            }

            foreach (var (_, (labels, w)) in power) Power.WithLabels(labels).Set(w);
            foreach (var gone in PowerSeries.Where(k => !power.ContainsKey(k)).ToList()) Power.RemoveLabelled(gone.Split('\u0001'));
            PowerSeries.Clear();
            PowerSeries.UnionWith(power.Keys);

            foreach (var (key, (labels, wh)) in energy)
            {
                // Two signatures with the same label are summed; when one leaves, the sum drops: recreate the series
                // (a counter reset, which increase() handles) instead of leaving it stuck until it catches up.
                if (EnergySeries.TryGetValue(key, out var last) && wh < last) Energy.RemoveLabelled(labels);
                Energy.WithLabels(labels).IncTo(wh);
            }
            foreach (var gone in EnergySeries.Keys.Where(k => !energy.ContainsKey(k)).ToList()) Energy.RemoveLabelled(gone.Split('\u0001'));
            EnergySeries.Clear();
            foreach (var (key, (_, wh)) in energy) EnergySeries[key] = wh;

            Events.IncTo(snapshot.Events);
            Unidentified.IncTo(snapshot.UnidentifiedWh);
        }
    }

    public static void Register(NilmService service) =>
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => Update(service.Metrics));
}
