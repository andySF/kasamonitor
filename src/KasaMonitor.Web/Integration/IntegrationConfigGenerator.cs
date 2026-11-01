using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KasaMonitor.Web.Integration;

/// <summary>
/// Builds the Prometheus/Grafana configuration shown and downloaded on /integration from the embedded <c>deploy/</c>
/// files: the scrape target (KasaMonitor host:port) and the Grafana data source uid are filled in; nothing else changes.
/// </summary>
public static partial class IntegrationConfigGenerator
{
    /// <summary>The target written in deploy/prometheus/prometheus.yml (Prometheus in Docker, KasaMonitor on the host network).</summary>
    public const string RepositoryTarget = "host.docker.internal:8910";
    public const string RepositoryDataSourceUid = "prometheus";

    [GeneratedRegex(@"^(\[[0-9A-Fa-f:]+\]|[A-Za-z0-9](?:[A-Za-z0-9.\-]{0,251}[A-Za-z0-9])?)(:\d{1,5})?$")]
    private static partial Regex TargetRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,40}$")]
    private static partial Regex UidRegex();

    /// <summary>
    /// Validates "host[:port]" (port defaults to 8910). Only host names, IPv4 and bracketed IPv6 are accepted, so the
    /// value can be pasted into YAML safely.
    /// </summary>
    public static string NormalizeTarget(string? input, int defaultPort = 8910)
    {
        var t = (input ?? "").Trim();
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) t = t[7..];
        else if (t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) t = t[8..];
        t = t.TrimEnd('/');
        var m = TargetRegex().Match(t);
        if (!m.Success) throw new FormatException($"Adresă invalidă: „{input}”. Format: host:port, ex. 192.168.1.10:8910.");
        if (m.Groups[2].Success)
        {
            var port = int.Parse(m.Groups[2].Value[1..]);
            if (port is < 1 or > 65535) throw new FormatException($"Port invalid: {port}.");
            return t;
        }
        return $"{t}:{defaultPort}";
    }

    public static string NormalizeDataSourceUid(string? uid)
    {
        var u = string.IsNullOrWhiteSpace(uid) ? RepositoryDataSourceUid : uid.Trim();
        if (!UidRegex().IsMatch(u)) throw new FormatException($"UID invalid: „{uid}” (litere, cifre, _ și -).");
        return u;
    }

    /// <summary>Snippet to paste under an existing <c>scrape_configs:</c>.</summary>
    public static string ScrapeConfigSnippet(string target)
    {
        target = NormalizeTarget(target);
        return $"""
                scrape_configs:
                  - job_name: kasa
                    metrics_path: /metrics
                    scrape_interval: 15s
                    static_configs:
                      - targets: ["{target}"]
                        labels:
                          instance: kasamonitor

                """;
    }

    /// <summary>deploy/prometheus/prometheus.yml with the scrape target replaced.</summary>
    public static string PrometheusYml(string target)
    {
        target = NormalizeTarget(target);
        var yml = DeployFiles.Read(DeployFiles.PrometheusConfig);
        var marker = $"\"{RepositoryTarget}\"";
        if (!yml.Contains(marker, StringComparison.Ordinal))
            throw new InvalidOperationException($"{DeployFiles.PrometheusConfig} no longer contains {marker}.");
        return yml.Replace(marker, $"\"{target}\"", StringComparison.Ordinal);
    }

    /// <summary>The dashboard JSON with every Prometheus data source reference pointing at <paramref name="dataSourceUid"/>.</summary>
    public static string DashboardJson(string? dataSourceUid = null)
    {
        var uid = NormalizeDataSourceUid(dataSourceUid);
        var json = DeployFiles.Read(DeployFiles.Dashboard);
        if (uid == RepositoryDataSourceUid) return json;
        var root = JsonNode.Parse(json)!;
        ReplaceDataSourceUid(root, uid);
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The alert rules with <c>datasourceUid: prometheus</c> replaced.</summary>
    public static string AlertRulesYml(string? dataSourceUid = null)
    {
        var uid = NormalizeDataSourceUid(dataSourceUid);
        var yml = DeployFiles.Read(DeployFiles.AlertRules);
        return uid == RepositoryDataSourceUid
            ? yml
            : yml.Replace($"datasourceUid: {RepositoryDataSourceUid}", $"datasourceUid: {uid}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Zip with the monitoring stack laid out as in <c>deploy/</c>: docker-compose.monitoring.yml, prometheus/prometheus.yml
    /// (target filled in) and grafana/provisioning/** (unchanged: the stack provisions its own data source uid).
    /// </summary>
    public static byte[] MonitoringBundle(string target)
    {
        target = NormalizeTarget(target);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, DeployFiles.MonitoringCompose, DeployFiles.Read(DeployFiles.MonitoringCompose));
            Add(zip, DeployFiles.PrometheusConfig, PrometheusYml(target));
            foreach (var path in DeployFiles.All.Where(p => p.StartsWith("grafana/provisioning/", StringComparison.Ordinal)))
                Add(zip, path, DeployFiles.Read(path));
            Add(zip, "README.txt", BundleReadme(target));
        }
        return ms.ToArray();
    }

    private static string BundleReadme(string target) => $"""
        KasaMonitor monitoring stack (Prometheus + Grafana)

        Prometheus scrapes KasaMonitor at http://{target}/metrics (prometheus/prometheus.yml, job "kasa").

          docker compose -f docker-compose.monitoring.yml up -d

        Grafana:    http://<this host>:3000  (admin / GRAFANA_ADMIN_PASSWORD, default "admin")
        Prometheus: http://<this host>:9090/targets  (job "kasa" must be UP)

        The KasaMonitor dashboard, the Prometheus data source (uid "prometheus") and two alert rules
        (plug offline 15 min, no data 10 min) are provisioned from grafana/provisioning/.
        If KasaMonitor runs on this same host with network_mode: host and the IP above is not reachable
        from containers, use "host.docker.internal:8910" as target (extra_hosts is already set).

        """;

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static void ReplaceDataSourceUid(JsonNode? node, string uid)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && obj["type"]!.GetValue<string>() == "prometheus" && obj.ContainsKey("uid"))
                    obj["uid"] = uid;
                foreach (var kv in obj.ToList()) ReplaceDataSourceUid(kv.Value, uid);
                break;
            case JsonArray arr:
                foreach (var item in arr) ReplaceDataSourceUid(item, uid);
                break;
        }
    }
}
