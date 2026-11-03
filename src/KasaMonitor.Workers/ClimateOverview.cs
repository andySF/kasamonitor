using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Home;

namespace KasaMonitor.Workers;

/// <summary>
/// The home page's „Climă” card over the climate service (<see cref="ClimateState"/>): „Afară” = every sensor ticked
/// „afară” (<see cref="ClimateSensor.IsOutdoor"/>, the same rule as /clima and /centrala: Open-Meteo by default, a physical
/// outdoor sensor once ticked), the rooms = every other not archived sensor with a temperature, by room and name. The boiler's water temperatures (supply, hot water) are not climate sensors: they stay on the
/// „Centrala” card. A stale value (<see cref="ClimateFreshnessLevel.Stale"/>, or the source reports it offline) is kept
/// and marked, so a sensor does not silently vanish.
/// </summary>
public sealed class ClimateOverview : IClimateOverview
{
    private readonly ClimateState _state;
    public ClimateOverview(ClimateState state) => _state = state;

    public IReadOnlyList<HomeClimateReading> Readings(DateTimeOffset nowUtc) => Build(_state.Sensors, nowUtc, s => !_state.IsOnline(s, nowUtc));

    /// <summary>The rule, apart from the state (tests): see the class summary.</summary>
    public static IReadOnlyList<HomeClimateReading> Build(IEnumerable<ClimateSensorLive> sensors, DateTimeOffset nowUtc, Func<ClimateSensorLive, bool> stale)
    {
        var list = new List<HomeClimateReading>();
        foreach (var s in sensors)
        {
            if (s.Sensor.IsArchived || s.TemperatureC is not { } t) continue;
            if (IsBoilerWater(s.Sensor)) continue;
            var outdoor = s.Sensor.IsOutdoor;
            var age = s.ReadUtc is { } r ? Math.Max(0, (nowUtc - r).TotalSeconds) : double.MaxValue;
            var room = outdoor ? "Afară" : string.IsNullOrWhiteSpace(s.Sensor.Room) ? s.Sensor.Name : s.Sensor.Room;
            list.Add(new HomeClimateReading(s.Sensor.Name, room, t, s.HumidityPercent, outdoor, age, stale(s)));
        }
        return list.OrderBy(r => r.IsOutdoor)
            .ThenBy(r => r.Room, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The boiler's former supply / hot-water virtual sensor (a water temperature, not a room). Not created since 0.9.3
    /// and removed at startup (<see cref="ClimateRecorder"/>); kept here defensively.
    /// </summary>
    public static bool IsBoilerWater(ClimateSensor s) => ViessmannClimate.IsWaterSensor(s.Source, s.ExternalId);
}
