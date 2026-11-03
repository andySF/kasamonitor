namespace KasaMonitor.Core.Nilm;

/// <summary>What one event did to the runs: the run it opened (if any) and the runs it closed.</summary>
public sealed record RunChange(ApplianceRun? Opened, IReadOnlyList<ApplianceRun> Closed)
{
    public static readonly RunChange None = new(null, Array.Empty<ApplianceRun>());
}

/// <summary>
/// Runs of the appliances:
/// <list type="number">
/// <item>a switch-on (+ΔP) with a signature opens a run;</item>
/// <item>a switch-off (−ΔP) closes the oldest open run of the same signature whose magnitude is within the clustering
/// tolerance (<see cref="SignatureClusterer.Eps"/>), first in first out (<see cref="RunEndReason.Paired"/>);</item>
/// <item>any switch-off that brings the house to its level before a run's switch-on (within the detector's tolerance)
/// also closes that run: whatever turned off, that appliance cannot still be on (<see cref="RunEndReason.HouseBelowLevel"/>;
/// a simple heuristic, wrong when something else turned on in between and off again);</item>
/// <item>a run open longer than <see cref="MaxOpen"/> is closed as uncertain (<see cref="RunEndReason.Timeout"/>) with
/// <b>no</b> energy (EstimatedWh = 0): its switch-off was never seen, so its duration is unknown.</item>
/// </list>
/// Energy = ΔP_on × duration / 3600, optionally corrected by <see cref="ApplianceEnergy.Corrected"/>.
/// </summary>
public sealed class ApplianceRunBuilder
{
    public static readonly TimeSpan MaxOpen = TimeSpan.FromHours(6);

    private readonly List<ApplianceRun> _open = new();

    public ApplianceRunBuilder(IEnumerable<ApplianceRun>? openRuns = null)
    {
        if (openRuns is not null) _open.AddRange(openRuns.Where(r => r.IsOpen).OrderBy(r => r.StartUtc));
    }

    /// <summary>
    /// A run that counts as running at <paramref name="nowUtc"/>: open and at most <see cref="MaxOpen"/> old. „Acum
    /// pornite” tests it too, so a run past the limit never shows as running even before <see cref="Expire"/> closed it
    /// (the summary is also refreshed by the page's actions and while there is no main meter, between two expiries).
    /// </summary>
    public static bool IsRunning(ApplianceRun run, DateTimeOffset nowUtc) => run.IsOpen && nowUtc - run.StartUtc <= MaxOpen;

    /// <summary>Open runs (not yet expired).</summary>
    public IReadOnlyList<ApplianceRun> Open() => _open.ToList();

    /// <summary>Closes the runs open longer than <see cref="MaxOpen"/> at <paramref name="nowUtc"/> (uncertain, no energy).</summary>
    public IReadOnlyList<ApplianceRun> Expire(DateTimeOffset nowUtc)
    {
        var expired = _open.Where(r => nowUtc - r.StartUtc > MaxOpen).ToList();
        foreach (var r in expired) _open.Remove(r);
        return expired.Select(r => r with
        {
            EndUtc = r.StartUtc + MaxOpen,
            EstimatedWh = 0,
            EndReason = RunEndReason.Timeout,
        }).ToList();
    }

    /// <summary>Feeds one stored event (events in time order). Returns the run opened and the runs closed.</summary>
    public RunChange Add(MeterEvent e)
    {
        var closed = new List<ApplianceRun>(Expire(e.TsUtc));
        ApplianceRun? opened = null;
        if (e.DeltaP > 0)
        {
            if (e.SignatureId is { } sig)
            {
                opened = new ApplianceRun(0, sig, e.TsUtc, null, e.DeltaP, null, e.Id, null, e.PowerBeforeW);
                _open.Add(opened);
            }
            return new RunChange(opened, closed);
        }

        var off = Math.Abs(e.DeltaP);
        if (e.SignatureId is { } s
            && _open.FirstOrDefault(r => r.SignatureId == s && r.StartUtc <= e.TsUtc
                                         && Math.Abs(r.PowerW - off) <= SignatureClusterer.Eps(r.PowerW, off)) is { } match)
        {
            _open.Remove(match);
            closed.Add(Close(match, e, RunEndReason.Paired));
        }

        // The house fell back to (or below) the level it had before a run started: that appliance is off too.
        var houseAfter = e.PowerBeforeW + e.DeltaP;
        foreach (var r in _open.Where(r => r.StartUtc <= e.TsUtc
                                           && houseAfter <= r.HouseBeforeW + MeterEventDetector.Tolerance(r.HouseBeforeW)).ToList())
        {
            _open.Remove(r);
            closed.Add(Close(r, e, RunEndReason.HouseBelowLevel));
        }
        return new RunChange(null, closed);
    }

    private static ApplianceRun Close(ApplianceRun r, MeterEvent e, RunEndReason reason) => r with
    {
        EndUtc = e.TsUtc,
        EndEventId = e.Id,
        EndReason = reason,
        EstimatedWh = ApplianceEnergy.Nominal(r.PowerW, e.TsUtc - r.StartUtc),
    };

    /// <summary>Replaces an open run by its stored copy (with the database id) so a later switch-off closes the stored row.</summary>
    public void Track(ApplianceRun stored)
    {
        var i = _open.FindIndex(r => r.StartEventId == stored.StartEventId && r.SignatureId == stored.SignatureId);
        if (i >= 0) _open[i] = stored;
    }
}

/// <summary>Energy of runs, per day, and the "unidentified" rest of the house.</summary>
public static class ApplianceEnergy
{
    public static double Nominal(double powerW, TimeSpan duration) => powerW * duration.TotalSeconds / 3600.0;

    /// <summary>
    /// The correction of a run when it was the only detected appliance active (no other event during it): what the house
    /// used above its level before the switch-on, (mean house W − W before) × duration. Kept only when it is between half
    /// and twice the nominal energy (otherwise something else changed and the nominal value is safer).
    /// </summary>
    public static double Corrected(double nominalWh, double houseMeanW, double powerBeforeW, TimeSpan duration)
    {
        var corrected = (houseMeanW - powerBeforeW) * duration.TotalSeconds / 3600.0;
        return corrected >= 0.5 * nominalWh && corrected <= 2 * nominalWh ? corrected : nominalWh;
    }

    /// <summary>
    /// Energy of a run inside [fromUtc, toUtc): a closed run proportionally to the overlap; an open run as power × time
    /// up to <paramref name="nowUtc"/>, never beyond <see cref="ApplianceRunBuilder.MaxOpen"/>.
    /// </summary>
    public static double InRange(ApplianceRun run, DateTimeOffset fromUtc, DateTimeOffset toUtc, DateTimeOffset nowUtc)
    {
        if (run.EndUtc is { } end)
        {
            var total = (end - run.StartUtc).TotalSeconds;
            if (total <= 0 || run.EstimatedWh is not { } wh) return 0;
            var overlap = Overlap(run.StartUtc, end, fromUtc, toUtc);
            return wh * overlap / total;
        }
        var until = nowUtc - run.StartUtc > ApplianceRunBuilder.MaxOpen ? run.StartUtc + ApplianceRunBuilder.MaxOpen : nowUtc;
        return run.PowerW * Overlap(run.StartUtc, until, fromUtc, toUtc) / 3600.0;
    }

    private static double Overlap(DateTimeOffset a0, DateTimeOffset a1, DateTimeOffset b0, DateTimeOffset b1)
    {
        var s = a0 > b0 ? a0 : b0;
        var e = a1 < b1 ? a1 : b1;
        return e > s ? (e - s).TotalSeconds : 0;
    }

    /// <summary>
    /// "Neidentificat" = house − Σ plugs − Σ runs of the detected appliances (signatures not measured by a plug: those are
    /// already in Σ plugs). Never below 0 (the run estimates are approximate).
    /// </summary>
    public static double Unidentified(double houseWh, double plugsWh, double unconfirmedRunsWh) =>
        Math.Max(0, houseWh - plugsWh - unconfirmedRunsWh);

    /// <summary>
    /// The plugs' energy over the part of the day the house was measured: the plugs' day counters cover the whole local
    /// day, the house only the minutes the live stream ran (first day, outages), so Σ plugs is scaled by the covered share.
    /// </summary>
    public static double PlugsOverCoveredTime(double plugsTodayWh, double coveredSeconds, double secondsSinceMidnight) =>
        secondsSinceMidnight <= 0 ? 0 : plugsTodayWh * Math.Clamp(coveredSeconds / secondsSinceMidnight, 0, 1);
}
