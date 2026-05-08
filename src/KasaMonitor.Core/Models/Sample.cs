namespace KasaMonitor.Core.Models;

public sealed record Sample(
    long Id,
    Guid PlugId,
    DateTimeOffset TimestampUtc,
    bool Online,
    long? PowerMw,
    long? VoltageMv,
    long? CurrentMa,
    long? TotalWh,
    int? Rssi,
    bool? RelayOn,
    string? ErrorMessage
);
