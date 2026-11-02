namespace KasaMonitor.Core.Heating;

/// <summary>What a burner run was for. The Viessmann API has no explicit signal (no <c>heating.dhw.charging</c> on a Vitodens 050-W), so it is inferred.</summary>
public enum BurnerPurpose
{
    Off,
    Heating,
    Dhw,
    Unknown,
}

/// <summary>
/// One burner run (or part of one, when a run is split between heating and hot water) built from the readings.
/// <see cref="StartUtc"/> = the first reading with the burner on; <see cref="EndUtc"/> = the next reading (burner off),
/// so each reading holds until the next one, as in a Grafana state timeline. <see cref="Ongoing"/>: the last reading
/// still has the burner on (<see cref="EndUtc"/> = that reading). Gas = the boiler's "today" counters' increase attributed
/// to the run (from the reading before it to at most 60 minutes after it, never into the next run), in m³; null when the
/// counters are missing.
/// </summary>
public sealed record BurnerSegment(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    BurnerPurpose Purpose,
    string Reason,
    double? AvgModulationPct,
    double? GasHeatingM3,
    double? GasDhwM3,
    int Readings,
    bool Ongoing)
{
    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>
/// Burner timeline: contiguous runs of <c>BurnerActive = true</c> and their purpose (heating vs hot water), by this rule,
/// in this order, on each run (a missing reading of at most 5 minutes beyond the usual cadence does not break a run):
/// <list type="number">
/// <item>(a) the boiler's gas counters, which the boiler updates late (measured: +0.6 m³ of heating 20 minutes after a
/// run), from the reading before the run to the last reading at most 60 minutes after the run's end, but never past the
/// reading before the next run: only <c>GasDhwTodayM3</c> grew → hot water; only <c>GasHeatingTodayM3</c> grew → heating;
/// both grew → the run is split in proportion to the increases when longer than 10 minutes (heating first, then hot
/// water), otherwise unknown;</item>
/// <item>(b) only when (a) did not decide (no counter grew, resolution 0.1 m³), for a combi boiler whose hot water
/// sensor follows the supply while the burner runs (rise = highest value in the run − the reading before it):
/// hot water when the hot water rose and ΔDhw − ΔSupply ≥ 2 °C, or when the supply did not rise (ΔSupply &lt; 1 °C) and
/// ΔDhw ≥ 2 °C; heating when ΔSupply ≥ 2 °C and ΔDhw ≤ ΔSupply; otherwise unknown. A missing temperature counts as no rise.</item>
/// </list>
/// Pure: no clock, no I/O. Readings with an unknown burner state are ignored (treated as missing).
/// </summary>
public static class BurnerTimeline
{
    /// <summary>A missing reading of at most this long (beyond the cadence) does not break a run.</summary>
    public static readonly TimeSpan MissingTolerance = TimeSpan.FromMinutes(5);

    /// <summary>A run where both counters grew is split only when longer than this; shorter = unknown.</summary>
    public static readonly TimeSpan SplitMinDuration = TimeSpan.FromMinutes(10);

    /// <summary>How long after a run's end the boiler's counters are still attributed to it (they are updated late).</summary>
    public static readonly TimeSpan CounterLookahead = TimeSpan.FromMinutes(60);

    /// <summary>Rule (b): how much more the hot water must rise than the supply (°C) for hot water.</summary>
    public const double DhwOverSupplyC = 2.0;

    /// <summary>Rule (b): below this rise (°C) the supply "did not rise".</summary>
    public const double SupplyFlatC = 1.0;

    /// <summary>Rule (b): rise (°C) of the hot water (with a flat supply) or of the supply that marks hot water / heating.</summary>
    public const double RiseC = 2.0;

    /// <summary>The counters have a 0.1 m³ resolution: anything below half of it is noise.</summary>
    private const double CounterStep = 0.05;

    private static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(ViessmannApi.DefaultPollIntervalSeconds);

    /// <summary>The metric label of a purpose (<c>heating_burner_purpose_info{purpose}</c>).</summary>
    public static string Label(BurnerPurpose purpose) => purpose switch
    {
        BurnerPurpose.Heating => "heating",
        BurnerPurpose.Dhw => "dhw",
        BurnerPurpose.Unknown => "unknown",
        _ => "off",
    };

    /// <summary>The usual time between readings: the median gap (at least 60 s; 120 s with fewer than two readings).</summary>
    public static TimeSpan Cadence(IReadOnlyList<HeatingReading> sorted)
    {
        if (sorted.Count < 2) return DefaultCadence;
        var gaps = new List<double>(sorted.Count - 1);
        for (var i = 1; i < sorted.Count; i++) gaps.Add((sorted[i].TsUtc - sorted[i - 1].TsUtc).TotalSeconds);
        gaps.Sort();
        var median = gaps[gaps.Count / 2];
        return TimeSpan.FromSeconds(Math.Max(60, median));
    }

    /// <summary>The longest gap between two readings that still belong to the same run.</summary>
    public static TimeSpan MaxGap(TimeSpan cadence) => cadence + MissingTolerance;

    /// <summary>The burner runs of the readings (any order), oldest first, each with its purpose.</summary>
    public static IReadOnlyList<BurnerSegment> Build(IEnumerable<HeatingReading> readings)
    {
        var r = readings.Where(x => x.BurnerActive is not null).OrderBy(x => x.TsUtc).ToList();
        var segments = new List<BurnerSegment>();
        if (r.Count == 0) return segments;
        var cadence = Cadence(r);
        var maxGap = MaxGap(cadence);

        // The runs first (first and last reading), then each one with its counter window, which stops before the next run.
        var runs = new List<(int First, int Last)>();
        for (var i = 0; i < r.Count; i++)
        {
            if (r[i].BurnerActive != true) continue;
            var j = i;
            while (j + 1 < r.Count && r[j + 1].BurnerActive == true && r[j + 1].TsUtc - r[j].TsUtc <= maxGap) j++;
            runs.Add((i, j));
            i = j;
        }

        for (var n = 0; n < runs.Count; n++)
        {
            var (i, j) = runs[n];
            var prev = i > 0 && r[i].TsUtc - r[i - 1].TsUtc <= maxGap ? r[i - 1] : null;
            var next = j + 1 < r.Count && r[j + 1].TsUtc - r[j].TsUtc <= maxGap ? r[j + 1] : null;
            var ongoing = j == r.Count - 1;
            var start = r[i].TsUtc;
            var end = next?.TsUtc ?? (ongoing ? r[j].TsUtc : r[j].TsUtc + cadence);

            // Counters: from the reading before the run to the last reading ≤ end + 60 min, never past the reading before the
            // next run (each increase belongs to one run only).
            var counterFirst = prev is null ? i : i - 1;
            var stop = n + 1 < runs.Count ? runs[n + 1].First - 1 : r.Count - 1;
            var counterLast = j;
            while (counterLast + 1 <= stop && r[counterLast + 1].TsUtc <= end + CounterLookahead) counterLast++;
            segments.AddRange(Classify(r, i, j, prev, counterFirst, counterLast, start, end, ongoing));
        }
        return segments;
    }

    /// <summary>
    /// The purpose at the latest reading: <see cref="BurnerPurpose.Off"/> when the burner is off, otherwise the purpose of
    /// the current run as known so far (same rule, on the readings of the run and the one before it); null without a
    /// reading with a known burner state.
    /// </summary>
    public static BurnerPurpose? Current(IEnumerable<HeatingReading> readings)
    {
        var r = readings.Where(x => x.BurnerActive is not null).OrderBy(x => x.TsUtc).ToList();
        if (r.Count == 0) return null;
        if (r[^1].BurnerActive != true) return BurnerPurpose.Off;
        var segments = Build(r);
        return segments.Count == 0 ? BurnerPurpose.Unknown : segments[^1].Purpose;
    }

    private static IEnumerable<BurnerSegment> Classify(List<HeatingReading> r, int i, int j, HeatingReading? prev, int counterFirst,
        int counterLast, DateTimeOffset start, DateTimeOffset end, bool ongoing)
    {
        var gasHeating = Increase(r, counterFirst, counterLast, x => x.GasHeatingTodayM3);
        var gasDhw = Increase(r, counterFirst, counterLast, x => x.GasDhwTodayM3);
        var run = r.GetRange(i, j - i + 1);
        var mods = run.Where(x => x.ModulationPct is not null).Select(x => x.ModulationPct!.Value).ToList();
        double? avgMod = mods.Count > 0 ? mods.Average() : null;
        var count = run.Count;

        var heatingUp = gasHeating is >= CounterStep;
        var dhwUp = gasDhw is >= CounterStep;
        if (dhwUp && !heatingUp)
        {
            yield return new(start, end, BurnerPurpose.Dhw, $"contorul de apă caldă +{gasDhw:0.0} m³ (până la 60 min după)", avgMod, gasHeating, gasDhw, count, ongoing);
            yield break;
        }
        if (heatingUp && !dhwUp)
        {
            yield return new(start, end, BurnerPurpose.Heating, $"contorul de încălzire +{gasHeating:0.0} m³ (până la 60 min după)", avgMod, gasHeating, gasDhw, count, ongoing);
            yield break;
        }
        if (heatingUp && dhwUp)
        {
            if (end - start > SplitMinDuration)
            {
                var share = gasHeating!.Value / (gasHeating.Value + gasDhw!.Value);
                var cut = start + TimeSpan.FromSeconds(Math.Round((end - start).TotalSeconds * share));
                var why = $"ambele contoare au crescut (încălzire +{gasHeating:0.0}, apă caldă +{gasDhw:0.0} m³), interval împărțit proporțional";
                yield return new(start, cut, BurnerPurpose.Heating, why, avgMod, gasHeating, 0, count, false);
                yield return new(cut, end, BurnerPurpose.Dhw, why, avgMod, 0, gasDhw, count, ongoing);
            }
            else
            {
                yield return new(start, end, BurnerPurpose.Unknown, "ambele contoare au crescut, interval prea scurt pentru împărțire",
                    avgMod, gasHeating, gasDhw, count, ongoing);
            }
            yield break;
        }

        var baseline = prev ?? r[i];
        var dDhw = Rise(run, baseline, x => x.DhwTempC) ?? 0;
        var dSupply = Rise(run, baseline, x => x.SupplyTempC) ?? 0;
        var temps = $"apa caldă {dDhw:+0.0;-0.0;0} °C, turul {dSupply:+0.0;-0.0;0} °C";
        if ((dDhw > 0 && dDhw - dSupply >= DhwOverSupplyC) || (dSupply < SupplyFlatC && dDhw >= RiseC))
            yield return new(start, end, BurnerPurpose.Dhw, "contoarele nu au crescut; " + temps, avgMod, gasHeating, gasDhw, count, ongoing);
        else if (dSupply >= RiseC && dDhw <= dSupply)
            yield return new(start, end, BurnerPurpose.Heating, "contoarele nu au crescut; " + temps, avgMod, gasHeating, gasDhw, count, ongoing);
        else
            yield return new(start, end, BurnerPurpose.Unknown, "contoarele nu au crescut, temperaturile nu decid; " + temps, avgMod, gasHeating, gasDhw, count, ongoing);
    }

    /// <summary>
    /// Σ of the positive steps of a "today" counter between consecutive known values in r[first..last] (a drop = the daily
    /// reset, not counted); null when fewer than two values are known.
    /// </summary>
    private static double? Increase(List<HeatingReading> r, int first, int last, Func<HeatingReading, double?> value)
    {
        double? previous = null;
        double? sum = null;
        for (var k = first; k <= last; k++)
        {
            if (value(r[k]) is not { } v) continue;
            if (previous is { } p) sum = (sum ?? 0) + Math.Max(0, v - p);
            previous = v;
        }
        return sum;
    }

    /// <summary>Highest value in the run minus the baseline's (the reading before the run, else the run's first); null when unknown.</summary>
    private static double? Rise(List<HeatingReading> run, HeatingReading baseline, Func<HeatingReading, double?> value)
    {
        var start = value(baseline) ?? run.Select(value).FirstOrDefault(v => v is not null);
        var values = run.Select(value).Where(v => v is not null).Select(v => v!.Value).ToList();
        if (start is null || values.Count == 0) return null;
        return values.Max() - start.Value;
    }
}
