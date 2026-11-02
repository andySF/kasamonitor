using KasaMonitor.Core.Reports;

namespace KasaMonitor.Web.Ui;

/// <summary>
/// The Romanian words of one period kind (single owner, used by the reports and the home page): the previous period
/// („ziua precedentă”), the comparison switch, „față de …”, the bars („pe ore”) and their title („Pe ore”).
/// </summary>
public sealed record PeriodNames(string Previous, string Compare, string Versus, string Buckets, string BucketsTitle)
{
    private static readonly PeriodNames DayNames = new("ziua precedentă", "Compară cu ziua precedentă", "față de ziua precedentă", "pe ore", "Pe ore");
    private static readonly PeriodNames WeekNames = new("săptămâna precedentă", "Compară cu săptămâna precedentă", "față de săptămâna precedentă", "pe zile", "Pe zile");
    private static readonly PeriodNames MonthNames = new("luna precedentă", "Compară cu luna precedentă", "față de luna precedentă", "pe zile", "Pe zile");
    private static readonly PeriodNames YearNames = new("anul precedent", "Compară cu anul precedent", "față de anul precedent", "pe luni", "Pe luni");

    public static PeriodNames For(ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => DayNames,
        ReportGranularity.Week => WeekNames,
        ReportGranularity.Month => MonthNames,
        _ => YearNames,
    };
}

/// <summary>Romanian month and day names (single owner; the container has no ro-RO culture data).</summary>
public static class RoNames
{
    public static readonly IReadOnlyList<string> Months =
        new[] { "ianuarie", "februarie", "martie", "aprilie", "mai", "iunie", "iulie", "august", "septembrie", "octombrie", "noiembrie", "decembrie" };
    public static readonly IReadOnlyList<string> MonthsShort = new[] { "ian", "feb", "mar", "apr", "mai", "iun", "iul", "aug", "sep", "oct", "nov", "dec" };
    public static readonly IReadOnlyList<string> Days = new[] { "duminică", "luni", "marți", "miercuri", "joi", "vineri", "sâmbătă" };
    public static readonly IReadOnlyList<string> DaysShort = new[] { "dum", "lun", "mar", "mie", "joi", "vin", "sâm" };

    public static string Month(int month) => Months[month - 1];

    public static string MonthShort(int month) => MonthsShort[month - 1];
}
