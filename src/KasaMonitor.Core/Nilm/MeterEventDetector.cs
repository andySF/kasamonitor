using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Nilm;

/// <summary>A step found by <see cref="MeterEventDetector"/>, before it is stored (see <see cref="MeterEvent"/> for the fields).</summary>
public sealed record DetectedEvent(
    Guid PlugId,
    DateTimeOffset TsUtc,
    double DeltaP,
    double DeltaQ,
    double DeltaPf,
    double TransientRatio,
    double RiseSeconds,
    double PowerBeforeW)
{
    public MeterEvent ToEvent() => new(0, PlugId, TsUtc, DeltaP, DeltaQ, DeltaPf, TransientRatio, RiseSeconds, PowerBeforeW);
}

/// <summary>
/// Steady-state transition detector over the 1-second power of the main meter (deterministic, explainable).
/// <list type="number">
/// <item>Steady: the "before" level is the mean of the last <see cref="BeforeSeconds"/> seconds; a second that differs
/// from it by more than max(<see cref="StableAbsW"/>, <see cref="StableRel"/> × level) starts a transition. A slow drift
/// stays inside the tolerance and moves the level with it (no event).</item>
/// <item>Transition: ends when the last <see cref="StableSeconds"/> seconds are within that tolerance of their mean (the
/// "after" level). ΔP = after − before; an event when |ΔP| ≥ <see cref="MinEventDeltaW"/>.</item>
/// <item>Staircase: an intermediate plateau of ≥ 2 s inside the transition, reached and left in the same direction by
/// ≥ <see cref="MinEventDeltaW"/> each, is a separate event (two appliances switched 2 s apart). A peak that falls back
/// (motor inrush) is not a plateau: it becomes the event's <see cref="DetectedEvent.TransientRatio"/>.</item>
/// <item>Gaps: missing seconds up to <see cref="MaxFillSeconds"/> are filled with the previous value (the raw table keeps
/// a row only when the power changed, or every 60 s); a longer gap, or a transition that does not settle within
/// <see cref="MaxTransitionSeconds"/>, restarts the detector (no event across it).</item>
/// </list>
/// Q = √(S² − P²) from the apparent power (0 when the meter does not report S); pf = P / S.
/// </summary>
public sealed class MeterEventDetector
{
    public const double DefaultMinEventDeltaW = 30;
    public const int BeforeSeconds = 5;
    public const int StableSeconds = 3;
    public const double StableAbsW = 5;
    public const double StableRel = 0.03;
    public const int TransientSeconds = 2;
    public const int MaxFillSeconds = 65;
    public const int MaxTransitionSeconds = 30;
    private const int MinPlateauSeconds = 2;

    private readonly record struct Point(DateTimeOffset Ts, double P, double Q, double Pf);
    private readonly record struct Level(double P, double Q, double Pf);

    private readonly List<Point> _steady = new();       // last BeforeSeconds points of the current steady state
    private readonly List<Point> _transition = new();   // points since the transition started
    private bool _inTransition;
    private Point? _last;
    private Guid _plugId;

    public MeterEventDetector(double minEventDeltaW = DefaultMinEventDeltaW) => MinEventDeltaW = minEventDeltaW;

    public double MinEventDeltaW { get; set; }

    /// <summary>Second of the last processed sample (null before the first one or after <see cref="Reset"/>).</summary>
    public DateTimeOffset? LastTs => _last?.Ts;

    /// <summary>Forgets everything (another meter, a long gap, the module switched off).</summary>
    public void Reset()
    {
        _steady.Clear();
        _transition.Clear();
        _inTransition = false;
        _last = null;
    }

    public static double Tolerance(double levelW) => Math.Max(StableAbsW, StableRel * Math.Abs(levelW));

    /// <summary>|Q| from P and S (0 without S, or when S &lt; |P| by rounding).</summary>
    public static double Reactive(double p, double? s) => s is { } va && va > Math.Abs(p) ? Math.Sqrt(va * va - p * p) : 0;

    /// <summary>Feeds one sample (any order of seconds is tolerated: older or repeated seconds are ignored).</summary>
    public IReadOnlyList<DetectedEvent> Add(LiveSample sample)
    {
        var ts = new DateTimeOffset(sample.TsUtc.UtcTicks - sample.TsUtc.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        if (_last is { } last && ts <= last.Ts) return Array.Empty<DetectedEvent>();
        if (_last is not null && sample.PlugId != _plugId) Reset();
        _plugId = sample.PlugId;
        var q = Reactive(sample.PowerW, sample.ApparentVa);
        var pf = sample.Pf ?? (sample.ApparentVa is { } s && s > 0 ? Math.Abs(sample.PowerW) / s : 1);
        var point = new Point(ts, sample.PowerW, q, pf);

        List<DetectedEvent>? events = null;
        if (_last is { } prev)
        {
            var gap = (long)(ts - prev.Ts).TotalSeconds;
            if (gap > MaxFillSeconds)
            {
                Reset();
            }
            else
            {
                for (var k = 1; k < gap; k++)
                    Step(prev with { Ts = prev.Ts.AddSeconds(k) }, ref events);
            }
        }
        Step(point, ref events);
        return (IReadOnlyList<DetectedEvent>?)events ?? Array.Empty<DetectedEvent>();
    }

    private void Step(Point x, ref List<DetectedEvent>? events)
    {
        _last = x;
        if (!_inTransition && _steady.Count > 0)
        {
            var before = Mean(_steady);
            if (Math.Abs(x.P - before.P) <= Tolerance(before.P))
            {
                _steady.Add(x);
                if (_steady.Count > BeforeSeconds) _steady.RemoveAt(0);
                return;
            }
            _inTransition = true;
            _transition.Clear();
        }
        else if (!_inTransition)
        {
            _inTransition = true;   // first points (or after a reset): wait for a steady state, no "before" level yet
            _transition.Clear();
        }

        _transition.Add(x);
        if (_transition.Count >= StableSeconds && IsStable(_transition, _transition.Count - StableSeconds, StableSeconds, out var after))
        {
            if (_steady.Count > 0) Settle(Mean(_steady), after, ref events);
            var plateauStart = PlateauStart(after.P);
            _steady.Clear();
            for (var i = Math.Max(plateauStart, _transition.Count - BeforeSeconds); i < _transition.Count; i++) _steady.Add(_transition[i]);
            _transition.Clear();
            _inTransition = false;
        }
        else if (_transition.Count > MaxTransitionSeconds)
        {
            // A load that keeps changing (induction hob, inverter): no reliable "after" level. Start over from here.
            _steady.Clear();
            _transition.RemoveRange(0, _transition.Count - 1);
        }
    }

    /// <summary>Index of the first point of the final plateau: the longest tail within tolerance of the "after" level.</summary>
    private int PlateauStart(double afterP)
    {
        var tol = Tolerance(afterP);
        var i = _transition.Count;
        while (i > 0 && Math.Abs(_transition[i - 1].P - afterP) <= tol) i--;
        return i;
    }

    private void Settle(Level before, Level after, ref List<DetectedEvent>? events)
    {
        var plateauStart = PlateauStart(after.P);

        // Intermediate plateaus (≥ 2 s, mutually stable) before the final one.
        var levels = new List<(int Start, int End, Level L)>();
        var i = 0;
        while (i < plateauStart)
        {
            var j = i + 1;
            while (j < plateauStart && IsStable(_transition, i, j - i + 1, out _)) j++;
            var len = j - i;
            if (len >= MinPlateauSeconds && IsStable(_transition, i, len, out var lvl))
            {
                levels.Add((i, j, lvl));
                i = j;
            }
            else
            {
                i++;
            }
        }

        var prev = before;
        var stepStart = 0;
        for (var k = 0; k < levels.Count; k++)
        {
            var (start, end, lvl) = levels[k];
            var next = k + 1 < levels.Count ? levels[k + 1].L : after;
            var up = lvl.P - prev.P;
            var then = next.P - lvl.P;
            if (Math.Abs(up) < MinEventDeltaW || Math.Abs(then) < MinEventDeltaW || Math.Sign(up) != Math.Sign(then)) continue;
            Emit(prev, lvl, stepStart, start, ref events);
            prev = lvl;
            stepStart = end;
        }
        Emit(prev, after, stepStart, Math.Max(plateauStart, stepStart), ref events);
    }

    /// <param name="stepStart">First point of the step inside the transition.</param>
    /// <param name="levelStart">First point of the level reached by the step.</param>
    private void Emit(Level from, Level to, int stepStart, int levelStart, ref List<DetectedEvent>? events)
    {
        var deltaP = to.P - from.P;
        if (Math.Abs(deltaP) < MinEventDeltaW || stepStart >= _transition.Count) return;
        var t0 = _transition[stepStart].Ts;
        var sign = Math.Sign(deltaP);
        var peak = double.MinValue;
        for (var i = stepStart; i < _transition.Count && _transition[i].Ts < t0.AddSeconds(TransientSeconds); i++)
            peak = Math.Max(peak, (_transition[i].P - from.P) * sign);
        var ratio = peak == double.MinValue ? 1 : Math.Max(0, peak / Math.Abs(deltaP));
        var rise = levelStart < _transition.Count ? (_transition[levelStart].Ts - t0).TotalSeconds : 0;
        (events ??= new()).Add(new DetectedEvent(_plugId, t0, deltaP, to.Q - from.Q, to.Pf - from.Pf,
            Math.Round(ratio, 3), Math.Max(0, rise), from.P));
    }

    private static bool IsStable(List<Point> points, int start, int count, out Level mean)
    {
        double p = 0, q = 0, pf = 0;
        for (var i = start; i < start + count; i++) { p += points[i].P; q += points[i].Q; pf += points[i].Pf; }
        mean = new Level(p / count, q / count, pf / count);
        var tol = Tolerance(mean.P);
        for (var i = start; i < start + count; i++)
            if (Math.Abs(points[i].P - mean.P) > tol) return false;
        return true;
    }

    private static Level Mean(List<Point> points)
    {
        double p = 0, q = 0, pf = 0;
        foreach (var x in points) { p += x.P; q += x.Q; pf += x.Pf; }
        return new Level(p / points.Count, q / points.Count, pf / points.Count);
    }
}
