using System.Globalization;
using KasaMonitor.Core.Heating;

namespace KasaMonitor.Web.Ui;

/// <summary>Romanian texts and numbers of the Centrală page and the Dashboard card.</summary>
public static class HeatingFormat
{
    private static readonly NumberFormatInfo Ro = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NegativeSign = "-" };

    public static string Number(double? v, int decimals = 1) =>
        v is { } x ? x.ToString(decimals == 0 ? "#,##0" : "#,##0." + new string('0', decimals), Ro) : "—";

    public static string Temp(double? c) => c is null ? "—" : Number(c) + " °C";

    public static string M3(double? m3) => m3 is null ? "—" : Number(m3, 2) + " m³";

    /// <summary>Gas cost: m³ × kWh/m³ × RON/kWh.</summary>
    public static decimal GasCost(double m3, double kwhPerM3, decimal pricePerKwh) => (decimal)(m3 * kwhPerM3) * pricePerKwh;

    public static string Mode(string? mode) => mode switch
    {
        null => "—",
        "heating" => "încălzire",
        "standby" => "stand-by",
        "dhw" => "doar apă caldă",
        "dhwAndHeating" => "apă caldă și încălzire",
        "off" => "oprit",
        _ => mode,
    };

    public static string Program(string? program) => program switch
    {
        null => "—",
        "normal" => "normal",
        "comfort" => "confort",
        "reduced" => "redus",
        "standby" => "stand-by",
        "eco" => "eco",
        _ => program,
    };

    public static string DhwMode(string? mode) => mode switch
    {
        null => "—",
        "eco" => "eco",
        "comfort" => "confort",
        "efficient" => "eficient",
        "off" => "oprit",
        _ => mode,
    };

    public static string Period(string period) => period switch
    {
        "day" => "Azi",
        "last7days" => "Ultimele 7 zile",
        "month" => "Luna aceasta",
        "lastmonth" => "Luna trecută",
        "year" => "Anul acesta",
        "lastyear" => "Anul trecut",
        _ => period,
    };

    public static string Day(string day) => day switch
    {
        "mon" => "luni",
        "tue" => "marți",
        "wed" => "miercuri",
        "thu" => "joi",
        "fri" => "vineri",
        "sat" => "sâmbătă",
        "sun" => "duminică",
        _ => day,
    };

    /// <summary>One row of the gas table: m³ per purpose for a period (null when the boiler does not report it).</summary>
    public sealed record GasRow(string Period, double? DhwM3, double? HeatingM3)
    {
        public double TotalM3 => (DhwM3 ?? 0) + (HeatingM3 ?? 0);
    }

    /// <summary>The gas table (today, 7 days, month, last month, year) from the stored period values.</summary>
    public static IReadOnlyList<GasRow> GasRows(IReadOnlyList<HeatingEnergyPeriod> periods)
    {
        double? Value(string purpose, string period) =>
            periods.FirstOrDefault(p => p.Kind == HeatingKinds.Gas && p.Purpose == purpose && p.Period == period)?.Value;
        return new[] { "day", "last7days", "month", "lastmonth", "year" }
            .Select(p => new GasRow(p, Value(HeatingKinds.Dhw, p), Value(HeatingKinds.Heating, p)))
            .Where(r => r.DhwM3 is not null || r.HeatingM3 is not null)
            .ToList();
    }

    /// <summary>Sum of a kind (heat or power) over both purposes for a period; null when neither exists.</summary>
    public static double? Sum(IReadOnlyList<HeatingEnergyPeriod> periods, string kind, string period)
    {
        var values = periods.Where(p => p.Kind == kind && p.Period == period).Select(p => p.Value).ToList();
        return values.Count == 0 ? null : values.Sum();
    }
}
