namespace KasaMonitor.Core.Models;

public sealed record DailyKwh(int Year, int Month, int Day, long EnergyWh);

public sealed record PlugDailyKwh(Guid PlugId, DailyKwh Day);
