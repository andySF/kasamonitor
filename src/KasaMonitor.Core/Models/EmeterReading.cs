namespace KasaMonitor.Core.Models;

/// <summary>
/// One energy-meter reading. Voltage and current are null when the device does not report them
/// (Tapo: only when get_emeter_data is not answered). For Tapo devices <see cref="TotalWh"/> is the device's
/// month-to-date energy (resets monthly); the Prometheus counter logic tolerates the reset. <see cref="TotalWh"/> is
/// null when the device did not report its energy counter in this answer (Shelly: missing em1data/emdata/aenergy):
/// the cumulative counter is then not published and the sample stores no total.
/// <see cref="TodayWh"/>/<see cref="MonthWh"/> are the device's own counters for the current local day/month
/// (Tapo: get_energy_usage; legacy: emeter.get_daystat, refreshed every few minutes), null when unknown.
/// The remaining values are reported only by some devices (Shelly Gen2/Gen3) and are null elsewhere:
/// power factor, apparent power, frequency, cumulative returned energy (Wh), device temperature and the relay state
/// read from the device (null = no relay, or the protocol does not report it). <see cref="PowerMw"/> keeps its sign:
/// a Shelly meter with a reversed clamp (or exporting) reports negative power.
/// </summary>
public sealed record EmeterReading(
    long PowerMw,
    long? VoltageMv,
    long? CurrentMa,
    long? TotalWh,
    long? TodayWh = null,
    long? MonthWh = null,
    double? PowerFactor = null,
    double? ApparentPowerVa = null,
    double? FrequencyHz = null,
    double? ReturnedWh = null,
    double? TemperatureC = null,
    bool? RelayOn = null
);
