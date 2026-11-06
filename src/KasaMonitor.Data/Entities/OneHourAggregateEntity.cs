namespace KasaMonitor.Data.Entities;

/// <summary>
/// One UTC hour of a plug's power, rolled up from its minute rows by RollupWorker (0.14.0; the table existed unwritten
/// before): <see cref="PAvgMw"/> the mean of the minute averages, <see cref="PMaxMw"/> the largest minute maximum,
/// <see cref="SampleCount"/> the number of minute rows in the hour (not raw readings), <see cref="EnergyWh"/> = average ×
/// those minutes. Kept forever, like the minute rows.
/// </summary>
public sealed class OneHourAggregateEntity
{
    public Guid PlugId { get; set; }
    public DateTimeOffset BucketStartUtc { get; set; }
    public long PAvgMw { get; set; }
    public long PMaxMw { get; set; }
    /// <summary>
    /// Average × minutes present, rounded to the Wh per hour (a 0.4 W plug gives 0): not for energy totals, which come from
    /// DailyEnergy; kept for a rough look at one hour.
    /// </summary>
    public long EnergyWh { get; set; }
    public int SampleCount { get; set; }
}
