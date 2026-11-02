using KasaMonitor.Core.Reports;

namespace KasaMonitor.Core.Home;

/// <summary>
/// The typical day of the main meter ("profil tipic"): the mean kWh of each local hour of the day over the last
/// <see cref="Days"/> days, from the live hourly sums (<see cref="PlugHourWh"/>). Only hours whose live minutes cover at
/// least <see cref="MinMinutes"/> of 60 count (an hour with a gap would pull the mean down); a covered hour is scaled to 60
/// minutes. Null for an hour of the day without any such hour.
/// </summary>
public static class TypicalProfile
{
    public const int Days = 30;
    public const int MinMinutes = 54;

    public static IReadOnlyList<double?> Build(IEnumerable<PlugHourWh> hours, TimeZoneInfo tz)
    {
        var sum = new double[24];
        var count = new int[24];
        foreach (var h in hours)
        {
            if (h.Minutes < MinMinutes) continue;
            var hour = TimeZoneInfo.ConvertTime(h.HourStartUtc, tz).Hour;
            sum[hour] += h.Wh / 1000.0 * 60.0 / Math.Min(60, h.Minutes);
            count[hour]++;
        }
        return Enumerable.Range(0, 24).Select(i => count[i] > 0 ? sum[i] / count[i] : (double?)null).ToList();
    }
}
