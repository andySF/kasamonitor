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

/// <summary>
/// One sensor on one local day (table <c>ClimateDaily</c>, 0.9.2): min / time-weighted mean / max temperature and humidity
/// and the number of readings, computed from <c>ClimateReadings</c>. A day without any value has a row with
/// <see cref="Count"/> 0 and no stats (it is known to be empty). <see cref="Day"/> = <c>yyyy-MM-dd</c>.
/// </summary>
public sealed class ClimateDailyEntity
{
    public Guid SensorId { get; set; }
    public string Day { get; set; } = "";
    public double? MinC { get; set; }
    public double? AvgC { get; set; }
    public double? MaxC { get; set; }
    public double? MinHumidity { get; set; }
    public double? AvgHumidity { get; set; }
    public double? MaxHumidity { get; set; }
    public int Count { get; set; }
}
