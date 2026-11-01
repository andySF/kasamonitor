using KasaMonitor.Workers;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// KasaMonitor's own remote-write metrics on /metrics, refreshed from <see cref="PrometheusRemoteWriter.Status"/> right
/// before every scrape: samples sent, failed sends, dropped samples, last success (unix seconds).
/// </summary>
public static class RemoteWriteMetrics
{
    public static readonly KasaMetrics.MetricInfo SamplesInfo = new("kasa_remote_write_samples_total", "counter", "samples",
        "Live samples accepted by the Prometheus remote write endpoint.");
    public static readonly KasaMetrics.MetricInfo FailuresInfo = new("kasa_remote_write_failures_total", "counter", "requests",
        "Failed remote write requests (retried, or dropped on HTTP 4xx).");
    public static readonly KasaMetrics.MetricInfo DroppedInfo = new("kasa_remote_write_dropped_samples_total", "counter", "samples",
        "Live samples dropped by the remote write (older than 5 minutes in the queue, or rejected).");
    public static readonly KasaMetrics.MetricInfo LastSuccessInfo = new("kasa_remote_write_last_success_timestamp_seconds", "gauge", "s",
        "Unix time of the last successful remote write (0 = none yet).");

    public static readonly IReadOnlyList<KasaMetrics.MetricInfo> Catalog = new[] { SamplesInfo, FailuresInfo, DroppedInfo, LastSuccessInfo };

    private static readonly Counter Samples = Metrics.CreateCounter(SamplesInfo.Name, SamplesInfo.Help);
    private static readonly Counter Failures = Metrics.CreateCounter(FailuresInfo.Name, FailuresInfo.Help);
    private static readonly Counter Dropped = Metrics.CreateCounter(DroppedInfo.Name, DroppedInfo.Help);
    private static readonly Gauge LastSuccess = Metrics.CreateGauge(LastSuccessInfo.Name, LastSuccessInfo.Help);

    public static void Update(RemoteWriteStatus s)
    {
        Samples.IncTo(s.SentSamples);
        Failures.IncTo(s.Failures);
        Dropped.IncTo(s.DroppedSamples);
        LastSuccess.Set(s.LastSuccessUtc?.ToUnixTimeSeconds() ?? 0);
    }

    public static void Register(PrometheusRemoteWriter writer) =>
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => Update(writer.Status));
}
