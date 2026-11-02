using KasaMonitor.Workers;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// The two series of the home page on /metrics, refreshed from <see cref="HomeService.Metrics"/> (computed once per minute)
/// right before every scrape: the standby floor of the rest of the house and the bill estimate of the current month. A value
/// that is not known (no main meter, no night yet, less than 6 h of the month) has no series.
/// </summary>
public static class HomeMetrics
{
    public static readonly string[] BillLabels = { "kind" };

    public static readonly KasaMetrics.MetricInfo StandbyInfo = new("kasa_house_standby_w", "gauge", "W",
        "Standby / always-on floor of the house: median of (main meter - plugs) between 01:00 and 05:00 over the last 7 nights; 0 when it is 5 W or less (not measurable).");
    public static readonly KasaMetrics.MetricInfo BillInfo = new("kasa_bill_estimate_ron", "gauge", "RON",
        "Bill estimate of the current month (kind = electricity|gas|total): used so far + mean per elapsed day x days left.");

    public static readonly IReadOnlyList<KasaMetrics.MetricInfo> Catalog = new[] { StandbyInfo, BillInfo };

    private static readonly Gauge Standby = Metrics.CreateGauge(StandbyInfo.Name, StandbyInfo.Help,
        new GaugeConfiguration { SuppressInitialValue = true });
    private static readonly Gauge Bill = Metrics.CreateGauge(BillInfo.Name, BillInfo.Help, BillLabels);

    private static readonly object Sync = new();

    public static void Update(HomeMetricSnapshot snapshot)
    {
        lock (Sync)
        {
            if (snapshot.StandbyW is { } w) Standby.Set(w);
            else Standby.Unpublish();
            Set("electricity", snapshot.Bill?.ElectricityRon);
            Set("gas", snapshot.Bill?.GasRon);
            Set("total", snapshot.Bill?.TotalRon);
        }
    }

    private static void Set(string kind, decimal? value)
    {
        if (value is { } v) Bill.WithLabels(kind).Set((double)v);
        else Bill.RemoveLabelled(kind);
    }

    public static void Register(HomeService service) =>
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => Update(service.Metrics));
}
