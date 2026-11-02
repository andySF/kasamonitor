namespace KasaMonitor.Core.Home;

/// <summary>Local wall-clock instants of the home page (night windows, "the same time yesterday").</summary>
public static class LocalClock
{
    /// <summary>The UTC instant of a local wall-clock time; a time skipped by a DST change moves to the first valid one after it.</summary>
    public static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo tz)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (tz.IsInvalidTime(local)) local = local.AddMinutes(30);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    /// <summary>The local day of an instant.</summary>
    public static DateOnly Day(DateTimeOffset utc, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, tz).DateTime);

    /// <summary>
    /// "Ieri la aceeași oră": yesterday's local date at today's local time of <paramref name="nowUtc"/> (so on a DST day
    /// the comparison is by wall clock, not by 24 h), as UTC.
    /// </summary>
    public static DateTimeOffset YesterdaySameTimeUtc(DateTimeOffset nowUtc, TimeZoneInfo tz) => SameTimeOnUtc(nowUtc, tz, Day(nowUtc, tz).AddDays(-1));

    /// <summary>The local time of <paramref name="nowUtc"/> on another local <paramref name="day"/>, as UTC.</summary>
    public static DateTimeOffset SameTimeOnUtc(DateTimeOffset nowUtc, TimeZoneInfo tz, DateOnly day)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, tz).DateTime;
        return ToUtc(day.ToDateTime(TimeOnly.FromDateTime(localNow)), tz);
    }
}
