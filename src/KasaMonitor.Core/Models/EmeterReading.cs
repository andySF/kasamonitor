namespace KasaMonitor.Core.Models;

public sealed record EmeterReading(
    long PowerMw,
    long VoltageMv,
    long CurrentMa,
    long TotalWh
);
