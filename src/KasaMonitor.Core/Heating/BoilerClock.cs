using KasaMonitor.Core.Protocol.Shelly;

namespace KasaMonitor.Core.Heating;

/// <summary>How a gas "currentDay" value is written into the daily history (<see cref="BoilerClock.Classify"/>).</summary>
public enum GasDayAction
{
    /// <summary>Not lower than the stored highest: the usual upsert (highest kept, last = received).</summary>
    Upsert,
    /// <summary>The boiler's daily reset: <see cref="GasDayDecision.TargetDay"/> gets highest = last = received.</summary>
    Reset,
    /// <summary>A drop that is not the daily reset: the day is left as it is (highest and last), logged once a day.</summary>
    IgnoredDecrease,
}

/// <param name="TargetDay">The boiler day the value is written to (the next day for a reset seen just before midnight).</param>
/// <param name="CarryToPreviousDay">A reset after midnight: the stored highest was the previous day's value and is carried there.</param>
public sealed record GasDayDecision(GasDayAction Action, DateOnly TargetDay, bool CarryToPreviousDay);

/// <summary>
/// A gas day whose highest value still holds the previous day's total (seen in 0.8.0: readings between 00:00 and 01:00
/// Romanian time written on the new day before the boiler's reset at 00:00 CET). <see cref="NewPreviousMaxM3"/> /
/// <see cref="NewPreviousLastM3"/> go to the previous day, the current day gets highest = its last value.
/// </summary>
public sealed record GasDayRepair(
    string Purpose,
    DateOnly Day,
    double CurrentMaxM3,
    double CurrentLastM3,
    double? PreviousMaxM3,
    double? PreviousLastM3,
    double NewPreviousMaxM3,
    double NewPreviousLastM3);

/// <summary>
/// Heat produced or boiler electricity (<see cref="HeatingKinds.Heat"/> / <see cref="HeatingKinds.Power"/>) of a day that
/// still held the previous day's total: the previous day gets <see cref="NewPreviousKwh"/>, the day's value is cleared.
/// </summary>
public sealed record KwhDayRepair(string Kind, string Purpose, DateOnly Day, double CurrentKwh, double PreviousKwh, double NewPreviousKwh);

/// <summary>
/// The boiler's own day. Viessmann resets the "currentDay" counters at midnight in the boiler's time zone (Central
/// European Time: 00:00 CET/CEST = 01:00 Romanian time all year), so every daily attribution of the boiler's gas,
/// electricity and heat uses that zone (setting <c>BoilerTimeZone</c>, default <see cref="DefaultTimeZoneId"/>), not the
/// application's <c>TZ</c>. Also the single owner of the daily reset guard and of the repair of a leaked day.
/// </summary>
public static class BoilerClock
{
    public const string DefaultTimeZoneId = "Europe/Berlin";

    private static readonly Lazy<TimeZoneInfo> Default = new(() => TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId));

    /// <summary>Europe/Berlin (CET/CEST).</summary>
    public static TimeZoneInfo DefaultTimeZone => Default.Value;

    /// <summary>The zone of an IANA (or Windows) id; false when the system does not know it.</summary>
    public static bool TryResolve(string? id, out TimeZoneInfo tz)
    {
        tz = DefaultTimeZone;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>The zone of <paramref name="id"/>, or Europe/Berlin when empty or unknown.</summary>
    public static TimeZoneInfo Resolve(string? id) => TryResolve(id, out var tz) ? tz : DefaultTimeZone;

    /// <summary>The boiler day of an instant.</summary>
    public static DateOnly DayOf(DateTimeOffset utc, TimeZoneInfo boilerTz) => HeatingEnergy.DayOf(utc, boilerTz);

    /// <summary>UTC instant of the boiler's midnight that starts <paramref name="day"/>.</summary>
    public static DateTimeOffset MidnightUtc(DateOnly day, TimeZoneInfo boilerTz) => ShellyEnergyData.LocalMidnightUtc(day, boilerTz);

    // ---- stale value right after the boiler's midnight ----

    /// <summary>
    /// A value read this long after the boiler's midnight may still be the previous day's total (the same 30 minutes as
    /// the end of the reset window, <see cref="ResetWindowTo"/>)…
    /// </summary>
    public static readonly TimeSpan StaleAfterMidnight = TimeSpan.FromMinutes(30);

    /// <summary>…when it is at least this share of the previous day's highest (or not lower than its last value)…</summary>
    public const double StaleMinShare = 0.9;

    /// <summary>…and the previous day used at least this much (m³ of gas): after a day of ~0 nothing is stale.</summary>
    public const double StaleMinPreviousM3 = 0.05;

    /// <summary>The same threshold for the heat produced (kWh, about the energy of 0.05 m³ of gas).</summary>
    public const double StaleMinPreviousHeatKwh = 0.5;

    /// <summary>The same threshold for the boiler's own electricity (kWh; a day is a few tenths of a kWh).</summary>
    public const double StaleMinPreviousPowerKwh = 0.05;

    /// <summary>
    /// True when a "today" value read at <paramref name="readUtc"/> is still the previous boiler day's total: read within
    /// 30 minutes after the boiler's midnight, the previous day's highest is at least <paramref name="minPrevious"/>, and
    /// the value is ≥ 90 % of that highest or not lower than the previous day's last value. Such a value belongs to the
    /// previous day (it may raise its highest / last) and the new day gets nothing until the first lower value. False
    /// without a previous day, or after a day of about zero (a new small value is then today's, never doubled).
    /// </summary>
    public static bool IsStale(double value, double? previousMax, double? previousLast, DateTimeOffset readUtc, TimeZoneInfo boilerTz,
        double minPrevious = StaleMinPreviousM3)
    {
        if (!InStaleWindow(readUtc, boilerTz)) return false;
        if (previousMax is not { } max || max < minPrevious) return false;
        return value >= StaleMinShare * max || (previousLast is { } last && value >= last);
    }

    /// <summary>True during the first 30 minutes of a boiler day (inclusive).</summary>
    public static bool InStaleWindow(DateTimeOffset readUtc, TimeZoneInfo boilerTz)
    {
        var since = readUtc - MidnightUtc(DayOf(readUtc, boilerTz), boilerTz);
        return since >= TimeSpan.Zero && since <= StaleAfterMidnight;
    }

    // ---- reset guard ----

    /// <summary>A drop is a reset when the boiler's time is between these (around its midnight, inclusive)…</summary>
    public static readonly TimeOnly ResetWindowFrom = new(23, 45), ResetWindowTo = new(0, 30);

    /// <summary>…or, for the hourly profile only, when the new value is under this share of the previous one…</summary>
    public const double ResetMaxShare = 0.2;

    /// <summary>…and no reset was accepted during this long before.</summary>
    public static readonly TimeSpan ResetMinGap = TimeSpan.FromHours(20);

    /// <summary>True when the boiler's time of <paramref name="utc"/> is between 23:45 and 00:30.</summary>
    public static bool InResetWindow(DateTimeOffset utc, TimeZoneInfo boilerTz)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, boilerTz).DateTime);
        return local >= ResetWindowFrom || local <= ResetWindowTo;
    }

    /// <summary>
    /// The reset rule of the hourly profile: a drop from <paramref name="previous"/> to <paramref name="value"/> at
    /// <paramref name="utc"/> is a reset when the boiler's time is between 23:45 and 00:30, or the new value is under 20 %
    /// of the previous one and no reset was accepted in the 20 hours before (<paramref name="lastResetUtc"/>). The daily
    /// rows accept only the first case (<see cref="Classify"/>): a reset during the day never rewrites the day.
    /// </summary>
    public static bool IsReset(double previous, double value, DateTimeOffset utc, TimeZoneInfo boilerTz, DateTimeOffset? lastResetUtc)
    {
        if (!(value < previous)) return false;
        if (InResetWindow(utc, boilerTz)) return true;
        return value < ResetMaxShare * previous && (lastResetUtc is not { } last || utc - last >= ResetMinGap);
    }

    /// <summary>
    /// Where a gas "currentDay" value read at <paramref name="readUtc"/> goes. <paramref name="day"/> = the boiler day of
    /// the reading, <paramref name="storedMaxM3"/> = the highest stored for that day. A drop is the daily reset only in the
    /// window 23:45–00:30 (boiler time): seen before the boiler's midnight it belongs to the next day; seen just after it
    /// it carries the stored highest (the previous day's total read after midnight) to the previous day. Any other drop,
    /// a reset under 20 % during the day included, leaves the day as it is (<see cref="GasDayAction.IgnoredDecrease"/>):
    /// the day's value and the <c>_total</c> counter never go down, they wait until the boiler's value passes them again.
    /// </summary>
    public static GasDayDecision Classify(double receivedM3, double? storedMaxM3, DateOnly day, DateTimeOffset readUtc, TimeZoneInfo boilerTz)
    {
        if (storedMaxM3 is not { } stored || receivedM3 >= stored) return new(GasDayAction.Upsert, day, false);
        if (!InResetWindow(readUtc, boilerTz)) return new(GasDayAction.IgnoredDecrease, day, false);
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(readUtc, boilerTz).DateTime);
        if (local >= ResetWindowFrom) return new(GasDayAction.Reset, day.AddDays(1), false);
        return new(GasDayAction.Reset, day, true);
    }

    /// <summary>
    /// The previous day's highest and last value after carrying <paramref name="valueM3"/> (a total read after midnight)
    /// there: highest = max(its highest, value); last = the same when its last is lower.
    /// </summary>
    public static (double MaxM3, double LastM3) CarryToPrevious(double? previousMaxM3, double? previousLastM3, double valueM3)
    {
        var max = previousMaxM3 is { } pm && pm > valueM3 ? pm : valueM3;
        var last = previousLastM3 is { } pl && pl >= max ? pl : max;
        return (max, last);
    }

    // ---- repair of a day that holds the previous day's total ----

    /// <summary>The repair looks at the current boiler day only this long after its midnight.</summary>
    public static readonly TimeSpan RepairAfterMidnight = TimeSpan.FromMinutes(30);

    /// <summary>The current day's last value at most this share of its highest (exactly half included)…</summary>
    public const double RepairMaxShare = 0.5;

    /// <summary>…or its highest equal to the previous day's highest within max(0.05, 1 %): it is that total.</summary>
    public static double SameTotalTolerance(double previous) => Math.Max(0.05, 0.01 * Math.Abs(previous));

    /// <summary>
    /// The gas purposes of the current boiler day whose highest value is the previous day's total: more than 30 minutes
    /// after the boiler's midnight, highest &gt; last, and either last ≤ 50 % of highest (the measured case 1.4 / 0.7) or
    /// the highest equals the previous day's highest within <see cref="SameTotalTolerance"/> (the case 1.4 / 0.8 at
    /// the deployment). The previous day gets highest = max(its highest, the current highest) and the same for its last
    /// value when lower; the current day gets highest = last. Empty when there is nothing to repair.
    /// </summary>
    public static IReadOnlyList<GasDayRepair> RepairCurrentDay(HeatingEnergyDay? previous, HeatingEnergyDay? current, DateTimeOffset nowUtc, TimeZoneInfo boilerTz)
    {
        var list = new List<GasDayRepair>();
        if (current is null) return list;
        var today = DayOf(nowUtc, boilerTz);
        if (current.Day != today || nowUtc - MidnightUtc(today, boilerTz) <= RepairAfterMidnight) return list;
        if (previous is not null && previous.Day != today.AddDays(-1)) previous = null;

        void Check(string purpose, double? max, double? last, double? prevMax, double? prevLast)
        {
            if (max is not { } m || last is not { } l || !(m > l)) return;
            var underHalf = l <= RepairMaxShare * m + 1e-9;
            var yesterdaysTotal = prevMax is { } pm && Math.Abs(m - pm) <= SameTotalTolerance(pm) + 1e-9;
            if (!underHalf && !yesterdaysTotal) return;
            var (newPrevMax, newPrevLast) = CarryToPrevious(prevMax, prevLast, m);
            list.Add(new GasDayRepair(purpose, current.Day, m, l, prevMax, prevLast, newPrevMax, newPrevLast));
        }

        Check(HeatingKinds.Heating, current.GasHeatingMaxM3, current.GasHeatingLastM3, previous?.GasHeatingMaxM3, previous?.GasHeatingLastM3);
        Check(HeatingKinds.Dhw, current.GasDhwMaxM3, current.GasDhwLastM3, previous?.GasDhwMaxM3, previous?.GasDhwLastM3);
        return list;
    }

    /// <summary>
    /// The heat produced and the boiler's electricity (kept as the day's highest, no last value) of a current day whose
    /// gas was repaired: each value equal to the previous day's within <see cref="SameTotalTolerance"/> is the previous
    /// day's total read after midnight; the previous day keeps max(its value, this one) and the current day's value is
    /// cleared (the next reading writes the current "today" value again). A different value is the day's own and stays.
    /// </summary>
    public static IReadOnlyList<KwhDayRepair> RepairCurrentDayKwh(HeatingEnergyDay? previous, HeatingEnergyDay current)
    {
        var list = new List<KwhDayRepair>();
        if (previous is null || previous.Day != current.Day.AddDays(-1)) return list;
        void Check(string kind, string purpose, double? value, double? prev)
        {
            if (value is not { } v || prev is not { } p || Math.Abs(v - p) > SameTotalTolerance(p) + 1e-9) return;
            list.Add(new KwhDayRepair(kind, purpose, current.Day, v, p, Math.Max(v, p)));
        }
        Check(HeatingKinds.Heat, HeatingKinds.Heating, current.HeatHeatingKwh, previous.HeatHeatingKwh);
        Check(HeatingKinds.Heat, HeatingKinds.Dhw, current.HeatDhwKwh, previous.HeatDhwKwh);
        Check(HeatingKinds.Power, HeatingKinds.Heating, current.PowerHeatingKwh, previous.PowerHeatingKwh);
        Check(HeatingKinds.Power, HeatingKinds.Dhw, current.PowerDhwKwh, previous.PowerDhwKwh);
        return list;
    }
}
