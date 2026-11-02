namespace KasaMonitor.Data.Entities;

/// <summary>A Miele appliance (table MieleDevices). <see cref="Id"/> = the Miele deviceId; times = unix seconds, UTC.</summary>
public sealed class MieleDeviceEntity
{
    public string Id { get; set; } = "";
    public int? TypeRaw { get; set; }
    public string TypeText { get; set; } = "";
    /// <summary>Technical type (model) from ident.deviceIdentLabel.techType.</summary>
    public string TechType { get; set; } = "";
    /// <summary>Friendly name set in the Miele app (ident.deviceName).</summary>
    public string DeviceName { get; set; } = "";
    public string Alias { get; set; } = "";
    /// <summary>The plug that feeds the appliance (optional): energy per cycle and NILM ground truth.</summary>
    public Guid? PlugId { get; set; }
    public bool IsArchived { get; set; }
    public long? LastSeenUtc { get; set; }
}

/// <summary>One state of an appliance (table MieleStateSnapshots), stored on change and at most every 5 min otherwise. <see cref="Ts"/> = unix seconds.</summary>
public sealed class MieleStateSnapshotEntity
{
    public long Id { get; set; }
    public long Ts { get; set; }
    public string DeviceId { get; set; } = "";
    public int? StatusRaw { get; set; }
    public string? StatusText { get; set; }
    public int? ProgramTypeRaw { get; set; }
    public string? ProgramTypeText { get; set; }
    public int? ProgramIdRaw { get; set; }
    public string? ProgramIdText { get; set; }
    public int? PhaseRaw { get; set; }
    public string? PhaseText { get; set; }
    public int? RemainingSec { get; set; }
    public int? ElapsedSec { get; set; }
    public int? StartTimeSec { get; set; }
    public double? TargetTempC { get; set; }
    public int? SpinRpm { get; set; }
    public int? DryingStepRaw { get; set; }
    public bool? DoorOpen { get; set; }
    public bool? SignalFailure { get; set; }
    public bool? SignalInfo { get; set; }
    public double? EcoEnergyKwh { get; set; }
    public double? EcoWaterL { get; set; }
    public double? EcoEnergyForecast { get; set; }
    public double? EcoWaterForecast { get; set; }
    public bool? RemoteFullControl { get; set; }
    public bool? RemoteSmartGrid { get; set; }
    public bool? RemoteMobileStart { get; set; }
}

/// <summary>
/// One program run (table MieleCycles). Times = unix seconds; <see cref="PhasesJson"/> =
/// <c>[{"phaseRaw":260,"phaseText":"Spălare principală","fromTs":…,"toTs":…}]</c>.
/// </summary>
public sealed class MieleCycleEntity
{
    public long Id { get; set; }
    public string DeviceId { get; set; } = "";
    public long StartTs { get; set; }
    public long? EndTs { get; set; }
    public long LastRunningTs { get; set; }
    public int? ProgramRaw { get; set; }
    public string? Program { get; set; }
    public string PhasesJson { get; set; } = "[]";
    public double? EnergyKwh { get; set; }
    public double? WaterL { get; set; }
    public double? PlugEnergyWh { get; set; }
    public string? EndReason { get; set; }
}
