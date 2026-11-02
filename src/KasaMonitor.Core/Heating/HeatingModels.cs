namespace KasaMonitor.Core.Heating;

/// <summary>A heating device read through a cloud API (today: the Viessmann boiler behind a ViCare gateway).</summary>
public sealed record HeatingDevice(
    Guid Id,
    string Provider,
    string InstallationId,
    string GatewaySerial,
    string DeviceId,
    string ModelId,
    string Name,
    bool Enabled,
    DateTimeOffset? LastSeenUtc,
    string? LastError);

/// <summary>One poll of the boiler (table HeatingReadings). Fields the model does not expose are null.</summary>
public sealed record HeatingReading(
    Guid DeviceId,
    DateTimeOffset TsUtc,
    double? BoilerTempC,
    double? BoilerTargetC,
    double? SupplyTempC,
    double? ReturnTempC,
    double? OutsideTempC,
    double? RoomTempC,
    double? DhwTempC,
    double? DhwTargetC,
    bool? BurnerActive,
    double? ModulationPct,
    double? PumpPct,
    double? PressureBar,
    long? BurnerStarts,
    double? BurnerHours,
    string? OperatingMode,
    string? ActiveProgram,
    string? DhwMode,
    string? ActiveError,
    string? StatusCode,
    double? GasHeatingTodayM3 = null,
    double? GasDhwTodayM3 = null,
    double? PowerTodayKwh = null,
    double? HeatTodayKwh = null)
{
    /// <summary>
    /// The reading of a snapshot, with the boiler's "today" counters (gas per purpose in m³, electricity and heat summed
    /// over both purposes) so the hours of a day can be rebuilt from the increases between readings.
    /// </summary>
    public static HeatingReading From(Guid deviceId, HeatingSnapshot s, double kwhPerM3 = ViessmannApi.DefaultKwhPerM3,
        EnergyUnit assumedGasUnit = EnergyUnit.CubicMeter) => new(deviceId, s.ReadUtc,
        s.BoilerTempC, s.BoilerTargetC, s.SupplyTempC, s.ReturnTempC, s.OutsideTempC, s.RoomTempC, s.DhwTempC, s.DhwTargetC,
        s.BurnerActive, s.ModulationPct, s.PumpPct, s.PressureBar, s.BurnerStarts, s.BurnerHours,
        s.OperatingMode, s.ActiveProgram, s.DhwMode, s.ActiveError, s.StatusCode,
        GasToday(s.GasHeating, kwhPerM3, assumedGasUnit), GasToday(s.GasDhw, kwhPerM3, assumedGasUnit),
        SumToday(s.PowerHeating, s.PowerDhw), SumToday(s.HeatHeating, s.HeatDhw));

    /// <summary>"Today" of a summary: currentDay, or day[0] of a series.</summary>
    public static double? Today(EnergySummary? summary) =>
        summary?.CurrentDay ?? (summary?.Days is { Count: > 0 } days ? days[0] : null);

    private static double? GasToday(EnergySummary? summary, double kwhPerM3, EnergyUnit assumed) =>
        Today(summary) is { } v ? HeatingEnergy.ToM3(v, summary!.Unit, kwhPerM3, assumed) : null;

    private static double? SumToday(EnergySummary? heating, EnergySummary? dhw) =>
        Today(heating) is null && Today(dhw) is null ? null : (Today(heating) ?? 0) + (Today(dhw) ?? 0);
}

/// <summary>
/// Energy of one boiler day (table HeatingEnergyDaily; the day follows the boiler's midnight, <see cref="BoilerClock"/>),
/// built by KasaMonitor from the boiler's "currentDay" values, attributed to the boiler day of KasaMonitor's own
/// reading, or from a day[] series. Gas keeps two values per purpose:
/// the day's highest (<c>…MaxM3</c>, used once the day is over) and the last one seen (<c>…LastM3</c>, used for the
/// current day). Gas in m³, heat produced and
/// electricity in kWh (highest of the day).
/// </summary>
public sealed record HeatingEnergyDay(
    Guid DeviceId,
    DateOnly Day,
    double? GasHeatingMaxM3,
    double? GasDhwMaxM3,
    double? HeatHeatingKwh,
    double? HeatDhwKwh,
    double? PowerHeatingKwh,
    double? PowerDhwKwh,
    string Source,
    double? GasHeatingLastM3 = null,
    double? GasDhwLastM3 = null)
{
    /// <summary>Gas of the day as counted: the last value for <paramref name="today"/> (and later), the highest for closed days.</summary>
    public double? GasHeatingFor(DateOnly today) => Day >= today ? GasHeatingLastM3 ?? GasHeatingMaxM3 : GasHeatingMaxM3;

    public double? GasDhwFor(DateOnly today) => Day >= today ? GasDhwLastM3 ?? GasDhwMaxM3 : GasDhwMaxM3;

    public double GasTotalM3(DateOnly today) => (GasHeatingFor(today) ?? 0) + (GasDhwFor(today) ?? 0);
}

/// <summary>A gas value lower than the one stored for the same local day (counter reset after midnight, correction).</summary>
public sealed record HeatingDayDecrease(Guid DeviceId, DateOnly Day, string Purpose, double StoredMaxM3, double? StoredLastM3, double ReceivedM3);

/// <summary>The boiler's own period values (table HeatingEnergyPeriods), overwritten at every poll.</summary>
public sealed record HeatingEnergyPeriod(Guid DeviceId, string Kind, string Purpose, string Period, double Value, string Unit, DateTimeOffset UpdatedUtc);

/// <summary>Sums of every stored day: the cumulative counters of /metrics (gas: Σ closed days' highest + today's last value).</summary>
public sealed record HeatingEnergyTotals(
    double GasHeatingM3,
    double GasDhwM3,
    double HeatHeatingKwh,
    double HeatDhwKwh,
    double PowerHeatingKwh,
    double PowerDhwKwh);

/// <summary>Names used in the period table and the metric labels.</summary>
public static class HeatingKinds
{
    public const string Gas = "gas";
    public const string Heat = "heat";
    public const string Power = "power";
    public const string Heating = "heating";
    public const string Dhw = "dhw";
    public const string UnitM3 = "m3";
    public const string UnitKwh = "kWh";
    /// <summary>Daily rows built from the summary's currentDay / from a day[] series.</summary>
    public const string SourceSummary = "summary";
    public const string SourceSeries = "series";
}
