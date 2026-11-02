namespace KasaMonitor.Core.Miele;

/// <summary>What one observation changed in the cycle of an appliance.</summary>
public sealed record MieleCycleChange(MieleCycle? Opened, MieleCycle? Updated, MieleCycle? Closed)
{
    public static readonly MieleCycleChange None = new(null, null, null);
    public bool Any => Opened is not null || Updated is not null || Closed is not null;
}

/// <summary>
/// The cycle state machine of one appliance. A cycle opens when the status turns Running (5) and closes at End
/// programmed (7), Idle (10) or Off (1), or after <see cref="MieleApi.CycleTimeout"/> without Running (pause, failure,
/// interrupted, not connected …; then it ends at the last time it was seen Running). While open it records the phases
/// (a span per change of <c>programPhase</c>) and the highest <c>ecoFeedback</c> consumption (cumulative within the
/// program, reset when it ends). <see cref="MieleCycleChange.Updated"/> is reported when the phases, program or the
/// consumption changed (so the stored row follows), not for every observation.
/// </summary>
public sealed class MieleCycleTracker
{
    private readonly string _deviceId;
    private MieleCycle? _open;

    /// <param name="open">The open cycle stored before a restart (continued, not opened again).</param>
    public MieleCycleTracker(string deviceId, MieleCycle? open = null)
    {
        _deviceId = deviceId;
        _open = open is { IsOpen: true } ? open : null;
    }

    public MieleCycle? Open => _open;

    /// <summary>The stored id of a cycle opened by <see cref="Observe"/> (after the insert).</summary>
    public void SetId(long id)
    {
        if (_open is { } o) _open = o with { Id = id };
    }

    public MieleCycleChange Observe(DateTimeOffset tsUtc, MieleStateValues v)
    {
        var status = v.StatusRaw;
        if (_open is null)
        {
            if (status != MieleTexts.StatusRunning) return MieleCycleChange.None;
            var phases = v.PhaseText is { } pt ? new[] { new MielePhaseSpan(v.PhaseRaw, pt, tsUtc, null) } : Array.Empty<MielePhaseSpan>();
            _open = new MieleCycle(0, _deviceId, tsUtc, null, tsUtc, v.ProgramIdRaw, v.ProgramIdText, phases,
                Positive(v.EcoEnergyKwh), Positive(v.EcoWaterL), null, null);
            return new MieleCycleChange(_open, null, null);
        }

        var c = _open;
        var changed = false;
        if (status == MieleTexts.StatusRunning) c = c with { LastRunningUtc = tsUtc };
        if (c.ProgramRaw is null && v.ProgramIdRaw is not null || c.Program is null && v.ProgramIdText is not null)
        {
            c = c with { ProgramRaw = c.ProgramRaw ?? v.ProgramIdRaw, Program = c.Program ?? v.ProgramIdText };
            changed = true;
        }
        if (v.PhaseText is { } text && (c.Phases.Count == 0 || c.Phases[^1].PhaseRaw != v.PhaseRaw || c.Phases[^1].PhaseText != text))
        {
            var spans = c.Phases.ToList();
            if (spans.Count > 0) spans[^1] = spans[^1] with { ToUtc = tsUtc };
            spans.Add(new MielePhaseSpan(v.PhaseRaw, text, tsUtc, null));
            c = c with { Phases = spans };
            changed = true;
        }
        var energy = Max(c.EnergyKwh, Positive(v.EcoEnergyKwh));
        var water = Max(c.WaterL, Positive(v.EcoWaterL));
        if (energy != c.EnergyKwh || water != c.WaterL)
        {
            c = c with { EnergyKwh = energy, WaterL = water };
            changed = true;
        }

        var reason = status switch
        {
            MieleTexts.StatusEndProgrammed => MieleEndReasons.End,
            MieleTexts.StatusIdle => MieleEndReasons.Idle,
            MieleTexts.StatusOff => MieleEndReasons.Off,
            _ => null,
        };
        if (reason is not null) return Close(c, tsUtc, reason);
        if (tsUtc - c.LastRunningUtc >= MieleApi.CycleTimeout) return Close(c, c.LastRunningUtc, MieleEndReasons.Timeout);
        _open = c;
        return changed ? new MieleCycleChange(null, c, null) : MieleCycleChange.None;
    }

    /// <summary>Without an observation (e.g. the appliance went silent): closes a cycle not Running for 30 minutes.</summary>
    public MieleCycleChange Tick(DateTimeOffset nowUtc)
    {
        if (_open is not { } c || nowUtc - c.LastRunningUtc < MieleApi.CycleTimeout) return MieleCycleChange.None;
        return Close(c, c.LastRunningUtc, MieleEndReasons.Timeout);
    }

    private MieleCycleChange Close(MieleCycle c, DateTimeOffset endUtc, string reason)
    {
        if (endUtc < c.StartUtc) endUtc = c.StartUtc;
        var spans = c.Phases.ToList();
        if (spans.Count > 0 && spans[^1].ToUtc is null) spans[^1] = spans[^1] with { ToUtc = endUtc < spans[^1].FromUtc ? spans[^1].FromUtc : endUtc };
        var closed = c with { EndUtc = endUtc, EndReason = reason, Phases = spans };
        _open = null;
        return new MieleCycleChange(null, null, closed);
    }

    private static double? Positive(double? v) => v is > 0 ? v : null;

    private static double? Max(double? a, double? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}

/// <summary>Whether a state is stored: on every change of a compared value, else at most every <see cref="MieleApi.SnapshotHeartbeat"/>.</summary>
public static class MieleSnapshotRule
{
    public static bool ShouldWrite(MieleStateSnapshot? previous, MieleStateValues current, DateTimeOffset nowUtc) =>
        previous is null || previous.Values != current || nowUtc - previous.TsUtc >= MieleApi.SnapshotHeartbeat;
}
