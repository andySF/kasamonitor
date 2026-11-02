namespace KasaMonitor.Core.Home;

/// <summary>
/// One temperature of the home page's „Climă” card: a room (or outside) with its temperature, the humidity when the sensor
/// has one, how old the value is and whether it is stale (older than its source's tolerance).
/// </summary>
public sealed record HomeClimateReading(string Name, string Room, double TemperatureC, double? HumidityPercent, bool IsOutdoor,
    double AgeSeconds, bool Stale = false);

/// <summary>
/// What the home page's „Climă” card shows: the temperatures per room and outside, indoor first. The card is left out when
/// there is no reading (no climate source with a value).
/// </summary>
public interface IClimateOverview
{
    IReadOnlyList<HomeClimateReading> Readings(DateTimeOffset nowUtc);
}
