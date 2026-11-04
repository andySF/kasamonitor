using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Web.Ui;

/// <summary>One point of the live chart on /contor (x = UTC time, shown in the browser's local time; y = null leaves a gap).</summary>
public sealed record LivePoint(DateTime TimeUtc, decimal? PowerW);

/// <summary>One line of the live chart on /contor.</summary>
public sealed record LiveChartSeries(Guid Id, string Name, string Color);

/// <summary>The colours of the live chart (Okabe-Ito). A plug's colour follows its position in the list of candidates, so it stays put when another plug is toggled.</summary>
public static class LiveChartColors
{
    public const string Main = "#0072B2";

    /// <summary>
    /// Optimized with category-colors together with <see cref="Main"/> (CIEDE2000, normal and colour-blind vision), then
    /// ordered so that the first ones are the furthest apart: the colours of a few lines differ the most.
    /// </summary>
    public static readonly IReadOnlyList<string> Overlays = new[]
    {
        "#DF0001", "#86BA00", "#A4AAED", "#FC789C", "#71790B", "#00A988", "#B5485D", "#C9AD73", "#9A69FF",
    };

    /// <summary>The first colour no active line uses; with more lines than colours they repeat, evenly.</summary>
    public static string NextFree(IReadOnlyCollection<string> used) =>
        Overlays.FirstOrDefault(c => !used.Contains(c)) ?? Overlays[used.Count % Overlays.Count];
}

/// <summary>
/// Sliding window of one line of the live chart: the last <see cref="Span"/> of points, at most
/// <see cref="MaxPoints"/>. <see cref="Points"/> is one list instance updated in place, so the chart component
/// (rendered once) only pushes new data to ApexCharts (<c>UpdateSeriesAsync</c>) instead of being re-rendered.
/// </summary>
public sealed class LiveChartWindow
{
    public static readonly TimeSpan Span = TimeSpan.FromMinutes(10);
    public const int MaxPoints = 600;

    private readonly SlidingWindow<decimal?> _window = new(Span, MaxPoints);

    /// <summary>The chart's data (same instance for the life of the window).</summary>
    public List<LivePoint> Points { get; } = new();

    /// <summary>Adds a point; false when it is out of order (older than the newest point) and was ignored.</summary>
    public bool Add(DateTimeOffset tsUtc, decimal? powerW)
    {
        if (!_window.Add(tsUtc, powerW)) return false;
        Rebuild();
        return true;
    }

    /// <summary>Adds many points at once, oldest first (the list is rebuilt once).</summary>
    public void AddRange(IEnumerable<(DateTimeOffset TsUtc, decimal? PowerW)> points)
    {
        foreach (var (ts, w) in points) _window.Add(ts, w);
        Rebuild();
    }

    private void Rebuild()
    {
        Points.Clear();
        foreach (var (ts, w) in _window.Snapshot()) Points.Add(new LivePoint(ts.UtcDateTime, w));
    }
}

/// <summary>
/// The common time grid of the live chart. The main meter and the plugs report at different moments, while ApexCharts
/// needs the same x values in every series for its shared tooltip, so a sample only updates the latest value of its
/// series and <see cref="Tick"/> adds one point per whole UTC second to all of them, with the same x. A value is held for
/// <see cref="MaxSampleAge"/>; after that the series gets null (a gap in the line).
/// <see cref="Observe"/> may run on any thread (the stream's); the other members run on the component's dispatcher.
/// </summary>
public sealed class LiveChartGrid
{
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromSeconds(30);

    /// <summary>Ticks between two full pushes of the window (they trim the chart to 10 minutes).</summary>
    public const int TrimEvery = 60;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, Line> _lines;
    private DateTimeOffset? _lastX;
    private DateTime? _sentThrough;
    private int _ticksSinceFullPush;

    public LiveChartGrid(IEnumerable<Guid> seriesIds) =>
        _lines = seriesIds.Distinct().ToDictionary(id => id, _ => new Line());

    /// <summary>The points of a series (same list instance for the life of the grid; the chart's Items).</summary>
    public List<LivePoint> Points(Guid seriesId) => _lines[seriesId].Window.Points;

    /// <summary>True when the next push should be the whole window. Stays true until a full push succeeded.</summary>
    public bool FullPushDue
    {
        get { lock (_sync) return _ticksSinceFullPush >= TrimEvery; }
    }

    /// <summary>
    /// Resamples the samples received before the page opened on the grid, up to the current second, by the rule of
    /// <see cref="Tick"/>. These points go with the chart's first render, so they are not pending.
    /// </summary>
    public void Seed(Func<Guid, IEnumerable<LiveSample>> recent, DateTimeOffset nowUtc)
    {
        var end = WholeSecond(nowUtc);
        lock (_sync)
        {
            var samples = _lines.Keys.ToDictionary(id => id, id => recent(id).OrderBy(s => s.TsUtc).ToList());
            var first = samples.Values.Where(list => list.Count > 0).Select(list => list[0].TsUtc).DefaultIfEmpty(end).Min();
            var start = WholeSecond(first) == first ? first : WholeSecond(first).AddSeconds(1);
            var oldest = end - LiveChartWindow.Span + TimeSpan.FromSeconds(1);
            if (start < oldest) start = oldest;

            foreach (var (id, line) in _lines)
            {
                var list = samples[id];
                var next = 0;
                var points = new List<(DateTimeOffset, decimal?)>();
                for (var x = start; x <= end; x = x.AddSeconds(1))
                {
                    while (next < list.Count && list[next].TsUtc <= x) line.Observe(list[next++]);
                    points.Add((x, line.ValueAt(x)));
                }
                while (next < list.Count) line.Observe(list[next++]);   // received within the current second: for the first tick
                line.Window.AddRange(points);
            }

            _lastX = end;
            _sentThrough = end.UtcDateTime;
        }
    }

    /// <summary>Updates the latest value of the sample's series; false when the series is not on this chart or the sample is out of order.</summary>
    public bool Observe(LiveSample sample)
    {
        lock (_sync) return _lines.TryGetValue(sample.PlugId, out var line) && line.Observe(sample);
    }

    /// <summary>Adds the point of the current whole second to every series; false when that second already has its point.</summary>
    public bool Tick(DateTimeOffset nowUtc)
    {
        var x = WholeSecond(nowUtc);
        lock (_sync)
        {
            if (_lastX is { } last && x <= last) return false;
            foreach (var line in _lines.Values) line.Window.Add(x, line.ValueAt(x));
            _lastX = x;
            _ticksSinceFullPush++;
            return true;
        }
    }

    /// <summary>The points not sent to the chart yet, per series (the same x values in all of them).</summary>
    public Dictionary<Guid, IReadOnlyList<LivePoint>> Pending()
    {
        lock (_sync)
        {
            return _lines.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<LivePoint>)kv.Value.Window.Points.Where(p => _sentThrough is not { } sent || p.TimeUtc > sent).ToList());
        }
    }

    /// <summary>Records what reached the chart: the points up to <paramref name="throughUtc"/>; a full push also restarts the count towards the next one.</summary>
    public void MarkSent(DateTime? throughUtc, bool fullPush = false)
    {
        lock (_sync)
        {
            if (throughUtc is { } through && (_sentThrough is not { } sent || through > sent)) _sentThrough = through;
            if (fullPush) _ticksSinceFullPush = 0;
        }
    }

    /// <summary>
    /// ApexCharts pairs the data of <c>appendData</c> with its series by position, and Blazor-ApexCharts finds them by name
    /// (two series with one name would both get the same points), so plugs with the same alias need distinct series names.
    /// </summary>
    public static IReadOnlyList<string> UniqueNames(IEnumerable<string> names)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var name in names)
        {
            var unique = name;
            for (var n = 2; !seen.Add(unique); n++) unique = $"{name} #{n}";
            result.Add(unique);
        }
        return result;
    }

    private static DateTimeOffset WholeSecond(DateTimeOffset ts) => DateTimeOffset.FromUnixTimeSeconds(ts.ToUnixTimeSeconds());

    private sealed class Line
    {
        private DateTimeOffset? _ts;
        private decimal _powerW;

        public LiveChartWindow Window { get; } = new();

        public bool Observe(LiveSample s)
        {
            if (_ts is { } ts && s.TsUtc < ts) return false;
            _ts = s.TsUtc;
            _powerW = Math.Round((decimal)s.PowerW, 1);
            return true;
        }

        public decimal? ValueAt(DateTimeOffset x) => _ts is { } ts && x - ts <= MaxSampleAge ? _powerW : null;
    }
}
