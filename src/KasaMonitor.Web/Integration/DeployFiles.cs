using System.Reflection;

namespace KasaMonitor.Web.Integration;

/// <summary>
/// The monitoring files from the repository's <c>deploy/</c> directory (compose, Prometheus config, Grafana provisioning),
/// embedded in KasaMonitor.Web.dll at build time (see KasaMonitor.Web.csproj). The repository files stay the single
/// source; the /integration page serves them, optionally with the real KasaMonitor address filled in.
/// </summary>
public static class DeployFiles
{
    public const string MonitoringCompose = "docker-compose.monitoring.yml";
    public const string PrometheusConfig = "prometheus/prometheus.yml";
    public const string Dashboard = "grafana/provisioning/dashboards/kasamonitor.json";
    public const string AlertRules = "grafana/provisioning/alerting/rules.yml";
    public const string DataSource = "grafana/provisioning/datasources/prometheus.yml";
    public const string DashboardProvider = "grafana/provisioning/dashboards/dashboards.yml";

    private const string Prefix = "deploy/";
    private static readonly Assembly Asm = typeof(DeployFiles).Assembly;

    /// <summary>Paths relative to <c>deploy/</c>, with forward slashes.</summary>
    public static IReadOnlyList<string> All { get; } = Asm.GetManifestResourceNames()
        .Select(n => n.Replace('\\', '/'))
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(n => n[Prefix.Length..])
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToList();

    public static string Read(string relativePath)
    {
        var name = Asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Replace('\\', '/') == Prefix + relativePath)
            ?? throw new FileNotFoundException($"Embedded deploy file not found: {relativePath}");
        using var stream = Asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
