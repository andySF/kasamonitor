namespace KasaMonitor.Core.Costing;

public sealed class TariffCalculator
{
    private readonly decimal _ronPerKwh;

    public TariffCalculator(decimal ronPerKwh)
    {
        if (ronPerKwh <= 0)
            throw new ArgumentOutOfRangeException(nameof(ronPerKwh), ronPerKwh, "Tariff rate must be > 0 RON/kWh.");
        _ronPerKwh = ronPerKwh;
    }

    public decimal CostFromWh(long wh) => wh * _ronPerKwh / 1000m;
}
