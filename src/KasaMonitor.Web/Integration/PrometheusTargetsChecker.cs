using System.Text.Json;

namespace KasaMonitor.Web.Integration;

public sealed record PrometheusTargetStatus(string Job, string Health, string ScrapeUrl, string? LastError, DateTimeOffset? LastScrape);

/// <param name="Targets">Active targets of job "kasa" (empty when the job is not configured).</param>
public sealed record PrometheusCheckResult(bool Reachable, string Message, IReadOnlyList<PrometheusTargetStatus> Targets)
{
    public bool KasaUp => Targets.Any(t => t.Health == "up");
}

/// <summary>
/// "Verifică" on /integration: GET http://&lt;prometheus&gt;/api/v1/targets from the KasaMonitor server (5 s timeout)
/// and report the health of job "kasa". Only that fixed API path is requested and only parsed fields are shown.
/// </summary>
public sealed class PrometheusTargetsChecker
{
    public const string JobName = "kasa";
    private readonly HttpClient _http;

    public PrometheusTargetsChecker(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>"localhost:9090", "http://prom:9090" or "https://prom.example.com" -> base URI.</summary>
    public static Uri BaseUri(string address)
    {
        var a = (address ?? "").Trim().TrimEnd('/');
        if (a.Length == 0) a = "localhost:9090";
        if (!a.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            a = "http://" + a;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query))
            throw new FormatException($"Adresă Prometheus invalidă: „{address}”. Format: host:port, ex. localhost:9090.");
        return uri;
    }

    public async Task<PrometheusCheckResult> CheckAsync(string address, CancellationToken ct)
    {
        Uri baseUri;
        try { baseUri = BaseUri(address); }
        catch (FormatException ex) { return new PrometheusCheckResult(false, ex.Message, Array.Empty<PrometheusTargetStatus>()); }

        var url = new Uri(baseUri, "/api/v1/targets?state=active");
        string body;
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return new PrometheusCheckResult(false, $"{url}: HTTP {(int)resp.StatusCode}.", Array.Empty<PrometheusTargetStatus>());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var reason = ex is TaskCanceledException ? "timeout 5 s" : ex.Message;
            return new PrometheusCheckResult(false, $"Prometheus nu răspunde la {url} ({reason}).", Array.Empty<PrometheusTargetStatus>());
        }

        try
        {
            var targets = ParseKasaTargets(body);
            var msg = targets.Count == 0
                ? $"Prometheus răspunde, dar nu are job-ul „{JobName}”: adaugă snippet-ul de mai sus în prometheus.yml și reîncarcă Prometheus."
                : targets.Any(t => t.Health == "up")
                    ? $"Job-ul „{JobName}” e UP."
                    : $"Job-ul „{JobName}” există, dar nu e UP: {targets[0].LastError ?? targets[0].Health}.";
            return new PrometheusCheckResult(true, msg, targets);
        }
        catch (JsonException)
        {
            return new PrometheusCheckResult(false, $"{url} nu a întors JSON-ul API-ului Prometheus.", Array.Empty<PrometheusTargetStatus>());
        }
    }

    public static IReadOnlyList<PrometheusTargetStatus> ParseKasaTargets(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<PrometheusTargetStatus>();
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("activeTargets", out var active) || active.ValueKind != JsonValueKind.Array)
            throw new JsonException("No data.activeTargets.");
        foreach (var t in active.EnumerateArray())
        {
            var job = t.TryGetProperty("labels", out var labels) && labels.TryGetProperty("job", out var j) ? j.GetString() : null;
            if (job != JobName) continue;
            var lastError = t.TryGetProperty("lastError", out var le) ? le.GetString() : null;
            DateTimeOffset? last = t.TryGetProperty("lastScrape", out var ls) && DateTimeOffset.TryParse(ls.GetString(), out var parsed)
                ? parsed : null;
            list.Add(new PrometheusTargetStatus(
                job,
                t.TryGetProperty("health", out var h) ? h.GetString() ?? "" : "",
                t.TryGetProperty("scrapeUrl", out var su) ? su.GetString() ?? "" : "",
                string.IsNullOrEmpty(lastError) ? null : lastError,
                last));
        }
        return list;
    }
}
