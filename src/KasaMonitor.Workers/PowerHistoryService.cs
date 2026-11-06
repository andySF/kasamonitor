using System.Diagnostics;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Reports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KasaMonitor.Workers;

/// <summary>A series the „Putere” chart can show: the main meter or another plug / meter channel (not archived).</summary>
public sealed record PowerChannel(Guid PlugId, string Alias, bool IsMain);

/// <summary>The points of one plug over one period at one resolution (null = no data in that bucket).</summary>
public sealed record HistoryPowerSeries(Guid PlugId, ReportPeriod Period, PowerResolution Resolution, IReadOnlyList<DateTimeOffset> Grid,
    IReadOnlyList<PowerPoint?> Points)
{
    public PowerSummary? Summary => PowerHistory.Summary(Points);
}

/// <summary>
/// „Putere” of /history (0.14.0): the channels, the selected series (in the address order) and their points, and — with a
/// comparison — one series of the comparison period (<see cref="ComparedPlugId"/>: the main meter when shown, else the
/// first selected) aligned on the period's grid. <see cref="NowIndex"/>: the bucket of now in a period in progress (the
/// comparison's figures then cover the same part of it, <see cref="ComparisonSummary"/>).
/// </summary>
public sealed record HistoryPower(ReportPeriod Period, PowerResolution Resolution, IReadOnlyList<DateTimeOffset> Grid,
    IReadOnlyList<PowerChannel> Channels, IReadOnlyList<Guid> Selected, IReadOnlyList<HistoryPowerSeries> Series,
    CompareMode Compare, ReportPeriod? ComparisonPeriod, Guid? ComparedPlugId, IReadOnlyList<PowerPoint?>? ComparisonAligned,
    PowerSummary? ComparisonSummary, int? NowIndex)
{
    public PowerChannel? Main => Channels.FirstOrDefault(c => c.IsMain);
}

/// <summary>
/// The power chart of /history from the aggregates only (minute rows for ≤ 2 days, hour rows otherwise; never Samples or
/// LiveSamples, so it keeps working once the retention trimmed the raw readings). One series of one period is read on the
/// key (PlugId, BucketStartUtc) and cached (<see cref="PeriodCache{TKey,TValue}"/>: shared by every circuit and the CSV,
/// stale-while-revalidate); toggling a series reads only that series.
/// </summary>
public sealed class PowerHistoryService
{
    public const int MaxCachedSeries = 96;

    private readonly IServiceScopeFactory _scopes;
    private readonly Func<TimeZoneInfo> _tz;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _log;
    private readonly PeriodCache<(Guid Plug, ReportPeriod Period, PowerResolution Resolution), HistoryPowerSeries> _cache;

    public PowerHistoryService(IServiceScopeFactory scopes, EnergyReportService reports, ILogger<PowerHistoryService>? log = null, Func<DateTimeOffset>? clock = null)
        : this(scopes, () => reports.TimeZone, log, clock)
    {
    }

    public PowerHistoryService(IServiceScopeFactory scopes, Func<TimeZoneInfo> timeZone, ILogger<PowerHistoryService>? log = null, Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _tz = timeZone;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log ?? (ILogger)NullLogger.Instance;
        _cache = new PeriodCache<(Guid, ReportPeriod, PowerResolution), HistoryPowerSeries>(_clock, _tz, MaxCachedSeries, _log, "Power series");
    }

    public TimeZoneInfo TimeZone => _tz();

    /// <summary>The time the last series took to read and compute (ms), for the log and the report.</summary>
    public long LastDurationMs { get; private set; }

    /// <summary>The section of a period: <paramref name="selection"/> null = the default (the main meter alone).</summary>
    public async Task<HistoryPower> GetAsync(ReportPeriod period, CompareMode compare, IReadOnlyList<Guid>? selection, CancellationToken ct)
    {
        var tz = TimeZone;
        var channels = await ChannelsAsync(ct);
        var main = channels.FirstOrDefault(c => c.IsMain)?.PlugId;
        var known = channels.Select(c => c.PlugId).ToHashSet();
        var selected = selection is null
            ? (main is { } m ? new[] { m } : Array.Empty<Guid>())
            : selection.Where(known.Contains).ToArray();
        var resolution = PowerHistory.ResolutionFor(period);
        var grid = PowerHistory.Grid(period, resolution, tz);
        var series = await Task.WhenAll(selected.Select(id => SeriesAsync(id, period, resolution, ct)));
        var nowIndex = PowerHistory.NowIndex(grid, period.EndUtc(tz), _clock());

        var compared = compare == CompareMode.None ? null : PowerHistory.Reference(selected, main);
        var comparisonPeriod = compared is null ? null : PowerHistory.ComparisonPeriod(period, compare);
        IReadOnlyList<PowerPoint?>? aligned = null;
        PowerSummary? comparisonSummary = null;
        if (compared is { } cid && comparisonPeriod is { } cp)
        {
            // The comparison is drawn at the resolution of the period (a range of 46 days against 45 last year stays daily).
            var other = await SeriesAsync(cid, cp, resolution, ct);
            aligned = PowerHistory.Align(period, grid, cp, other.Grid, other.Points, tz);
            comparisonSummary = PowerHistory.ComparisonSummary(other.Points, aligned, nowIndex);
        }
        return new HistoryPower(period, resolution, grid, channels, selected, series, compare, comparisonPeriod, compared, aligned, comparisonSummary, nowIndex);
    }

    /// <summary>The main meter first, then the other plugs and meter channels that are not archived, by name.</summary>
    public async Task<IReadOnlyList<PowerChannel>> ChannelsAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var plugs = await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        return plugs.Where(p => !p.IsArchived)
            .OrderBy(p => p.Role == PlugRole.MainMeter ? 0 : 1)
            .ThenBy(p => p.Alias, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PowerChannel(p.Id, p.Alias, p.Role == PlugRole.MainMeter))
            .ToList();
    }

    /// <summary>One plug over one period at one resolution (cached).</summary>
    public Task<HistoryPowerSeries> SeriesAsync(Guid plugId, ReportPeriod period, PowerResolution resolution, CancellationToken ct) =>
        _cache.GetAsync((plugId, period, resolution), period, _ => ComputeAsync(plugId, period, resolution, CancellationToken.None), ct);

    private async Task<HistoryPowerSeries> ComputeAsync(Guid plugId, ReportPeriod period, PowerResolution resolution, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tz = TimeZone;
        var from = period.StartUtc(tz);
        var to = period.EndUtc(tz);
        IReadOnlyList<PowerAggregate> rows;
        using (var scope = _scopes.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IPowerAggregateRepository>();
            rows = resolution == PowerResolution.Minute
                ? await repo.ListMinutesAsync(plugId, from, to, ct)
                : await repo.ListHoursAsync(plugId, from, to, ct);
        }
        var grid = PowerHistory.Grid(period, resolution, tz);
        var points = PowerHistory.Points(grid, to, rows);
        LastDurationMs = sw.ElapsedMilliseconds;
        _log.LogDebug("Power of {Plug} for {Kind} {Start} ({Resolution}, {Rows} rows) computed in {Ms} ms",
            plugId, period.Kind, period.Start, resolution, rows.Count, sw.ElapsedMilliseconds);
        return new HistoryPowerSeries(plugId, period, resolution, grid, points);
    }
}
