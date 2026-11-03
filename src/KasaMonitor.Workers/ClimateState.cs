using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Heating;

namespace KasaMonitor.Workers;

/// <summary>
/// What the UI and /metrics show about one sensor: the sensor, its last value read (stored or not), when it was read,
/// whether the source reported it online, and whether it runs on batteries (Shelly H&amp;T: longer expected interval).
/// </summary>
public sealed record ClimateSensorLive(
    ClimateSensor Sensor,
    double? TemperatureC,
    double? HumidityPercent,
    int? BatteryPercent,
    DateTimeOffset? ReadUtc,
    bool SourceOnline = true,
    bool BatteryPowered = false,
    TimeSpan? ExpectedInterval = null);

/// <summary>
/// In-memory state of the climate section (singleton), the service the pages use (/clima, the „Afară” row of
/// /centrala, the future home page): the sensors with their last values, a status text per source, the Govee device
/// list and the cloud requests made today per source.
/// </summary>
public sealed class ClimateState
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ClimateSensorLive> _sensors = new();
    private readonly Dictionary<string, string?> _status = new(StringComparer.Ordinal);
    private IReadOnlyList<GoveeDevice>? _goveeDevices;
    private readonly Func<int> _viessmannPollSeconds;

    /// <param name="viessmannPollSeconds">The boiler's poll interval (expected interval of its virtual sensors); default 120 s.</param>
    public ClimateState(Func<int>? viessmannPollSeconds = null, Func<DateTimeOffset>? clock = null, TimeZoneInfo? tz = null)
    {
        _viessmannPollSeconds = viessmannPollSeconds ?? (() => ViessmannApi.DefaultPollIntervalSeconds);
        GoveeCalls = new DailyCallCounter(clock, tz);
        OpenMeteoCalls = new DailyCallCounter(clock, tz);
    }

    public event Action? Changed;

    /// <summary>HTTP requests to Govee made today (local calendar day; the account's limit is 10,000 a day).</summary>
    public DailyCallCounter GoveeCalls { get; }

    /// <summary>HTTP requests to Open-Meteo made today.</summary>
    public DailyCallCounter OpenMeteoCalls { get; }

    /// <summary>Requests today per cloud source (<c>climate_api_requests_today{source}</c>).</summary>
    public IReadOnlyDictionary<string, int> RequestsToday => new Dictionary<string, int>
    {
        [ClimateSources.Govee] = GoveeCalls.Today,
        [ClimateSources.OpenMeteo] = OpenMeteoCalls.Today,
    };

    /// <summary>Not archived sensors: indoor first, by room and name.</summary>
    public IReadOnlyList<ClimateSensorLive> Sensors
    {
        get
        {
            lock (_sync)
                return _sensors.Values.Where(s => !s.Sensor.IsArchived)
                    .OrderBy(s => s.Sensor.IsOutdoor)
                    .ThenBy(s => s.Sensor.Room, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(s => s.Sensor.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(s => s.Sensor.Id)
                    .ToList();
        }
    }

    public ClimateSensorLive? Get(Guid sensorId)
    {
        lock (_sync) return _sensors.GetValueOrDefault(sensorId);
    }

    /// <summary>
    /// The outdoor value to show next to the boiler („Afară” in /centrala): the not archived „afară” sensor with a value
    /// that is not stale, a physical one before Open-Meteo, then the most recently read; null when there is none.
    /// </summary>
    public ClimateSensorLive? Outdoor(DateTimeOffset now) =>
        Sensors.Where(s => s.Sensor.IsOutdoor && s.TemperatureC is not null)
            .OrderBy(s => Freshness(s, now) == ClimateFreshnessLevel.Stale)
            .ThenBy(s => s.Sensor.Source == ClimateSources.OpenMeteo)
            .ThenByDescending(s => s.ReadUtc)
            .FirstOrDefault();

    /// <summary>The interval the sensor's source delivers at.</summary>
    public TimeSpan Expected(ClimateSensorLive s) =>
        ClimateFreshness.Expected(s.Sensor.Source, s.BatteryPowered, _viessmannPollSeconds(), s.ExpectedInterval);

    public ClimateFreshnessLevel Freshness(ClimateSensorLive s, DateTimeOffset now) =>
        ClimateFreshness.Level(s.ReadUtc, now, Expected(s));

    /// <summary>Online = the source reports it online and its last value is not stale (6 expected intervals).</summary>
    public bool IsOnline(ClimateSensorLive s, DateTimeOffset now) => s.SourceOnline && Freshness(s, now) != ClimateFreshnessLevel.Stale;

    public void Publish(ClimateSensorLive live)
    {
        lock (_sync) _sensors[live.Sensor.Id] = live;
        Raise();
    }

    /// <summary>Replaces the sensor record (edited name/room/afară, archived) keeping its values.</summary>
    public void UpdateSensor(ClimateSensor sensor)
    {
        lock (_sync)
            _sensors[sensor.Id] = _sensors.TryGetValue(sensor.Id, out var cur) ? cur with { Sensor = sensor } : new ClimateSensorLive(sensor, null, null, sensor.BatteryPercent, null);
        Raise();
    }

    /// <summary>Why a source reads nothing / failed last (null when it works or is simply not configured).</summary>
    public string? SourceStatus(string source)
    {
        lock (_sync) return _status.GetValueOrDefault(source);
    }

    public void SetSourceStatus(string source, string? status)
    {
        lock (_sync)
        {
            if (_status.GetValueOrDefault(source) == status) return;
            _status[source] = status;
        }
        Raise();
    }

    /// <summary>The account's devices from the last successful list (null before the first one).</summary>
    public IReadOnlyList<GoveeDevice>? GoveeDevices { get { lock (_sync) return _goveeDevices; } }

    public DateTimeOffset? GoveeDevicesUtc { get; private set; }

    public void SetGoveeDevices(IReadOnlyList<GoveeDevice> devices, DateTimeOffset atUtc)
    {
        lock (_sync)
        {
            _goveeDevices = devices;
            GoveeDevicesUtc = atUtc;
        }
        Raise();
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { /* a UI handler must never break the worker */ }
    }
}
