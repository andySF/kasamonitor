namespace KasaMonitor.Core.Models;

public sealed record PlugLiveState(
    Guid PlugId,
    string Mac,
    string Alias,
    bool IsOnline,
    EmeterReading? LastReading,
    DateTimeOffset LastSampleUtc
);
