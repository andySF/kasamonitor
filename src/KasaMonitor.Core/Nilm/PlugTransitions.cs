namespace KasaMonitor.Core.Nilm;

/// <summary>
/// A plug's real change of state: <see cref="TsUtc"/> = the first second of the new state, <see cref="StepW"/> = the
/// level after − the level before (<see cref="PlugEventMatcher.Median"/> of [t + 3 s, t + 20 s] and [t − 20 s, t − 3 s]).
/// </summary>
public readonly record struct PlugTransition(DateTimeOffset TsUtc, bool TurnedOn, double StepW);

/// <summary>
/// A run measured by a plug: on → off (<see cref="PowerW"/> = its median level, <see cref="EstimatedWh"/> = the integral of the
/// plug's power). <see cref="RunEndReason.Timeout"/>: the data stopped while it ran, so its duration is not a measurement.
/// While open, <see cref="PowerW"/> is the level after the switch-on and there is no energy yet.
/// </summary>
public sealed record PlugRun(DateTimeOffset StartUtc, DateTimeOffset? EndUtc, double PowerW, double? EstimatedWh, RunEndReason EndReason = RunEndReason.Paired)
{
    /// <summary>The stored form: no event behind it (<see cref="ApplianceRun.NoEvent"/>).</summary>
    public ApplianceRun ToRun(long id, long signatureId) =>
        new(id, signatureId, StartUtc, EndUtc, PowerW, EstimatedWh, ApplianceRun.NoEvent, null, 0, EndReason);
}

/// <summary>What one second of a plug's timeline settled (<see cref="PlugTimeline.Add"/>).</summary>
public readonly record struct PlugStep(PlugTransition? Transition, PlugRun? Opened, PlugRun? Closed);

/// <summary>
/// The state changes of a plug with a single appliance, read from its own power. Measured on 68 h of a fridge: 240 real
/// transitions, while the main meter's events explain a transition with one candidate in 31 % of the cases
/// (docs/decisions.md, 0.13.2), so the appliance's runs come from here, not from those events.
/// </summary>
public static class PlugTransitions
{
    /// <summary>The plug counts as on from this power up.</summary>
    public const double OnThresholdW = 20;

    /// <summary>A new state must still hold this long after its first second, or it was a pulse.</summary>
    public const int MinHoldSeconds = 5;

    /// <summary>
    /// How long a row still holds here; older than that there is no data. Longer than <see cref="PlugEventMatcher.MaxHold"/>: the
    /// stream writes a row 60 s after the last one, but at its next sample, and that came up to 69 s later (262 of 7,373 intervals
    /// over 68 h of a fridge were of 61–69 s). With 60 s those heartbeats counted as gaps and 62 of 118 runs ended on one,
    /// with 621 Wh a day of the plug's 1,011; with 70 s the only gaps left are the 2 real ones (133 s and 578 s), all 120
    /// runs end on their switch-off and hold 989 Wh a day.
    /// </summary>
    public static readonly TimeSpan MaxRowAge = TimeSpan.FromSeconds(70);

    /// <summary>
    /// A plug's readings on a 1-second grid over [from, to], with <see cref="PlugEventMatcher.Hold"/> (rows must start
    /// <see cref="MaxRowAge"/> before <paramref name="fromUtc"/>): its live stream where that has a value, otherwise
    /// its polls. A second without either is missing from the list: no data.
    /// </summary>
    public static List<PlugPowerPoint> Grid(IReadOnlyList<PlugPowerPoint> stream, IReadOnlyList<PlugPowerPoint> polled, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var held = PlugEventMatcher.Hold(stream, fromUtc, toUtc, MaxRowAge);
        var polls = PlugEventMatcher.Hold(polled, fromUtc, toUtc, MaxRowAge);
        if (polls.Count == 0) return held;
        if (held.Count == 0) return polls;
        var grid = new List<PlugPowerPoint>(Math.Max(held.Count, polls.Count));
        int i = 0, j = 0;
        while (i < held.Count || j < polls.Count)
        {
            if (j >= polls.Count || (i < held.Count && held[i].TsUtc <= polls[j].TsUtc))
            {
                if (j < polls.Count && polls[j].TsUtc == held[i].TsUtc) j++;
                grid.Add(held[i++]);
            }
            else
            {
                grid.Add(polls[j++]);
            }
        }
        return grid;
    }

    /// <summary>The transitions and the runs of a whole series on a 1-second grid (<see cref="Grid"/>).</summary>
    public static (List<PlugTransition> Transitions, List<PlugRun> Runs) Replay(IReadOnlyList<PlugPowerPoint> grid)
    {
        var transitions = new List<PlugTransition>();
        var runs = new List<PlugRun>();
        if (grid.Count == 0) return (transitions, runs);
        var timeline = new PlugTimeline();
        PlugRun? open = null;
        var end = grid[^1].TsUtc + PlugEventMatcher.Outer;
        var next = 0;
        for (var s = grid[0].TsUtc; s <= end; s = s.AddSeconds(1))
        {
            double? watts = null;
            if (next < grid.Count && grid[next].TsUtc == s) watts = grid[next++].PowerW;
            var step = timeline.Add(s, watts);
            if (step.Transition is { } t) transitions.Add(t);
            if (step.Closed is { } closed)
            {
                runs.Add(closed);
                open = null;
            }
            if (step.Opened is { } opened) open = opened;
        }
        if (open is not null) runs.Add(open);
        return (transitions, runs);
    }

    /// <summary>The transitions of a whole series on a 1-second grid.</summary>
    public static IReadOnlyList<PlugTransition> Detect(IReadOnlyList<PlugPowerPoint> grid) => Replay(grid).Transitions;
}

/// <summary>
/// The timeline of one plug, fed second by second without a hole in the seconds (a second without data: null), so a replay of
/// the stored rows and the live tick are the same code. The step of a transition needs the 20 s after it, so a second
/// is settled 20 s after it was fed (<see cref="PlugEventMatcher.Outer"/>).
/// <list type="bullet">
/// <item>No data: no transition across the gap; the state starts again from the first second with data.</item>
/// <item>A run open when the data stops is closed at the last second with data, with the reason
/// <see cref="RunEndReason.Timeout"/> (uncertain, its duration does not count) and the energy measured until then.</item>
/// </list>
/// </summary>
public sealed class PlugTimeline
{
    private const int RingSize = 64;   // more than [c − 20 s, c + 20 s]
    private static readonly long Reach = (long)PlugEventMatcher.Outer.TotalSeconds;
    private static readonly long Inner = (long)PlugEventMatcher.Inner.TotalSeconds;

    private readonly long _ignoreBefore;
    private readonly double?[] _values = new double?[RingSize];
    private readonly long[] _seconds = new long[RingSize];
    private readonly Queue<(long Second, bool On)> _confirmed = new();
    private readonly List<double> _runLevels = new();
    private bool? _on;
    private long? _since;
    private long? _first;
    private long? _runStart;
    private double _runWh;
    private long _lastData;

    /// <param name="ignoreBeforeUtc">Transitions before this time only warm the state up: they are not reported and open no run.</param>
    public PlugTimeline(DateTimeOffset? ignoreBeforeUtc = null)
    {
        _ignoreBefore = ignoreBeforeUtc?.ToUnixTimeSeconds() ?? long.MinValue;
        Array.Fill(_seconds, -1);
    }

    /// <summary>Feeds the next second; the result is about the second fed 20 s ago.</summary>
    public PlugStep Add(DateTimeOffset secondUtc, double? watts)
    {
        var head = secondUtc.ToUnixTimeSeconds();
        _first ??= head;
        var slot = Slot(head);
        _seconds[slot] = head;
        _values[slot] = watts;
        Decide(head, watts);
        var settled = head - Reach;
        return settled < _first ? default : Settle(settled);
    }

    private static int Slot(long second) => (int)(second % RingSize);

    private double? At(long second) => _seconds[Slot(second)] == second ? _values[Slot(second)] : null;

    /// <summary>The state: a change is real when it still holds <see cref="PlugTransitions.MinHoldSeconds"/> after its first second.</summary>
    private void Decide(long second, double? watts)
    {
        if (watts is not { } w)
        {
            _on = null;
            _since = null;
            return;
        }
        var state = w >= PlugTransitions.OnThresholdW;
        if (_on is not { } on)
        {
            _on = state;
            return;
        }
        if (state == on)
        {
            _since = null;
            return;
        }
        _since ??= second;
        if (second - _since < PlugTransitions.MinHoldSeconds) return;
        _confirmed.Enqueue((_since.Value, state));
        _on = state;
        _since = null;
    }

    private double? Level(long from, long to)
    {
        var values = new List<double>((int)(to - from + 1));
        for (var s = from; s <= to; s++)
            if (At(s) is { } w) values.Add(w);
        return values.Count == 0 ? null : PlugEventMatcher.Median(values);
    }

    private PlugStep Settle(long second)
    {
        PlugTransition? transition = null;
        double after = 0;
        if (_confirmed.TryPeek(out var next) && next.Second == second)
        {
            _confirmed.Dequeue();
            if (second >= _ignoreBefore && Level(second - Reach, second - Inner) is { } b && Level(second + Inner, second + Reach) is { } a)
            {
                after = a;
                transition = new PlugTransition(DateTimeOffset.FromUnixTimeSeconds(second), next.On, a - b);
            }
        }

        PlugRun? opened = null, closed = null;
        if (At(second) is not { } watts)
        {
            if (_runStart is not null) closed = CloseRun(_lastData, RunEndReason.Timeout);
            return new PlugStep(transition, null, closed);
        }
        if (transition is { } t)
        {
            if (!t.TurnedOn && _runStart is not null) closed = CloseRun(second, RunEndReason.Paired);
            if (t.TurnedOn)
            {
                if (_runStart is not null) closed = CloseRun(second, RunEndReason.Timeout);
                _runStart = second;
                opened = new PlugRun(t.TsUtc, null, after, null);
            }
        }
        if (_runStart is not null)
        {
            _runWh += watts / 3600.0;
            _runLevels.Add(watts);
        }
        _lastData = second;
        return new PlugStep(transition, opened, closed);
    }

    private PlugRun CloseRun(long endSecond, RunEndReason reason)
    {
        var start = _runStart!.Value;
        var run = new PlugRun(DateTimeOffset.FromUnixTimeSeconds(start), DateTimeOffset.FromUnixTimeSeconds(endSecond),
            _runLevels.Count > 0 ? PlugEventMatcher.Median(_runLevels) : 0, _runWh, reason);
        _runStart = null;
        _runWh = 0;
        _runLevels.Clear();
        return run;
    }
}

/// <summary>
/// An event of the main meter that is only the plug's own step seen again: the single candidate of a transition of a plug with
/// a single appliance. It takes no label and enters no signature, so it forms no run and is no longer subtracted from the
/// rest of the house, where the plug measures the same energy. Not a confirmation: it never feeds the centre of the
/// appliance (<see cref="ExclusiveSignatures"/>). On the real data ≈ 15 % of them are a foreign small step (docs/nilm.md, section 7).
/// </summary>
public static class PlugShadows
{
    /// <summary>|ΔP − the plug's step| at most this.</summary>
    public const double ToleranceW = 15;

    /// <summary>The event lies at most this long before the transition, never after it: measured, the median event comes 3 s earlier than the plug.</summary>
    public static readonly TimeSpan MaxLead = TimeSpan.FromSeconds(5);

    /// <summary>What can be one: an event nobody confirmed (already a shadow: so that a recalculation gives the same result).</summary>
    public static bool CanBe(EventLabelSource source) => source is EventLabelSource.None or EventLabelSource.Model or EventLabelSource.PlugShadow;

    /// <summary>
    /// Event id → the plug it is a shadow of. Every event in the window of a transition, with the same sign and within
    /// the tolerance, is its candidate (a confirmed one too, which then keeps the others out); a transition with one candidate
    /// that <see cref="CanBe"/> a shadow marks it. An event is taken at most once: by the earliest of its transitions.
    /// </summary>
    public static IReadOnlyDictionary<long, Guid> Find(IEnumerable<(Guid PlugId, PlugTransition Transition)> transitions, IEnumerable<MeterEvent> events)
    {
        var sorted = events.OrderBy(e => e.TsUtc).ToList();
        var times = sorted.Select(e => e.TsUtc).ToArray();
        var found = new Dictionary<long, Guid>();
        foreach (var (plug, t) in transitions.OrderBy(x => x.Transition.TsUtc).ThenBy(x => x.PlugId))
        {
            var i = Array.BinarySearch(times, t.TsUtc - MaxLead);
            if (i < 0) i = ~i;
            else while (i > 0 && times[i - 1] == times[i]) i--;
            MeterEvent? only = null;
            var candidates = 0;
            for (; i < sorted.Count && times[i] <= t.TsUtc; i++)
            {
                var e = sorted[i];
                if (e.DeltaP > 0 != t.StepW > 0 || Math.Abs(e.DeltaP - t.StepW) > ToleranceW) continue;
                candidates++;
                only = e;
            }
            if (candidates == 1 && CanBe(only!.Source)) found.TryAdd(only.Id, plug);
        }
        return found;
    }
}
