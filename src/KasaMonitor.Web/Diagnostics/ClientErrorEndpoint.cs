namespace KasaMonitor.Web.Diagnostics;

public sealed record ClientErrorReport(string? Kind, string? Message, string? Stack, string? Url, string[]? Recent);

/// <summary>
/// POST /api/client-error: browser-side errors (window error/unhandledrejection, the Blazor error banner) written to the
/// server log at Warning, so they show up in <c>docker logs</c>. Fields are truncated; at most
/// <see cref="MaxPerMinute"/> reports per minute are logged.
/// </summary>
public sealed class ClientErrorEndpoint
{
    public const int MaxField = 2000;
    public const int MaxPerMinute = 30;

    private readonly ILogger _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _sync = new();
    private DateTimeOffset _windowStart;
    private int _inWindow;

    public ClientErrorEndpoint(ILoggerFactory loggers, Func<DateTimeOffset>? clock = null)
    {
        _log = loggers.CreateLogger("KasaMonitor.ClientErrors");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public IResult Handle(ClientErrorReport? report, HttpContext context)
    {
        if (report is null || string.IsNullOrWhiteSpace(report.Message)) return Results.BadRequest();

        lock (_sync)
        {
            var now = _clock();
            if (now - _windowStart >= TimeSpan.FromMinutes(1)) { _windowStart = now; _inWindow = 0; }
            if (++_inWindow > MaxPerMinute) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }

        var recent = report.Recent is { Length: > 0 }
            ? string.Join(" | ", report.Recent.Take(5).Select(r => Cut(r, 500)))
            : "";
        _log.LogWarning("Client error ({Kind}) at {Url} from {Ip} [{UserAgent}]: {Message}{NewLine}{Stack}{NewLine}Recent console errors: {Recent}",
            Cut(report.Kind, 40) ?? "error",
            Cut(report.Url, 300),
            context.Connection.RemoteIpAddress?.ToString(),
            Cut(context.Request.Headers.UserAgent.ToString(), 200),
            Cut(report.Message, MaxField),
            Environment.NewLine,
            Cut(report.Stack, MaxField),
            Environment.NewLine,
            recent);
        return Results.NoContent();
    }

    private static string? Cut(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max] + "…").Replace('\r', ' ');
}
