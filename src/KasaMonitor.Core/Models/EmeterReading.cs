namespace KasaMonitor.Core.Models;

/// <summary>
/// One energy-meter reading. Voltage and current are null when the device does not report them
/// (Tapo: only when get_emeter_data is not answered). For Tapo devices <see cref="TotalWh"/> is the device's
/// month-to-date energy (resets monthly); the Prometheus counter logic tolerates the reset.
/// <see cref="TodayWh"/>/<see cref="MonthWh"/> are the device's own counters for the current local day/month
/// (Tapo: get_energy_usage; legacy: emeter.get_daystat, refreshed every few minutes), null when unknown.
/// </summary>
public sealed record EmeterReading(
    long PowerMw,
    long? VoltageMv,
    long? CurrentMa,
    long TotalWh,
    long? TodayWh = null,
    long? MonthWh = null
);
