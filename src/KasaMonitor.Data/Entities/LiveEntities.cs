namespace KasaMonitor.Data.Entities;

/// <summary>One stored live sample (table LiveSamples; written on change or every 60 s). <see cref="Ts"/> = unix seconds, UTC.</summary>
public sealed class LiveSampleEntity
{
    public long Id { get; set; }
    public Guid PlugId { get; set; }
    public long Ts { get; set; }
    public double PowerW { get; set; }
    public double? ApparentVa { get; set; }
    public double? VoltageV { get; set; }
    public double? CurrentA { get; set; }
    public double? Pf { get; set; }
}

/// <summary>One minute of the live stream (table LiveMinuteAggregates, kept forever). <see cref="MinuteTs"/> = unix seconds of the minute start.</summary>
public sealed class LiveMinuteAggregateEntity
{
    public Guid PlugId { get; set; }
    public long MinuteTs { get; set; }
    public double MinW { get; set; }
    public double AvgW { get; set; }
    public double MaxW { get; set; }
    public double? AvgVoltageV { get; set; }
    public double EnergyWh { get; set; }
    public int SampleCount { get; set; }
    public double CoveredSeconds { get; set; }
}

/// <summary>One relay command (table RelayCommands). <see cref="Ts"/> = unix milliseconds, UTC.</summary>
public sealed class RelayCommandEntity
{
    public long Id { get; set; }
    public long Ts { get; set; }
    public Guid PlugId { get; set; }
    public bool On { get; set; }
    public string Source { get; set; } = "";
    public string Result { get; set; } = "";
}
