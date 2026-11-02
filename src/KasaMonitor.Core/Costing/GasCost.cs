namespace KasaMonitor.Core.Costing;

/// <summary>Gas cost (single owner of the rule): m³ × kWh/m³ (calorific factor) × RON/kWh.</summary>
public static class GasCost
{
    public static decimal Ron(double m3, double kwhPerM3, decimal pricePerKwh) => (decimal)(m3 * kwhPerM3) * pricePerKwh;

    /// <summary>m³ → kWh with the calorific factor.</summary>
    public static double Kwh(double m3, double kwhPerM3) => m3 * kwhPerM3;
}
