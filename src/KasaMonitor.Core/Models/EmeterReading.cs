namespace KasaMonitor.Core.Models;

/// <summary>
/// One energy-meter reading. Voltage and current are null when the device does not report them
/// (Tapo: only when get_emeter_data is not answered). For Tapo devices <see cref="TotalWh"/> is the device's
/// month-to-date energy (resets monthly); the Prometheus counter logic tolerates the reset.
/// </summary>
public sealed record EmeterReading(
    long PowerMw,
    long? VoltageMv,
    long? CurrentMa,
    long TotalWh
);
