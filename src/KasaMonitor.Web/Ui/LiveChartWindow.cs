using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Web.Ui;

/// <summary>One point of the live chart on /contor (x = UTC time, shown in the browser's local time).</summary>
public sealed record LivePoint(DateTime TimeUtc, decimal PowerW);

/// <summary>
/// Sliding window behind the live chart of /contor: the last <see cref="Span"/> of 1-second samples, at most
/// <see cref="MaxPoints"/> points. <see cref="Points"/> is one list instance updated in place, so the chart component
/// (rendered once) only pushes new data to ApexCharts (<c>UpdateSeriesAsync</c>) instead of being re-rendered.
/// </summary>
public sealed class LiveChartWindow
{
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(10);
    public const int MaxPoints = 600;

    private readonly SlidingWindow<decimal> _window = new(Span, MaxPoints);

    /// <summary>The chart's data (same instance for the life of the window).</summary>
    public List<LivePoint> Points { get; } = new();

    /// <summary>Seeds the window (samples already received before the page opened), oldest first.</summary>
    public void Seed(IEnumerable<LiveSample> samples)
    {
        foreach (var s in samples.OrderBy(s => s.TsUtc)) _window.Add(s.TsUtc, Round(s.PowerW));
        Rebuild();
    }

    /// <summary>Adds a sample; false when it is out of order (older than the newest point) and was ignored.</summary>
    public bool Add(LiveSample s)
    {
        if (!_window.Add(s.TsUtc, Round(s.PowerW))) return false;
        Rebuild();
        return true;
    }

    private void Rebuild()
    {
        Points.Clear();
        foreach (var (ts, w) in _window.Snapshot()) Points.Add(new LivePoint(ts.UtcDateTime, w));
    }

    private static decimal Round(double w) => Math.Round((decimal)w, 1);
}
