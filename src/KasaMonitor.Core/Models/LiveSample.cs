namespace KasaMonitor.Core.Models;

/// <summary>
/// One reading of the live stream (1 s for the main meter, 1–10 s adaptive for plugs). <see cref="TsUtc"/> is the
/// reading's second (rounded). <see cref="Seconds"/> is how long the reading stands for: the interval it was
/// scheduled with, capped at the time since the channel's previous reading (a missed second adds nothing).
/// Stored in <c>LiveSamples</c> only when it changed (<see cref="KasaMonitor.Core.Metering.ChangeRule"/>) or every 60 s;
/// <see cref="FrequencyHz"/> and <see cref="Seconds"/> are not stored.
/// </summary>
public sealed record LiveSample(
    Guid PlugId,
    DateTimeOffset TsUtc,
    double PowerW,
    double? ApparentVa = null,
    double? VoltageV = null,
    double? CurrentA = null,
    double? Pf = null,
    double? FrequencyHz = null,
    double Seconds = 1);

/// <summary>
/// One UTC minute of a channel's live stream (table <c>LiveMinuteAggregates</c>, kept forever), computed from every
/// sample received (also those not stored). <see cref="EnergyWh"/> = Σ W × seconds / 3600; <see cref="AvgW"/> is
/// weighted by seconds; <see cref="CoveredSeconds"/> tells how much of the 60 s the samples covered.
/// </summary>
public sealed record LiveMinuteAggregate(
    Guid PlugId,
    DateTimeOffset MinuteUtc,
    double MinW,
    double AvgW,
    double MaxW,
    double? AvgVoltageV,
    double EnergyWh,
    int SampleCount,
    double CoveredSeconds)
{
    /// <summary>
    /// Combines two partial aggregates of the same minute (a restart in the middle of a minute, a late sample after
    /// the minute was closed): min/max, summed energy, samples and seconds, averages weighted by seconds.
    /// </summary>
    public LiveMinuteAggregate Merge(LiveMinuteAggregate other)
    {
        var seconds = CoveredSeconds + other.CoveredSeconds;
        double Weighted(double a, double b) => seconds > 0 ? (a * CoveredSeconds + b * other.CoveredSeconds) / seconds : (a + b) / 2;
        double? voltage = (AvgVoltageV, other.AvgVoltageV) switch
        {
            ({ } a, { } b) => Weighted(a, b),
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
        return this with
        {
            MinW = Math.Min(MinW, other.MinW),
            MaxW = Math.Max(MaxW, other.MaxW),
            AvgW = Weighted(AvgW, other.AvgW),
            AvgVoltageV = voltage,
            EnergyWh = EnergyWh + other.EnergyWh,
            SampleCount = SampleCount + other.SampleCount,
            CoveredSeconds = seconds,
        };
    }
}

/// <summary>Sources of a relay command (column <c>RelayCommands.Source</c>).</summary>
public static class RelayCommandSources
{
    public const string Ui = "ui";
}

/// <summary>One relay command sent to a device (table <c>RelayCommands</c>): <see cref="Result"/> is "ok" or the error.</summary>
public sealed record RelayCommand(long Id, DateTimeOffset TsUtc, Guid PlugId, bool On, string Source, string Result)
{
    public const string Ok = "ok";
    public bool Succeeded => Result == Ok;
}
