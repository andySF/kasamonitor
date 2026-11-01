using System.Globalization;
using KasaMonitor.Core.Costing;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Web.Ui;

/// <summary>Energy and cost texts for the Dashboard, in ro-RO format: "Azi: 0,46 kWh · 0,51 lei".</summary>
public static class EnergyFormat
{
    // ro-RO number format (decimal comma, dot for thousands), built explicitly so it does not depend on ICU data
    // being present in the container (invariant-globalization images have no ro-RO culture).
    private static readonly NumberFormatInfo Ro = new() { NumberDecimalSeparator = ",", NumberGroupSeparator = ".", NegativeSign = "-" };

    /// <summary>kWh with 2 decimals below 10 kWh, 1 decimal above ("0,46", "12,3", "1.234,5").</summary>
    public static string Kwh(long wh)
    {
        var kwh = wh / 1000m;
        return kwh.ToString(Math.Abs(kwh) < 10 ? "#,##0.00" : "#,##0.0", Ro);
    }

    public static string Lei(decimal amount) => amount.ToString("#,##0.00", Ro) + " lei";

    /// <summary>"Azi: 0,46 kWh · 0,51 lei".</summary>
    public static string Line(string label, long wh, TariffCalculator tariff) =>
        $"{label}: {Kwh(wh)} kWh · {Lei(tariff.CostFromWh(wh))}";

    /// <summary>Sum of a per-plug counter over the readings that have it (null when none has it).</summary>
    public static long? Sum(IEnumerable<EmeterReading?> readings, Func<EmeterReading, long?> pick)
    {
        long? total = null;
        foreach (var r in readings)
            if (r is not null && pick(r) is { } v) total = (total ?? 0) + v;
        return total;
    }
}
