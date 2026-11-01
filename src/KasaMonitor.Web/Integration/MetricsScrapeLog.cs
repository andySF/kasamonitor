namespace KasaMonitor.Web.Integration;

public sealed record ScrapeEntry(DateTimeOffset AtUtc, string? RemoteIp, string? UserAgent)
{
    /// <summary>Prometheus sends "Prometheus/&lt;version&gt;"; other scrapers (Grafana Agent/Alloy, VictoriaMetrics, ...) differ.</summary>
    public bool IsPrometheus => UserAgent?.StartsWith("Prometheus/", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>In-memory record of the last GET /metrics requests (no persistence; cleared on restart).</summary>
public sealed class MetricsScrapeLog
{
    public const int DefaultCapacity = 20;
    private readonly int _capacity;
    private readonly LinkedList<ScrapeEntry> _entries = new();
    private readonly object _sync = new();

    public MetricsScrapeLog(int capacity = DefaultCapacity) => _capacity = Math.Max(1, capacity);

    public void Record(ScrapeEntry entry)
    {
        lock (_sync)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > _capacity) _entries.RemoveLast();
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ScrapeEntry> Recent()
    {
        lock (_sync) return _entries.ToList();
    }

    public ScrapeEntry? LastPrometheus()
    {
        lock (_sync) return _entries.FirstOrDefault(e => e.IsPrometheus);
    }
}

/// <summary>Records GET /metrics in <see cref="MetricsScrapeLog"/> and passes the request on unchanged.</summary>
public sealed class MetricsScrapeLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly MetricsScrapeLog _log;
    private readonly Func<DateTimeOffset> _clock;

    public MetricsScrapeLogMiddleware(RequestDelegate next, MetricsScrapeLog log, Func<DateTimeOffset>? clock = null)
    {
        _next = next;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method)
            && context.Request.Path.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
        {
            var ua = context.Request.Headers.UserAgent.ToString();
            _log.Record(new ScrapeEntry(
                _clock(),
                context.Connection.RemoteIpAddress?.ToString(),
                ua.Length > 200 ? ua[..200] : (ua.Length == 0 ? null : ua)));
        }
        return _next(context);
    }
}
