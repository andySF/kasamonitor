namespace KasaMonitor.Data.Entities;

public sealed class MonthlyEnergyEntity
{
    public Guid PlugId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public long EnergyWh { get; set; }
}
