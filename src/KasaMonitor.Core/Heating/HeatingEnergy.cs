namespace KasaMonitor.Core.Heating;

/// <summary>
/// Gas units and daily attribution. The boiler reports gas in m³ (B0KA) or kWh (other models); KasaMonitor keeps gas
/// in m³ and converts to kWh with the calorific factor of the settings (default 10.55 kWh/m³) for the cost.
/// </summary>
public static class HeatingEnergy
{
    /// <param name="unit">Unit reported by the API; <see cref="EnergyUnit.Unknown"/> falls back to <paramref name="assumedUnit"/>.</param>
    public static double ToKwh(double value, EnergyUnit unit, double kwhPerM3, EnergyUnit assumedUnit = EnergyUnit.CubicMeter) =>
        Effective(unit, assumedUnit) == EnergyUnit.KilowattHour ? value : value * kwhPerM3;

    public static double ToM3(double value, EnergyUnit unit, double kwhPerM3, EnergyUnit assumedUnit = EnergyUnit.CubicMeter) =>
        Effective(unit, assumedUnit) == EnergyUnit.CubicMeter ? value : (kwhPerM3 > 0 ? value / kwhPerM3 : 0);

    public static EnergyUnit Effective(EnergyUnit unit, EnergyUnit assumedUnit) =>
        unit != EnergyUnit.Unknown ? unit : (assumedUnit == EnergyUnit.KilowattHour ? EnergyUnit.KilowattHour : EnergyUnit.CubicMeter);

    /// <summary>
    /// The day of an instant in <paramref name="tz"/>. For a "currentDay" value: the day of KasaMonitor's own reading in
    /// the boiler's zone (<see cref="BoilerClock"/>). The feature's timestamp is not used: the gateway re-sends many
    /// features in bulk with a fresh timestamp.
    /// </summary>
    public static DateOnly DayOf(DateTimeOffset readUtc, TimeZoneInfo tz) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(readUtc, tz).DateTime);

    /// <summary>
    /// The days of a <c>day[]</c> series (day[0] = the day of <paramref name="readAt"/>, day[1] = the day before, …).
    /// </summary>
    public static IEnumerable<(DateOnly Day, double Value)> SeriesDays(IReadOnlyList<double> days, DateTimeOffset readAt, TimeZoneInfo tz)
    {
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(readAt, tz).DateTime);
        for (var i = 0; i < days.Count; i++)
            yield return (first.AddDays(-i), days[i]);
    }

    /// <summary>Apparent efficiency = heat produced / gas energy (kWh); null without gas.</summary>
    public static double? Efficiency(double? heatKwh, double? gasKwh) =>
        heatKwh is { } h && gasKwh is { } g && g > 0 ? h / g : null;
}
