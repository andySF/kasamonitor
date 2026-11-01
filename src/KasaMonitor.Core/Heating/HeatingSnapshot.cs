namespace KasaMonitor.Core.Heating;

/// <summary>Unit of an energy value as reported by the API (<c>unit</c>): gas comes in m³ on some boilers, kWh on others.</summary>
public enum EnergyUnit { Unknown, CubicMeter, KilowattHour }

/// <summary>
/// One consumption/production feature of a purpose (heating or domestic hot water). The summary values come from
/// <c>…summary.&lt;purpose&gt;</c> (B0KA); <see cref="Days"/> (day[0] = today) only exists on models with the
/// <c>day[]</c> series (PyViCare schema), not on the Vitodens 050-W.
/// </summary>
public sealed record EnergySummary(
    double? CurrentDay,
    double? LastSevenDays,
    double? CurrentMonth,
    double? LastMonth,
    double? CurrentYear,
    double? LastYear,
    EnergyUnit Unit,
    DateTimeOffset? FeatureTs,
    IReadOnlyList<double>? Days = null,
    DateTimeOffset? DaysReadAt = null)
{
    /// <summary>Period names as exported in the <c>period</c> label.</summary>
    public static readonly string[] PeriodNames = { "day", "last7days", "month", "lastmonth", "year", "lastyear" };

    public IEnumerable<(string Period, double Value)> Periods()
    {
        if (CurrentDay is { } d) yield return ("day", d);
        if (LastSevenDays is { } w) yield return ("last7days", w);
        if (CurrentMonth is { } m) yield return ("month", m);
        if (LastMonth is { } lm) yield return ("lastmonth", lm);
        if (CurrentYear is { } y) yield return ("year", y);
        if (LastYear is { } ly) yield return ("lastyear", ly);
    }
}

/// <summary>One entry of <c>device.messages.*.raw</c> (status codes such as "S.6", service and info messages, errors).</summary>
public sealed record HeatingMessage(string Source, string Code, DateTimeOffset? Ts, string? Priority);

/// <summary>One slot of the heating schedule (shown only; KasaMonitor never changes it).</summary>
public sealed record HeatingScheduleSlot(string Day, string Mode, string Start, string End);

/// <summary>A feature of the dump (name and device), used for the "Nemapate" list.</summary>
public sealed record HeatingFeatureRef(string Name, string? DeviceId);

/// <summary>
/// Everything KasaMonitor reads from one bulk features answer. Fields the boiler does not expose stay null (on the
/// Vitodens 050-W B0KA: return, outside, boiler and pressure sensors) and are neither shown nor exported.
/// </summary>
public sealed class HeatingSnapshot
{
    public DateTimeOffset ReadUtc { get; init; }

    public double? BoilerTempC { get; init; }
    /// <summary><c>heating.boiler.temperature</c>: the boiler's target (20 °C when it does not heat), not a measurement.</summary>
    public double? BoilerTargetC { get; init; }
    public double? SupplyTempC { get; init; }
    public double? ReturnTempC { get; init; }
    public double? OutsideTempC { get; init; }
    public double? RoomTempC { get; init; }
    public double? DhwTempC { get; init; }
    public double? DhwTargetC { get; init; }

    public bool? BurnerActive { get; init; }
    public double? ModulationPct { get; init; }
    public double? PumpPct { get; init; }
    public double? PressureBar { get; init; }
    public long? BurnerStarts { get; init; }
    public double? BurnerHours { get; init; }

    public string? OperatingMode { get; init; }
    public string? ActiveProgram { get; init; }
    /// <summary>Target room temperature per program (normal, comfort, reduced).</summary>
    public IReadOnlyDictionary<string, double> ProgramTemps { get; init; } = new Dictionary<string, double>();
    public string? CircuitName { get; init; }
    public IReadOnlyList<HeatingScheduleSlot> Schedule { get; init; } = Array.Empty<HeatingScheduleSlot>();
    public string? FrostProtection { get; init; }

    public string? DhwMode { get; init; }
    public bool? DhwActive { get; init; }
    public string? DhwStatus { get; init; }
    public bool? DhwComfort { get; init; }
    public bool? DhwOneTimeCharge { get; init; }
    public bool? DhwScaldProtection { get; init; }

    public bool? MalfunctionLock { get; init; }
    public IReadOnlyList<HeatingMessage> Messages { get; init; } = Array.Empty<HeatingMessage>();
    /// <summary>Newest message whose priority is not "status" (or "blocare" when only the malfunction lock is on).</summary>
    public string? ActiveError { get; init; }
    /// <summary>Newest status code (e.g. "S.6").</summary>
    public string? StatusCode { get; init; }

    public EnergySummary? GasHeating { get; init; }
    public EnergySummary? GasDhw { get; init; }
    public EnergySummary? HeatHeating { get; init; }
    public EnergySummary? HeatDhw { get; init; }
    public EnergySummary? PowerHeating { get; init; }
    public EnergySummary? PowerDhw { get; init; }

    public double? WifiDbm { get; init; }
    public double? RoomSensorBatteryPct { get; init; }
    public double? RoomSensorLqiPct { get; init; }
    public string? GasType { get; init; }
    public bool? DaylightSaving { get; init; }

    public int FeatureCount { get; init; }
    public IReadOnlyList<HeatingFeatureRef> Unmapped { get; init; } = Array.Empty<HeatingFeatureRef>();
}
