namespace KasaMonitor.Data.Entities;

public sealed class DailyEnergyEntity
{
    public Guid PlugId { get; set; }
    public string Date { get; set; } = ""; // ISO YYYY-MM-DD, local
    public long EnergyWh { get; set; }
}
