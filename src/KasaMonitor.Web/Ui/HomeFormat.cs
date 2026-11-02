using KasaMonitor.Core.Reports;

namespace KasaMonitor.Web.Ui;

/// <summary>
/// Romanian texts and numbers of the home page „Acasă”. Decimals (decision of 0.9.0, the same on every page): W whole,
/// kWh 2 decimals below 10 and 1 above (<see cref="ReportFormat.Kwh"/>), m³ 2 decimals, lei 2 decimals, a Δ with the
/// decimals of its value.
/// </summary>
public static class HomeFormat
{
    /// <summary>kWh: 2 decimals below 10, 1 above (<see cref="ReportFormat.Kwh"/>); "—" for null.</summary>
    public static string Kwh(double? v) => ReportFormat.Kwh(v);

    /// <summary>m³ with 2 decimals; "—" for null.</summary>
    public static string M3(double? v) => v is { } x ? HeatingFormat.Number(x, 2) : "—";

    /// <summary>"7,80 kWh", or "—" without a value (no unit after a dash).</summary>
    public static string KwhU(double? v) => v is null ? "—" : Kwh(v) + " kWh";

    /// <summary>"0,70 m³", or "—" without a value.</summary>
    public static string M3U(double? v) => v is null ? "—" : M3(v) + " m³";

    /// <summary>Watts without decimals ("520"); "—" for null.</summary>
    public static string W(double? v) => v is { } x ? HeatingFormat.Number(x, 0) : "—";

    /// <summary>Lei with 2 decimals; "—" for null.</summary>
    public static string Lei(decimal? v) => v is { } x ? EnergyFormat.Lei(x) : "—";

    /// <summary>A price per kWh: "1,30 lei/kWh".</summary>
    public static string PerKwh(decimal price, int decimals = 2) => HeatingFormat.Number((double)price, decimals) + " lei/kWh";

    /// <summary>A text cut to <paramref name="max"/> characters with „…”.</summary>
    public static string Trim(string text, int max) => text.Length <= max ? text : text[..Math.Max(1, max - 1)] + "…";

    /// <summary>Card title of the energy tiles: "Energie azi", "Energie · 26 oct – 1 nov 2026", …</summary>
    public static string EnergyTitle(ReportPeriod p, bool isToday) => isToday ? "Energie azi" : "Energie · " + ReportFormat.Title(p);

    /// <summary>"Unde s-a dus energia azi" / "Unde s-a dus energia · noiembrie 2026".</summary>
    public static string FlowsTitle(ReportPeriod p, bool isToday) => isToday ? "Unde s-a dus energia azi" : "Unde s-a dus energia · " + ReportFormat.Title(p);

    /// <summary>The comparison switch: „Compară cu ieri (la aceeași oră)” for today, else „Compară cu luna precedentă” etc.</summary>
    public static string CompareLabel(ReportGranularity kind, bool isToday) =>
        isToday ? "Compară cu ieri (la aceeași oră)" : PeriodNames.For(kind).Compare;

    /// <summary>"1–2 nov".</summary>
    public static string FirstDays(DateOnly monthStart, int days) =>
        days <= 1 ? $"1 {RoNames.MonthShort(monthStart.Month)}" : $"1–{days} {RoNames.MonthShort(monthStart.Month)}";

    /// <summary>
    /// Δ as an arrow text: "▼ 1,30 kWh (−14 %)"; null without a comparison. Less = good (green), more = bad (red), under
    /// <see cref="ReportFormat.FlatPct"/> „=”. The amount uses the value's own format; the percent as on the reports.
    /// </summary>
    public static (string Text, string Css)? Delta(double? current, double? previous, Func<double, string> format, string unit)
    {
        if (current is not { } c || previous is not { } p) return null;
        var d = c - p;
        var pct = ReportMath.DeltaPct(c, p);
        var css = Math.Abs(d) < 1e-9 || pct is { } x && Math.Abs(x) < ReportFormat.FlatPct ? "flat" : d < 0 ? "good" : "bad";
        var arrow = css == "flat" ? "=" : d < 0 ? "▼" : "▲";
        var pctText = pct is { } q ? $" ({(q < 0 ? "−" : "+")}{HeatingFormat.Number(Math.Abs(q), Math.Abs(q) < 10 ? 1 : 0)} %)" : "";
        return ($"{arrow} {format(Math.Abs(d))}{(unit.Length > 0 ? " " + unit : "")}{pctText}", css);
    }

    /// <summary>Δ of money: "▼ 4,00 lei (−27 %)".</summary>
    public static (string Text, string Css)? DeltaLei(decimal? current, decimal? previous) =>
        Delta((double?)current, (double?)previous, v => EnergyFormat.Lei((decimal)v), "");
}
