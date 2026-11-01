using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Metering;

/// <summary>How "restul casei" (main meter − plugs) compares with the measurement error.</summary>
public enum RestKind
{
    /// <summary>Clearly above the margin: a real rest of the house.</summary>
    Positive,

    /// <summary>|main − plugs| within the margin: shown as "≈ 0" (noise, unsynchronised readings).</summary>
    WithinMargin,

    /// <summary>The plugs exceed the meter by more than the margin: shown as 0 with a warning (clamp position).</summary>
    Negative,

    /// <summary>
    /// Energy only: the plugs exceed the meter, but the meter's data of that interval is partial (live coverage under
    /// 90 %): shown as 0 with „date parțiale”, not as a clamp problem.
    /// </summary>
    PartialData,
}

/// <summary>
/// Main meter vs plugs over the same interval. <see cref="RestW"/> = max(0, main − plugs) (W for power, kWh for energy);
/// <see cref="RawW"/> keeps the signed difference.
/// </summary>
/// <see cref="FromPolls"/>: computed from the 10 s polled samples (no live stream in that minute) instead of the live stream.
public sealed record RestBalance(DateTimeOffset? IntervalStartUtc, double MainW, double PlugsW, double RawW, double RestW, RestKind Kind, bool FromPolls = false);

/// <summary>"Restul casei": what the main meter measures beyond the plugs.</summary>
public static class HouseBalance
{
    /// <summary>Fixed part of the error margin: 15 W (for energy: 15 W × the interval's hours).</summary>
    public const double MarginW = 15;

    /// <summary>Proportional part of the error margin: 3 % of the main meter.</summary>
    public const double MarginFraction = 0.03;

    /// <summary>Margin of a comparison: max(<paramref name="absoluteMargin"/>, 3 % of the main meter).</summary>
    public static double Margin(double main, double absoluteMargin) => Math.Max(absoluteMargin, MarginFraction * Math.Abs(main));

    /// <summary>
    /// Classifies main − plugs: within max(<paramref name="absoluteMargin"/>, 3 % of main) → <see cref="RestKind.WithinMargin"/>,
    /// below −margin → <see cref="RestKind.Negative"/>, otherwise <see cref="RestKind.Positive"/>.
    /// </summary>
    public static RestKind Classify(double main, double plugs, double absoluteMargin)
    {
        var raw = main - plugs;
        var margin = Margin(main, absoluteMargin);
        return Math.Abs(raw) <= margin ? RestKind.WithinMargin : raw < 0 ? RestKind.Negative : RestKind.Positive;
    }

    /// <summary>Balance of two values measured over the same interval (power in W with the 15 W margin by default).</summary>
    public static RestBalance Balance(double main, double plugs, double absoluteMargin = MarginW, DateTimeOffset? intervalStartUtc = null) =>
        new(intervalStartUtc, main, plugs, main - plugs, Math.Max(0, main - plugs), Classify(main, plugs, absoluteMargin));

    /// <summary>Absolute margin of an energy comparison over <paramref name="hours"/> hours, in kWh (15 W × hours).</summary>
    public static double EnergyMarginKwh(double hours) => MarginW * hours / 1000.0;

    /// <summary>
    /// Mean power of the samples of the minute starting at <paramref name="minuteStartUtc"/>, weighted by the seconds
    /// each sample stands for (as in the minute aggregates); null when there is none.
    /// </summary>
    public static double? MinuteAverageW(IEnumerable<LiveSample> samples, DateTimeOffset minuteStartUtc)
    {
        var end = minuteStartUtc.AddMinutes(1);
        double sum = 0, seconds = 0;
        foreach (var s in samples)
        {
            if (s.TsUtc < minuteStartUtc || s.TsUtc >= end) continue;
            var w = s.Seconds > 0 ? s.Seconds : 1;
            sum += s.PowerW * w;
            seconds += w;
        }
        return seconds == 0 ? null : sum / seconds;
    }

    /// <summary>
    /// "Restul casei" now, from averages aligned on the last whole minute (not from the latest instantaneous values,
    /// which come at different rates and moments): the main meter's mean over that minute vs Σ the plugs' means over
    /// the same minute (from their live samples; an online plug without a live sample in that minute counts with its
    /// latest polled reading). Null when the main meter has no sample in that minute.
    /// </summary>
    public static RestBalance? LastWholeMinute(DateTimeOffset nowUtc, Guid mainId, Func<Guid, IEnumerable<LiveSample>> recent,
        IEnumerable<PlugLiveState> states)
    {
        var minute = LastWholeMinuteStart(nowUtc);
        if (MinuteAverageW(recent(mainId), minute) is not { } main) return null;
        return Aligned(minute, main, id => MinuteAverageW(recent(id), minute), states, fromPolls: false);
    }

    /// <summary>Start of the last whole UTC minute before <paramref name="nowUtc"/>.</summary>
    public static DateTimeOffset LastWholeMinuteStart(DateTimeOffset nowUtc) => LiveMinuteAggregator.MinuteOf(nowUtc).AddMinutes(-1);

    /// <summary>
    /// Fallback without a live stream on the main meter: the same last whole minute, from the means of the 10 s polled
    /// samples (<paramref name="polledAvgW"/>: plug → mean W of its online samples in that minute). A plug with a live
    /// sample in the minute uses the live mean; a plug with neither uses its latest polled reading when online.
    /// Null when the main meter has no polled sample in that minute either.
    /// </summary>
    public static RestBalance? FromPolledMinute(DateTimeOffset minuteStartUtc, Guid mainId, IReadOnlyDictionary<Guid, double> polledAvgW,
        Func<Guid, IEnumerable<LiveSample>> recent, IEnumerable<PlugLiveState> states)
    {
        if (!polledAvgW.TryGetValue(mainId, out var main)) return null;
        return Aligned(minuteStartUtc, main,
            id => MinuteAverageW(recent(id), minuteStartUtc) ?? (polledAvgW.TryGetValue(id, out var w) ? w : null),
            states, fromPolls: true);
    }

    private static RestBalance Aligned(DateTimeOffset minute, double main, Func<Guid, double?> plugAvg, IEnumerable<PlugLiveState> states, bool fromPolls)
    {
        double plugs = 0;
        foreach (var s in states.Where(s => PlugRoles.CountsInTotals(s.Role)))
        {
            if (plugAvg(s.PlugId) is { } avg) plugs += avg;
            else if (s is { IsOnline: true, LastReading: { } r }) plugs += r.PowerMw / 1000.0;
        }
        return Balance(main, plugs, MarginW, minute) with { FromPolls = fromPolls };
    }

    /// <summary>Σ power (W) of the online plugs whose role counts towards totals (<see cref="PlugRole.Plug"/>).</summary>
    public static double PlugsW(IEnumerable<PlugLiveState> states) => states
        .Where(s => PlugRoles.CountsInTotals(s.Role) && s.IsOnline && s.LastReading is not null)
        .Sum(s => s.LastReading!.PowerMw / 1000.0);

    /// <summary>Main meter power minus the online plugs; null without a main meter reading.</summary>
    public static double? RestW(double? mainW, IEnumerable<PlugLiveState> states) => mainW is { } m ? m - PlugsW(states) : null;

    /// <summary>The main meter's power: the 1-second stream when its sample is at most <paramref name="maxAge"/> old, else the 10 s poll.</summary>
    public static double? MainW(PlugLiveState? polled, LiveSample? streamed, DateTimeOffset nowUtc, TimeSpan maxAge)
    {
        if (streamed is not null && nowUtc - streamed.TsUtc <= maxAge) return streamed.PowerW;
        return polled is { IsOnline: true, LastReading: { } r } ? r.PowerMw / 1000.0 : null;
    }
}
