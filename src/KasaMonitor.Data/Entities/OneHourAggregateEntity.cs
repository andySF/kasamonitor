namespace KasaMonitor.Data.Entities;

public sealed class OneHourAggregateEntity
{
    public Guid PlugId { get; set; }
    public DateTimeOffset BucketStartUtc { get; set; }
    public long PAvgMw { get; set; }
    public long PMaxMw { get; set; }
    public long EnergyWh { get; set; }
    public int SampleCount { get; set; }
}
