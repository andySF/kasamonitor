namespace KasaMonitor.Data.Entities;

/// <summary>A heating device read from a cloud API (table HeatingDevices). <see cref="LastSeenUtc"/> = unix seconds.</summary>
public sealed class HeatingDeviceEntity
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string GatewaySerial { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public long? LastSeenUtc { get; set; }
    public string? LastError { get; set; }
}

/// <summary>One poll of the boiler (table HeatingReadings). <see cref="Ts"/> = unix seconds, UTC.</summary>
public sealed class HeatingReadingEntity
{
    public Guid DeviceId { get; set; }
    public long Ts { get; set; }
    public double? BoilerTempC { get; set; }
    public double? BoilerTargetC { get; set; }
    public double? SupplyTempC { get; set; }
    public double? ReturnTempC { get; set; }
    public double? OutsideTempC { get; set; }
    public double? RoomTempC { get; set; }
    public double? DhwTempC { get; set; }
    public double? DhwTargetC { get; set; }
    public bool? BurnerActive { get; set; }
    public double? ModulationPct { get; set; }
    public double? PumpPct { get; set; }
    public double? PressureBar { get; set; }
    public long? BurnerStarts { get; set; }
    public double? BurnerHours { get; set; }
    public string? OperatingMode { get; set; }
    public string? ActiveProgram { get; set; }
    public string? DhwMode { get; set; }
    public string? ActiveError { get; set; }
    public string? StatusCode { get; set; }
}

/// <summary>
/// Energy of one local day (table HeatingEnergyDaily). <see cref="Day"/> = ISO yyyy-MM-dd; gas in m³: highest value of the
/// day (<c>…MaxM3</c>) and last value seen (<c>…LastM3</c>).
/// </summary>
public sealed class HeatingEnergyDailyEntity
{
    public Guid DeviceId { get; set; }
    public string Day { get; set; } = "";
    public double? GasHeatingMaxM3 { get; set; }
    public double? GasHeatingLastM3 { get; set; }
    public double? GasDhwMaxM3 { get; set; }
    public double? GasDhwLastM3 { get; set; }
    public double? HeatHeatingKwh { get; set; }
    public double? HeatDhwKwh { get; set; }
    public double? PowerHeatingKwh { get; set; }
    public double? PowerDhwKwh { get; set; }
    public string Source { get; set; } = "";
    public long UpdatedTs { get; set; }
}

/// <summary>The boiler's period values (table HeatingEnergyPeriods): kind gas|heat|power, purpose heating|dhw, period day|last7days|month|lastmonth|year|lastyear.</summary>
public sealed class HeatingEnergyPeriodEntity
{
    public Guid DeviceId { get; set; }
    public string Kind { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Period { get; set; } = "";
    public double Value { get; set; }
    public string Unit { get; set; } = "";
    public long UpdatedTs { get; set; }
}

/// <summary>The latest raw features answer per device (table HeatingRawFeatures, overwritten at every poll).</summary>
public sealed class HeatingRawFeaturesEntity
{
    public Guid DeviceId { get; set; }
    public long Ts { get; set; }
    public string Json { get; set; } = "";
}
