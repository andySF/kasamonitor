using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Metering;

/// <summary>"Did the power change?": the single rule shared by the adaptive interval and the change-based storage.</summary>
public static class ChangeRule
{
    public const double MinDeltaW = 1.0;
    public const double MinDeltaRatio = 0.01;

    /// <summary>|now − previous| &gt; max(1 W, 1 % of |previous|).</summary>
    public static bool IsSignificant(double previousW, double nowW) =>
        Math.Abs(nowW - previousW) > Math.Max(MinDeltaW, MinDeltaRatio * Math.Abs(previousW));
}

/// <summary>
/// Wall-clock alignment of the stream ticks: every tick at the start of a second (HH:MM:SS.000), the sample stamped
/// with the nearest second. The rounding boundary (.500) is as far as possible from the tick, so a timer that wakes a
/// few ms early or up to ~490 ms late keeps its second. (Ticking at .500 and rounding, measured on 2026-11-01, lost
/// 2–5 seconds per minute: a wake-up at .499 rounded to the previous tick's second.)
/// </summary>
public static class LiveClock
{
    /// <summary>Time from <paramref name="now"/> to the next whole second of the wall clock (always &gt; 0).</summary>
    public static TimeSpan DelayToNextSecond(DateTimeOffset now)
    {
        var ticksIntoSecond = now.UtcTicks % TimeSpan.TicksPerSecond;
        return TimeSpan.FromTicks(TimeSpan.TicksPerSecond - ticksIntoSecond);
    }

    /// <summary>The nearest whole second (±500 ms tolerance for a late tick or a slow answer).</summary>
    public static DateTimeOffset RoundToSecond(DateTimeOffset t)
    {
        var utc = t.UtcTicks + TimeSpan.TicksPerSecond / 2;
        return new DateTimeOffset(utc - utc % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }
}

/// <summary>
/// Adaptive poll interval of one channel. The main meter is fixed at the base interval (1 s). Plugs: base interval
/// while the power changed (<see cref="ChangeRule"/>) during the last <see cref="ActiveWindow"/> or a house event is
/// active (the main meter jumped), otherwise one second longer after each quiet reading, up to <see cref="MaxSeconds"/>.
/// The first change brings it back to the base interval at once.
/// </summary>
public sealed class AdaptiveInterval
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(60);
    public const int MaxSeconds = 10;

    private readonly bool _fixed;
    private readonly int _base;
    private double? _lastW;
    private DateTimeOffset _lastChange = DateTimeOffset.MinValue;

    public AdaptiveInterval(bool fixedRate, int baseSeconds = 1)
    {
        _fixed = fixedRate;
        _base = Math.Max(1, baseSeconds);
        CurrentSeconds = _base;
    }

    /// <summary>The interval chosen at the last reading (seconds).</summary>
    public int CurrentSeconds { get; private set; }

    /// <summary>Records a reading and returns the seconds until the next one.</summary>
    public int Next(DateTimeOffset ts, double powerW, DateTimeOffset boostUntil)
    {
        var changed = _lastW is not { } last || ChangeRule.IsSignificant(last, powerW);
        _lastW = powerW;
        if (changed) _lastChange = ts;
        if (_fixed || changed || ts - _lastChange < ActiveWindow || ts < boostUntil)
            CurrentSeconds = _base;
        else
            CurrentSeconds = Math.Min(Math.Max(MaxSeconds, _base), CurrentSeconds + 1);
        return CurrentSeconds;
    }

    /// <summary>A house event: back to the base interval (the caller polls the channel at once).</summary>
    public void Boost() => CurrentSeconds = _base;
}

/// <summary>
/// Change-based storage of raw samples, per plug: a sample is written when its power differs significantly
/// (<see cref="ChangeRule"/>) from the last WRITTEN one, or when <see cref="Witness"/> has passed since then.
/// </summary>
public sealed class ChangeFilter
{
    public static readonly TimeSpan Witness = TimeSpan.FromSeconds(60);

    private readonly Dictionary<Guid, (DateTimeOffset Ts, double W)> _written = new();

    public bool ShouldWrite(LiveSample s)
    {
        if (_written.TryGetValue(s.PlugId, out var last)
            && s.TsUtc - last.Ts < Witness
            && !ChangeRule.IsSignificant(last.W, s.PowerW))
            return false;
        _written[s.PlugId] = (s.TsUtc, s.PowerW);
        return true;
    }
}
