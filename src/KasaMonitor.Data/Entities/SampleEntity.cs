namespace KasaMonitor.Data.Entities;

public sealed class SampleEntity
{
    public long Id { get; set; }
    public Guid PlugId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public bool Online { get; set; }
    public long? PowerMw { get; set; }
    public long? VoltageMv { get; set; }
    public long? CurrentMa { get; set; }
    public long? TotalWh { get; set; }
    public int? Rssi { get; set; }
    public bool? RelayOn { get; set; }
    public string? ErrorMessage { get; set; }
}
