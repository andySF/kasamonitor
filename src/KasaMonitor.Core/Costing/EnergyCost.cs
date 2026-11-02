namespace KasaMonitor.Core.Costing;

/// <summary>Electricity cost (single owner of the rule): kWh × the tariff (RON/kWh).</summary>
public static class ElectricityCost
{
    public static decimal Ron(double kwh, decimal tariffRonPerKwh) => (decimal)kwh * tariffRonPerKwh;

    public static decimal? Ron(double? kwh, decimal tariffRonPerKwh) => kwh is { } k ? Ron(k, tariffRonPerKwh) : null;
}

/// <summary>The prices of the house: electricity tariff, gas factor and price; <see cref="HasGas"/> = a boiler is connected.</summary>
public sealed record EnergyPrices(decimal TariffRonPerKwh, double GasKwhPerM3, decimal GasPricePerKwh, bool HasGas)
{
    public decimal? Electricity(double? kwh) => ElectricityCost.Ron(kwh, TariffRonPerKwh);

    public decimal? Gas(double? m3) => m3 is { } g ? GasCost.Ron(g, GasKwhPerM3, GasPricePerKwh) : null;

    /// <summary>
    /// Electricity + gas, or null when a component that should exist is missing (no electricity, or no gas although there is
    /// a boiler): a missing part is never added as 0, so a total is always comparable with another total.
    /// </summary>
    public decimal? Total(double? kwh, double? m3) =>
        kwh is null || HasGas && m3 is null ? null : Electricity(kwh)!.Value + (Gas(m3) ?? 0);
}
