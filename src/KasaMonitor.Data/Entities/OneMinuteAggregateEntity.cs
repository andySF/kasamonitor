namespace KasaMonitor.Data.Entities;

public sealed class OneMinuteAggregateEntity
{
    public Guid PlugId { get; set; }
    public DateTimeOffset BucketStartUtc { get; set; }
    public long PAvgMw { get; set; }
    public long PMaxMw { get; set; }
    public long PMinMw { get; set; }
    public int SampleCount { get; set; }
    public int OnlineCount { get; set; }
}
