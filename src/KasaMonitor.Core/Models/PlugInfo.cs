namespace KasaMonitor.Core.Models;

public sealed record PlugInfo(
    string Mac,
    string Alias,
    string Model,
    string HwVer,
    string SwVer,
    string OemId,
    bool RelayOn,
    int RssiDbm,
    TimeSpan OnTime
);
