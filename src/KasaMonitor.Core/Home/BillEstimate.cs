using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.Home;

/// <summary>
/// The projection of one quantity (kWh or m³) over the current month: <see cref="SoFar"/> + the mean per elapsed day ×
/// the days left. <see cref="LowConfidence"/>: fewer than <see cref="BillEstimateRule.MinConfidentDays"/> days elapsed.
/// </summary>
/// <param name="MonthDays">The month's real length in days (31.04 in October, with the extra DST hour): <see cref="DaysLeft"/> uses it, so the shown calculation multiplies out to <see cref="Projected"/>.</param>
public sealed record MonthProjection(double SoFar, double Projected, double ElapsedDays, int DaysInMonth, bool LowConfidence, double MonthDays = 0)
{
    public double PerDay => ElapsedDays > 0 ? SoFar / ElapsedDays : 0;

    public double DaysLeft => Math.Max(0, (MonthDays > 0 ? MonthDays : DaysInMonth) - ElapsedDays);
}

/// <summary>
/// The bill estimate of the current month: electricity (kWh, RON), gas (m³, RON) and the total. <see cref="GasExpected"/>:
/// a boiler is connected, so the total needs both parts (a missing one is never counted as 0).
/// </summary>
public sealed record BillEstimate(MonthProjection? Electricity, decimal? ElectricityRon, MonthProjection? Gas, decimal? GasRon, bool GasExpected = false)
{
    public decimal? TotalRon => ElectricityRon is not { } e || GasExpected && GasRon is null ? null : e + (GasRon ?? 0);
    public bool LowConfidence => (Electricity?.LowConfidence ?? false) || (Gas?.LowConfidence ?? false);
}

/// <summary>
/// "ESTIMARE factură": consumption so far + (consumption so far / elapsed days) × the days left of the month. Elapsed days
/// are fractional (from the month's first local midnight to now), so the estimate does not jump at midnight; under
/// <see cref="MinElapsedDays"/> there is no estimate (too little to extrapolate).
/// </summary>
public static class BillEstimateRule
{
    public const double MinConfidentDays = 7;
    public const double MinElapsedDays = 0.25;

    public static MonthProjection? Project(double? soFar, ReportPeriod month, DateTimeOffset nowUtc, TimeZoneInfo tz)
    {
        if (soFar is not { } used || month.Kind != ReportGranularity.Month) return null;
        var start = month.StartUtc(tz);
        var end = month.EndUtc(tz);
        if (nowUtc <= start || nowUtc >= end) return null;
        var totalDays = (end - start).TotalDays;
        var elapsed = (nowUtc - start).TotalDays;
        if (elapsed < MinElapsedDays) return null;
        var projected = used + used / elapsed * (totalDays - elapsed);
        return new MonthProjection(used, projected, elapsed, month.DayCount, elapsed < MinConfidentDays, totalDays);
    }
}
