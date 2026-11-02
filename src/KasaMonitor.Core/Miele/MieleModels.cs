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
/// <c>ecoFeedback</c> current consumption seen during the cycle (the counters reset when the program ends);
/// <see cref="PlugEnergyWh"/> = what the linked plug measured over the cycle.
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
    public bool IsOpen => EndUtc is null;
    public TimeSpan? Duration => EndUtc is { } end ? end - StartUtc : null;
}

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
