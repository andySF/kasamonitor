using KasaMonitor.Core.Climate;
using KasaMonitor.Workers;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// The <c>climate_*</c> family on /metrics (labels <c>sensor</c> = the sensor's name, <c>room</c>, <c>source</c> =
/// viessmann|govee|shelly|openmeteo), refreshed from <see cref="ClimateState"/> right before every scrape. One sensor =
/// one series per family: a renamed sensor's old series is removed, an archived one disappears; a value the sensor does
/// not have (humidity of the boiler, battery of Open-Meteo) has no series. <c>climate_api_requests_today</c> has only
/// <c>source</c> (govee, openmeteo). Device ids and MACs are never labels.
/// </summary>
public static class ClimateMetrics
{
    public static readonly string[] Labels = { "sensor", "room", "source" };

    private static KasaMetrics.MetricInfo G(string name, string unit, string help) => new(name, "gauge", unit, help);

    public static readonly KasaMetrics.MetricInfo TemperatureInfo = G("climate_temperature_celsius", "°C", "Last temperature read from the sensor.");
    public static readonly KasaMetrics.MetricInfo HumidityInfo = G("climate_humidity_percent", "% RH", "Last relative humidity read from the sensor.");
    public static readonly KasaMetrics.MetricInfo BatteryInfo = G("climate_battery_percent", "%", "Battery of the sensor (Govee, Shelly H&T), when reported.");
    public static readonly KasaMetrics.MetricInfo OnlineInfo = G("climate_online", "0/1", "The source reports the sensor online and its last value is within 6 expected intervals of its source.");
    public static readonly KasaMetrics.MetricInfo LastReadingInfo = G("climate_last_reading_timestamp_seconds", "s", "Unix time of the last value read from the sensor.");
    public static readonly KasaMetrics.MetricInfo RequestsInfo = G("climate_api_requests_today", "requests", "HTTP requests made today to a cloud source (label source: govee, openmeteo); Govee allows 10,000 a day.");

    public static readonly IReadOnlyList<KasaMetrics.MetricInfo> Catalog = new[]
    {
        TemperatureInfo, HumidityInfo, BatteryInfo, OnlineInfo, LastReadingInfo, RequestsInfo,
    };

    private static Gauge Gauge(KasaMetrics.MetricInfo i, string[]? labels = null) => Metrics.CreateGauge(i.Name, i.Help, labels ?? Labels);

    private static readonly Gauge Temperature = Gauge(TemperatureInfo);
    private static readonly Gauge Humidity = Gauge(HumidityInfo);
    private static readonly Gauge Battery = Gauge(BatteryInfo);
    private static readonly Gauge Online = Gauge(OnlineInfo);
    private static readonly Gauge LastReading = Gauge(LastReadingInfo);
    private static readonly Gauge Requests = Gauge(RequestsInfo, new[] { "source" });

    private static readonly Gauge[] PerSensor = { Temperature, Humidity, Battery, Online, LastReading };

    private static readonly object Sync = new();
    private static readonly Dictionary<Guid, string[]> Known = new();

    public static void Register(ClimateState state) =>
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => Update(state, DateTimeOffset.UtcNow));

    /// <summary>Writes the current state into the families; series of sensors gone, archived or renamed are removed.</summary>
    public static void Update(ClimateState state, DateTimeOffset now)
    {
        lock (Sync)
        {
            foreach (var (source, count) in state.RequestsToday) Requests.WithLabels(source).Set(count);

            var seen = new HashSet<Guid>();
            foreach (var s in state.Sensors)
            {
                var labels = new[] { s.Sensor.Name, s.Sensor.Room, s.Sensor.Source };
                seen.Add(s.Sensor.Id);
                if (Known.TryGetValue(s.Sensor.Id, out var previous) && !previous.SequenceEqual(labels)) Remove(previous);
                Known[s.Sensor.Id] = labels;

                Set(Temperature, labels, s.TemperatureC);
                Set(Humidity, labels, s.HumidityPercent);
                Set(Battery, labels, s.BatteryPercent);
                Online.WithLabels(labels).Set(state.IsOnline(s, now) ? 1 : 0);
                Set(LastReading, labels, s.ReadUtc?.ToUnixTimeSeconds());
            }
            foreach (var gone in Known.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                Remove(Known[gone]);
                Known.Remove(gone);
            }
        }
    }

    private static void Set(Gauge g, string[] labels, double? value)
    {
        if (value is { } v) g.WithLabels(labels).Set(v);
        else g.RemoveLabelled(labels);
    }

    private static void Remove(string[] labels)
    {
        foreach (var g in PerSensor) g.RemoveLabelled(labels);
    }
}
