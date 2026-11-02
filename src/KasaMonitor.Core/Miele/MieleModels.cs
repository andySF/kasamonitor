namespace KasaMonitor.Core.Miele;

/// <summary>A raw/localized pair of the API (<c>value_raw</c>, <c>value_localized</c>).</summary>
public sealed record MieleValue(int? Raw, string? Localized);

/// <summary>The <c>ident</c> of an appliance: type, the friendly name set in the Miele app and the technical type (model).</summary>
public sealed record MieleIdent(int? TypeRaw, string? TypeLocalized, string? DeviceName, string? TechType);

/// <summary>
/// The parsed <c>state</c> of one appliance. Times in seconds (the API sends <c>[hours, minutes]</c>); a value the
/// appliance does not support (<c>null</c>) or has no current value for (<c>-32768</c>) is null.
/// </summary>
public sealed record MieleDeviceState(
    string DeviceId,
    MieleIdent? Ident,
    MieleValue? Status,
    MieleValue? ProgramType,
    MieleValue? ProgramId,
    MieleValue? Phase,
    int? RemainingSec,
    int? ElapsedSec,
    int? StartTimeSec,
    double? TargetTempC,
    int? SpinRpm,
    int? DryingStepRaw,
    bool? DoorOpen,
    bool? SignalFailure,
    bool? SignalInfo,
    double? EcoEnergyKwh,
    double? EcoWaterL,
    double? EcoEnergyForecast,
    double? EcoWaterForecast,
    bool? RemoteFullControl,
    bool? RemoteSmartGrid,
    bool? RemoteMobileStart)
{
    public bool HasState => Status is not null || ProgramId is not null || Phase is not null;
}

/// <summary>An appliance known to KasaMonitor (table <c>MieleDevices</c>); <see cref="Id"/> = the Miele deviceId.</summary>
public sealed record MieleDevice(
    string Id,
    int? TypeRaw,
    string TypeText,
    string TechType,
    string DeviceName,
    string Alias,
    Guid? PlugId,
    bool IsArchived,
    DateTimeOffset? LastSeenUtc);

/// <summary>
/// The values of a state that are compared to decide whether a snapshot is stored (everything but the time). Texts
/// come from <see cref="MieleTexts"/>.
/// </summary>
public sealed record MieleStateValues(
    int? StatusRaw,
    string? StatusText,
    int? ProgramTypeRaw,
    string? ProgramTypeText,
    int? ProgramIdRaw,
    string? ProgramIdText,
    int? PhaseRaw,
    string? PhaseText,
    int? RemainingSec,
    int? ElapsedSec,
    int? StartTimeSec,
    double? TargetTempC,
    int? SpinRpm,
    int? DryingStepRaw,
    bool? DoorOpen,
    bool? SignalFailure,
    bool? SignalInfo,
    double? EcoEnergyKwh,
    double? EcoWaterL,
    double? EcoEnergyForecast,
    double? EcoWaterForecast,
    bool? RemoteFullControl,
    bool? RemoteSmartGrid,
    bool? RemoteMobileStart)
{
    public bool IsRunning => StatusRaw == MieleTexts.StatusRunning;

    public static MieleStateValues From(MieleDeviceState s, int? typeRaw) => new(
        s.Status?.Raw, MieleTexts.Status(s.Status),
        s.ProgramType?.Raw, MieleTexts.ProgramType(s.ProgramType),
        s.ProgramId?.Raw, MieleTexts.Program(s.ProgramId),
        s.Phase?.Raw, MieleTexts.Phase(typeRaw, s.Phase),
        s.RemainingSec, s.ElapsedSec, s.StartTimeSec, s.TargetTempC, s.SpinRpm, s.DryingStepRaw,
        s.DoorOpen, s.SignalFailure, s.SignalInfo,
        s.EcoEnergyKwh, s.EcoWaterL, s.EcoEnergyForecast, s.EcoWaterForecast,
        s.RemoteFullControl, s.RemoteSmartGrid, s.RemoteMobileStart);
}

/// <summary>One stored state (table <c>MieleStateSnapshots</c>).</summary>
public sealed record MieleStateSnapshot(DateTimeOffset TsUtc, string DeviceId, MieleStateValues Values);

/// <summary>One phase of a cycle; <see cref="ToUtc"/> null while it lasts.</summary>
public sealed record MielePhaseSpan(int? PhaseRaw, string PhaseText, DateTimeOffset FromUtc, DateTimeOffset? ToUtc);

/// <summary>
/// One program run (table <c>MieleCycles</c>): from the status turning Running to End programmed / Idle / Off, or to
/// <see cref="MieleApi.CycleTimeout"/> without Running. <see cref="EnergyKwh"/>/<see cref="WaterL"/> = the highest
/// <c>ecoFeedback</c> current consumption seen live during the cycle (the counters reset when the program ends; never
/// overwritten by the history); <see cref="ApiEnergyKwh"/>/<see cref="ApiWaterL"/> = the run's values in
/// <c>lastUsedPrograms</c> once the API history has it (<see cref="Source"/> merged or api);
/// <see cref="PlugEnergyWh"/> = what the linked plug measured over the cycle. Three separate pairs, never mixed. A run
/// known only from the API history (<see cref="Source"/> api) has no phases and no ecoFeedback values.
/// <see cref="ApiStartUtc"/> is the dedupe key of the API runs.
/// </summary>
public sealed record MieleCycle(
    long Id,
    string DeviceId,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    DateTimeOffset LastRunningUtc,
    int? ProgramRaw,
    string? Program,
    IReadOnlyList<MielePhaseSpan> Phases,
    double? EnergyKwh,
    double? WaterL,
    double? PlugEnergyWh,
    string? EndReason)
{
    /// <summary><see cref="MieleCycleSources"/>: observed (SSE), api (lastUsedPrograms only) or merged (both).</summary>
    public string Source { get; init; } = MieleCycleSources.Observed;
    /// <summary>The run's <c>startAt</c> in the API history (seconds), when the API reported it.</summary>
    public DateTimeOffset? ApiStartUtc { get; init; }
    /// <summary>The run's <c>endAt</c> in the API history.</summary>
    public DateTimeOffset? ApiEndUtc { get; init; }
    /// <summary>Miele's estimated energy per 15-minute slot (<c>energyMonitoring</c>), when it was returned.</summary>
    public IReadOnlyList<MieleEnergySlot>? EnergyProfile { get; init; }
    /// <summary>The run's calculated energy in the API history (<c>energyConsumption</c>); <see cref="EnergyKwh"/> stays the ecoFeedback value.</summary>
    public double? ApiEnergyKwh { get; init; }
    /// <summary>The run's calculated water in the API history (<c>waterConsumption</c>); <see cref="WaterL"/> stays the ecoFeedback value.</summary>
    public double? ApiWaterL { get; init; }

    /// <summary>Miele's best energy value: the API history's, else the ecoFeedback maximum seen live.</summary>
    public double? MieleEnergyKwh => ApiEnergyKwh ?? EnergyKwh;
    /// <summary>Miele's best water value: the API history's, else the ecoFeedback maximum seen live.</summary>
    public double? MieleWaterL => ApiWaterL ?? WaterL;

    public bool IsOpen => EndUtc is null;
    public TimeSpan? Duration => EndUtc is { } end ? end - StartUtc : null;
}

/// <summary>Where a cycle comes from (column <c>MieleCycles.Source</c>).</summary>
public static class MieleCycleSources
{
    /// <summary>Seen live (SSE / GET /devices) by KasaMonitor.</summary>
    public const string Observed = "observed";
    /// <summary>Only in the API history (<c>lastUsedPrograms</c>): a run KasaMonitor did not see.</summary>
    public const string Api = "api";
    /// <summary>Seen live and found in the API history.</summary>
    public const string Merged = "merged";

    public static string Name(string? source) => source switch
    {
        Api => "din API",
        Merged => "ambele",
        _ => "observat",
    };
}

/// <summary>One 15-minute slot of Miele's estimated energy profile (<c>energyMonitoring.data</c>: slot start UTC, kWh).</summary>
public sealed record MieleEnergySlot(DateTimeOffset StartUtc, double Kwh);

/// <summary>
/// One run of <c>GET /v1/devices/{id}/lastUsedPrograms</c>. Water and energy are calculated by Miele (not measured), null
/// when not available; <see cref="EnergyProfile"/> only with <c>calculateEnergyMonitoring=true</c>.
/// </summary>
public sealed record MieleProgramRun(
    int? ProgramRaw,
    string? Program,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double? EnergyKwh,
    double? WaterL,
    IReadOnlyList<MieleEnergySlot>? EnergyProfile);

/// <summary>Why a cycle was closed (column <c>MieleCycles.EndReason</c>).</summary>
public static class MieleEndReasons
{
    public const string End = "end";
    public const string Idle = "idle";
    public const string Off = "off";
    public const string Timeout = "timeout";

    public static string Name(string? reason) => reason switch
    {
        End => "program terminat",
        Idle => "inactiv",
        Off => "oprit",
        Timeout => "30 min fără funcționare",
        null => "—",
        _ => reason,
    };
}
