using System.Globalization;

namespace KasaMonitor.Core.Reports;

/// <summary>
/// A period in the address (single owner, the home page and /history): <c>p=zi|saptamana|luna|an|interval</c>, <c>d=</c>
/// the day (<c>yyyy-MM-dd</c>; a month also as <c>yyyy-MM</c>, a year as <c>yyyy</c>), a range as <c>de=</c> / <c>pana=</c>
/// (<c>yyyy-MM-dd</c>). Written in the shortest form: <c>?p=luna&amp;d=2026-11</c>, <c>?p=an&amp;d=2026</c>,
/// <c>?p=saptamana&amp;d=2026-10-26</c>, <c>?p=interval&amp;de=2026-10-01&amp;pana=2026-10-20</c>. A missing, invalid or future
/// period falls back to today; a range ending in the future ends today.
/// </summary>
public static class PeriodQuery
{
    private static readonly IReadOnlyDictionary<string, ReportGranularity> Kinds = new Dictionary<string, ReportGranularity>
    {
        ["zi"] = ReportGranularity.Day, ["saptamana"] = ReportGranularity.Week, ["luna"] = ReportGranularity.Month, ["an"] = ReportGranularity.Year,
        ["interval"] = ReportGranularity.Range,
    };

    public static string KindKey(ReportGranularity kind) => Kinds.First(kv => kv.Value == kind).Key;

    /// <summary>The period of the address; <paramref name="fallback"/> (default: today's day) when absent, invalid or in the future.</summary>
    public static ReportPeriod Parse(string? p, string? d, string? from, string? to, DateOnly today, ReportGranularity fallback = ReportGranularity.Day)
    {
        var kind = p is { } k && Kinds.TryGetValue(k.Trim().ToLowerInvariant(), out var g) ? g : fallback;
        if (kind == ReportGranularity.Range)
        {
            if (Day(from) is not { } a || Day(to) is not { } b) return ReportPeriod.Containing(fallback == ReportGranularity.Range ? ReportGranularity.Day : fallback, today);
            if (b < a) (a, b) = (b, a);
            if (a > today) return ReportPeriod.Containing(ReportGranularity.Day, today);
            return ReportPeriod.Between(a, b > today ? today : b);
        }
        var day = Day(d) ?? Month(d) ?? Year(d);
        return day is { } x && x <= today ? ReportPeriod.Containing(kind, x) : ReportPeriod.Containing(kind, today);
    }

    /// <summary>The query string of a period, without „?”: <c>p=luna&amp;d=2026-11</c>.</summary>
    public static string Format(ReportPeriod period) => period.Kind switch
    {
        ReportGranularity.Range => $"p=interval&de={period.Start:yyyy-MM-dd}&pana={period.LastDay:yyyy-MM-dd}",
        ReportGranularity.Month => $"p=luna&d={period.Start.ToString("yyyy-MM", CultureInfo.InvariantCulture)}",
        ReportGranularity.Year => $"p=an&d={period.Start.Year.ToString(CultureInfo.InvariantCulture)}",
        _ => $"p={KindKey(period.Kind)}&d={period.Start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}",
    };

    /// <summary>The address of /history for a period, with the comparison flag, a plug filter and a section anchor.</summary>
    public static string History(ReportPeriod period, bool compare = true, Guid? plug = null, string? anchor = null) =>
        "/history?" + Format(period) + (compare ? "" : "&cmp=0") + (plug is { } id ? "&plug=" + id : "") + (anchor is null ? "" : "#" + anchor);

    /// <summary>The comparison flag: on unless <c>cmp=0</c> (or false / nu).</summary>
    public static bool Compare(string? cmp) => cmp?.Trim().ToLowerInvariant() is not ("0" or "false" or "nu");

    /// <summary>The earliest day an address may name (older dates are invalid: nothing was measured, and DateOnly.MinValue has no previous day).</summary>
    public static readonly DateOnly MinDay = new(2000, 1, 1);

    private static DateOnly? Day(string? s) =>
        s is not null && DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d >= MinDay ? d : null;

    private static DateOnly? Month(string? s) =>
        s is not null && DateOnly.TryParseExact(s.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d >= MinDay ? d : null;

    /// <summary>A plug id of the address (<c>plug=</c> / <c>plugId=</c>); anything that is not a GUID is ignored.</summary>
    public static Guid? Plug(string? s) => Guid.TryParse(s?.Trim(), out var id) ? id : null;

    private static DateOnly? Year(string? s) =>
        s is not null && s.Trim().Length == 4 && int.TryParse(s.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && y is >= 2000 and <= 9999
            ? new DateOnly(y, 1, 1) : null;
}
