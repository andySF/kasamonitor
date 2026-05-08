namespace KasaMonitor.Core.Models;

public sealed record DailyKwh(int Year, int Month, int Day, long EnergyWh);
