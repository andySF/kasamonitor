namespace KasaMonitor.Data.Entities;

/// <summary>A temperature/humidity sensor (table ClimateSensors). (Source, ExternalId) is unique; <see cref="LastSeenUtc"/> = unix seconds.</summary>
public sealed class ClimateSensorEntity
{
    public Guid Id { get; set; }
    /// <summary>viessmann | govee | shelly | openmeteo.</summary>
    public string Source { get; set; } = "";
    public string ExternalId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Room { get; set; } = "";
    public bool IsOutdoor { get; set; }
    public bool IsArchived { get; set; }
    public long? LastSeenUtc { get; set; }
    public int? BatteryPercent { get; set; }
}

/// <summary>One stored reading (table ClimateReadings), on change or every 30 min. Key (SensorId, Ts); <see cref="Ts"/> = unix seconds.</summary>
public sealed class ClimateReadingEntity
{
    public Guid SensorId { get; set; }
    public long Ts { get; set; }
    public double TemperatureC { get; set; }
    public double? HumidityPercent { get; set; }
    public int? BatteryPercent { get; set; }
}
