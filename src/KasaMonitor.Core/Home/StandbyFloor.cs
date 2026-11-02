using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Home;

/// <summary>
/// "Standby / mereu pornit": the night floor of the REST of the house (main meter − plugs), in W, from
/// <see cref="Minutes"/> minutes of <see cref="Nights"/> nights. It is a part of the rest, never of the plugs: a plug's own
/// standby stays in the plug's bubble (no double counting).
/// </summary>
public sealed record StandbyFloor(double Watts, int Nights, int Minutes);

/// <summary>
/// The rule of <see cref="StandbyFloor"/>: the median of (main − Σ plugs) per minute between 01:00 and 05:00 local time over
/// the last 7 nights that have ended (fewer when the history is shorter), from the live minute aggregates. Every plug that
/// counts in the totals is subtracted, so none is counted twice (in its bubble and in the standby):
/// <list type="bullet">
/// <item>a plug with a live stream (<c>streamed</c>) by its minute aggregates: a minute counts only when every streamed plug
/// has it, and a night counts only when every streamed plug covers at least <see cref="MinPlugCoverage"/> of the main
/// meter's minutes (a plug offline or without stream that night would otherwise land in "standby");</item>
/// <item>a plug without a live stream by its mean polled power of that night (<c>otherPlugsW</c>, from the 10 s samples):
/// null for a night = some such plug has no sample then, and the night is skipped.</item>
/// </list>
/// The result is never below 0.
/// </summary>
public static class StandbyRule
{
    /// <summary>Share of the main meter's night minutes every streamed plug must have for the night to count.</summary>
    public const double MinPlugCoverage = 0.5;

    public static readonly TimeOnly NightStart = new(1, 0);
    public static readonly TimeOnly NightEnd = new(5, 0);
    public const int Nights = 7;

    /// <summary>The night windows [01:00, 05:00) of the last <paramref name="nights"/> nights ended before <paramref name="nowUtc"/>, newest first.</summary>
    public static IReadOnlyList<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> NightWindows(DateTimeOffset nowUtc, TimeZoneInfo tz, int nights = Nights)
    {
        var list = new List<(DateTimeOffset, DateTimeOffset)>();
        var today = LocalClock.Day(nowUtc, tz);
        for (var d = today; list.Count < nights && d >= today.AddDays(-nights); d = d.AddDays(-1))
        {
            var from = LocalClock.ToUtc(d.ToDateTime(NightStart), tz);
            var to = LocalClock.ToUtc(d.ToDateTime(NightEnd), tz);
            if (to <= nowUtc) list.Add((from, to));
        }
        return list;
    }

    /// <summary>
    /// The floor from the minute aggregates of the main meter (<paramref name="mainId"/>) and of the streamed plugs
    /// (<paramref name="streamedPlugIds"/>) inside <paramref name="windows"/>, minus the plugs without a stream
    /// (<paramref name="otherPlugsW"/>: their summed mean W per window index, null = unknown → night skipped; omitted = none).
    /// Null when no night qualifies.
    /// </summary>
    public static StandbyFloor? Compute(IEnumerable<LiveMinuteAggregate> minutes, Guid mainId, IReadOnlyCollection<Guid> streamedPlugIds,
        IReadOnlyList<(DateTimeOffset FromUtc, DateTimeOffset ToUtc)> windows, Func<int, double?>? otherPlugsW = null)
    {
        var all = minutes.ToList();
        var rests = new List<double>();
        var nights = 0;
        for (var wi = 0; wi < windows.Count; wi++)
        {
            var (from, to) = windows[wi];
            var other = otherPlugsW is null ? 0 : otherPlugsW(wi);
            if (other is not { } otherW) continue;
            var inWindow = all.Where(m => m.MinuteUtc >= from && m.MinuteUtc < to).ToList();
            var main = inWindow.Where(m => m.PlugId == mainId).GroupBy(m => m.MinuteUtc).ToDictionary(g => g.Key, g => g.First().AvgW);
            if (main.Count == 0) continue;
            var plugs = streamedPlugIds.Where(id => id != mainId)
                .Select(id => inWindow.Where(m => m.PlugId == id).GroupBy(m => m.MinuteUtc).ToDictionary(x => x.Key, x => x.First().AvgW))
                .ToList();
            if (plugs.Any(p => p.Count < MinPlugCoverage * main.Count)) continue;
            var before = rests.Count;
            foreach (var (minute, mainW) in main.OrderBy(kv => kv.Key))
            {
                double sum = 0;
                var complete = true;
                foreach (var p in plugs)
                {
                    if (!p.TryGetValue(minute, out var w)) { complete = false; break; }
                    sum += w;
                }
                if (complete) rests.Add(mainW - sum - otherW);
            }
            if (rests.Count > before) nights++;
        }
        if (rests.Count == 0) return null;
        return new StandbyFloor(Math.Max(0, Median(rests)), nights, rests.Count);
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) throw new ArgumentException("No values.", nameof(values));
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
