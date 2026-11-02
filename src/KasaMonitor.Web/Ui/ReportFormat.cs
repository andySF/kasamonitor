using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Reports;

namespace KasaMonitor.Web.Ui;

/// <summary>Romanian texts of the period reports (/history "Casă", /centrala "Rapoarte"): period titles, bar labels, Δ %, rest of the house.</summary>
public static class ReportFormat
{
    // Explicit names: the container runs with invariant globalization (no ro-RO culture data).
    private static readonly string[] Months =
        { "ianuarie", "februarie", "martie", "aprilie", "mai", "iunie", "iulie", "august", "septembrie", "octombrie", "noiembrie", "decembrie" };
    private static readonly string[] MonthsShort = { "ian", "feb", "mar", "apr", "mai", "iun", "iul", "aug", "sep", "oct", "nov", "dec" };
    private static readonly string[] Days = { "duminică", "luni", "marți", "miercuri", "joi", "vineri", "sâmbătă" };
    private static readonly string[] DaysShort = { "dum", "lun", "mar", "mie", "joi", "vin", "sâm" };

    /// <summary>
    /// The note of the boiler reports: "zi = după ceasul centralei (CET), se încheie la 01:00 ora României" (the end of the
    /// boiler's <paramref name="day"/> in local time; "CET" for Europe/Berlin, the zone id otherwise).
    /// </summary>
    public static string BoilerDayNote(TimeZoneInfo boilerTz, TimeZoneInfo localTz, DateOnly day)
    {
        var zone = boilerTz.Id is "Europe/Berlin" or "W. Europe Standard Time" ? "CET" : boilerTz.Id;
        var end = TimeZoneInfo.ConvertTime(KasaMonitor.Core.Heating.BoilerClock.MidnightUtc(day.AddDays(1), boilerTz), localTz);
        var where = localTz.Id is "Europe/Bucharest" or "GTB Standard Time" ? "ora României" : "ora locală";
        return $"zi = după ceasul centralei ({zone}), se încheie la {end:HH:mm} {where}";
    }

    public static string KindName(ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => "Zi",
        ReportGranularity.Week => "Săptămână",
        ReportGranularity.Month => "Lună",
        _ => "An",
    };

    /// <summary>"pe ore" / "pe zile" / "pe luni".</summary>
    public static string BucketsName(ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => "pe ore",
        ReportGranularity.Year => "pe luni",
        _ => "pe zile",
    };

    public static string Date(DateOnly d) => $"{d.Day} {Months[d.Month - 1]} {d.Year}";

    public static string ShortDate(DateOnly d) => $"{d.Day} {MonthsShort[d.Month - 1]}";

    /// <summary>"duminică, 1 noiembrie 2026" · "26 oct – 1 nov 2026" · "noiembrie 2026" · "2026".</summary>
    public static string Title(ReportPeriod p) => p.Kind switch
    {
        ReportGranularity.Day => $"{Days[(int)p.Start.DayOfWeek]}, {Date(p.Start)}",
        ReportGranularity.Week => p.Start.Year == p.LastDay.Year
            ? $"{ShortDate(p.Start)} – {ShortDate(p.LastDay)} {p.LastDay.Year}"
            : $"{ShortDate(p.Start)} {p.Start.Year} – {ShortDate(p.LastDay)} {p.LastDay.Year}",
        ReportGranularity.Month => $"{Months[p.Start.Month - 1]} {p.Start.Year}",
        _ => p.Start.Year.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>"față de ieri" / "față de săptămâna trecută" / … (the previous period).</summary>
    public static string VersusPrevious(ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => "față de ziua precedentă",
        ReportGranularity.Week => "față de săptămâna precedentă",
        ReportGranularity.Month => "față de luna precedentă",
        _ => "față de anul precedent",
    };

    /// <summary>Bar label: "07:00" (the second 03:00 of the autumn DST day: "03:00*"), "lun 26", "26", "nov".</summary>
    public static string BucketLabel(ReportBucket b, ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => b.LocalStart.ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture) + (b.RepeatedHour ? "*" : ""),
        ReportGranularity.Week => $"{DaysShort[(int)b.Day.DayOfWeek]} {b.Day.Day}",
        ReportGranularity.Month => b.Day.Day.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => MonthsShort[b.Day.Month - 1],
    };

    /// <summary>Row label of the table: "07:00–08:00", "luni 26 oct", "noiembrie".</summary>
    public static string BucketRow(ReportBucket b, ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => BucketLabel(b, kind) + "–" + b.LocalStart.AddHours(1).ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture),
        ReportGranularity.Year => Months[b.Day.Month - 1],
        _ => $"{Days[(int)b.Day.DayOfWeek]} {ShortDate(b.Day)}",
    };

    /// <summary>kWh with 2 decimals below 10, 1 above; "—" for null.</summary>
    public static string Kwh(double? kwh) => kwh is { } v ? HeatingFormat.Number(v, Math.Abs(v) < 10 ? 2 : 1) : "—";

    /// <summary>A value in its unit: "1,25 m³", "3,40 kWh".</summary>
    public static string Value(double? v, string unit) => v is null ? "—" : unit == "m3" ? HeatingFormat.M3(v) : Kwh(v) + " kWh";

    /// <summary>"▲ 12 %" / "▼ 3,5 %" / "= 0 %"; null without a comparison.</summary>
    public static string? Delta(double? pct)
    {
        if (pct is not { } p) return null;
        var abs = Math.Abs(p);
        var text = HeatingFormat.Number(abs, abs < 10 ? 1 : 0) + " %";
        return Math.Abs(p) < 0.05 ? "= " + text : (p > 0 ? "▲ " : "▼ ") + text;
    }

    /// <summary>Consumption up = red, down = green.</summary>
    public static string DeltaClass(double? pct) => pct switch
    {
        null => "text-secondary",
        > 0.05 => "text-danger",
        < -0.05 => "text-success",
        _ => "text-secondary",
    };

    /// <summary>"Restul casei" in W: "≈ 0 W" within the margin, "0 W" when the plugs exceed the meter, otherwise the value.</summary>
    public static string RestW(RestBalance? b) => b switch
    {
        null => "—",
        { Kind: RestKind.WithinMargin } => "≈ 0 W",
        { Kind: RestKind.Negative } => "0 W",
        _ => b.RestW.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " W",
    };

    public const string WithinMarginText = "în marja de eroare a măsurătorilor";
    public const string NegativeText = "prizele depășesc contorul: verifică poziția clemei";

    /// <summary>Subtitle of the rest of the house: "media minutului 21:04" (+ "din citirile la 10 s" without a live stream).</summary>
    public static string RestBasis(RestBalance? b) => b switch
    {
        null => "fără citiri ale contorului în ultimul minut",
        { FromPolls: true } => $"media minutului {b.IntervalStartUtc?.ToLocalTime():HH:mm}, din citirile la 10 s",
        _ => $"media minutului {b.IntervalStartUtc?.ToLocalTime():HH:mm}",
    };

    /// <summary>Tooltip of the rest of the house (power).</summary>
    public static string RestTitle(RestBalance? b)
    {
        if (b is null) return "Contorul general nu are eșantioane (nici live, nici la 10 s) în ultimul minut întreg.";
        var basis = $"Media minutului {b.IntervalStartUtc?.ToLocalTime():HH:mm}" + (b.FromPolls ? " (fără flux live: din citirile la 10 s ale contorului și prizelor)" : "")
                    + $": contor {b.MainW:0} W − prize {b.PlugsW:0} W = {b.RawW:0} W "
                    + $"(marjă ±{HouseBalance.Margin(b.MainW, HouseBalance.MarginW):0} W = max(15 W, 3 % din contor)).";
        return b.Kind switch
        {
            RestKind.WithinMargin => WithinMarginText + ". " + basis,
            RestKind.Negative => NegativeText + ". " + basis,
            _ => basis,
        };
    }

    /// <summary>Rest of the house in kWh: "≈ 0,12" within the margin, "0" with the plugs above the meter.</summary>
    public static string RestKwh(double? kwh, RestKind? kind) => kind switch
    {
        null => Kwh(kwh),
        RestKind.WithinMargin => "≈ " + Kwh(kwh),
        RestKind.Negative => "0 ⚠",
        RestKind.PartialData => "0 (date parțiale)",
        _ => Kwh(kwh),
    };

    public static string RestKwhTitle(RestKind? kind) => kind switch
    {
        RestKind.WithinMargin => WithinMarginText + " (max(15 W × ore, 3 % din casă))",
        RestKind.Negative => NegativeText,
        RestKind.PartialData => PartialText,
        _ => "",
    };

    public const string PartialText = "date parțiale: fluxul live al contorului acoperă sub 90 % din interval (memoria contorului poate fi incompletă)";

    /// <summary>Text when there is no Δ: the previous period was 0, or one of the periods has no data.</summary>
    public static string NoDelta(double? previous) =>
        previous is { } p && Math.Abs(p) < 1e-9 ? "perioada precedentă: 0" : "fără comparație (lipsesc datele uneia dintre perioade)";
}
