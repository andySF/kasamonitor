using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>What happened to one sample.</summary>
public enum ClimateRecordResult
{
    /// <summary>Stored as a new reading.</summary>
    Stored,
    /// <summary>Read, but not different enough from the last stored reading (and the heartbeat not due).</summary>
    Unchanged,
    /// <summary>Offline or without a temperature: nothing stored.</summary>
    Offline,
    /// <summary>The sensor is archived: nothing stored.</summary>
    Archived,
}

/// <summary>
/// Turns samples of every source into sensors and readings: creates the sensor on its first sample, updates its
/// last-seen time and battery, stores a reading when <see cref="ClimateWriteRule"/> says so and publishes the value
/// in <see cref="ClimateState"/>. Also the single place the UI edits sensors through.
/// </summary>
public sealed class ClimateRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ClimateState _state;
    private readonly ILogger<ClimateRecorder>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<(string Source, string ExternalId), ClimateSensor> _sensors = new();
    private readonly Dictionary<Guid, ClimateReading> _lastStored = new();
    private bool _loaded;

    public ClimateRecorder(IServiceScopeFactory scopeFactory, ClimateState state, ILogger<ClimateRecorder>? log = null)
    {
        _scopeFactory = scopeFactory;
        _state = state;
        _log = log;
    }

    /// <summary>Loads the sensors and their last stored readings (once; the published values are the stored ones until the first read).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await LoadCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        if (_loaded) return;
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IClimateRepository>();
        await RemoveBoilerWaterSensorsAsync(repo, ct);
        var sensors = await repo.ListSensorsAsync(includeArchived: true, ct);
        var latest = await repo.LatestReadingsAsync(ct);
        foreach (var s in sensors)
        {
            latest.TryGetValue(s.Id, out var last);
            lock (_sensors)
            {
                _sensors[(s.Source, s.ExternalId)] = s;
                if (last is not null) _lastStored[s.Id] = last;
            }
            _state.Publish(new ClimateSensorLive(s, last?.TemperatureC, last?.HumidityPercent, s.BatteryPercent, last?.TsUtc));
        }
        _loaded = true;
    }

    /// <summary>
    /// Once per process, before the sensors are loaded: removes the boiler's former supply / hot-water sensors (0.9.3: water
    /// temperatures are not climate) with their readings and daily stats. Nothing is lost: the same temperatures stay in
    /// <c>HeatingReadings</c>. Idempotent (nothing left to delete on the next start).
    /// </summary>
    private async Task RemoveBoilerWaterSensorsAsync(IClimateRepository repo, CancellationToken ct)
    {
        var ids = (await repo.ListSensorsAsync(includeArchived: true, ct))
            .Where(s => ViessmannClimate.IsWaterSensor(s.Source, s.ExternalId)).Select(s => s.Id).ToList();
        if (ids.Count == 0) return;
        var (sensors, readings, days) = await repo.DeleteSensorsAsync(ids, ct);
        _log?.LogInformation("Climate: removed {Sensors} boiler water sensors (supply / hot water) with {Readings} readings and {Days} daily rows; " +
                             "the temperatures stay in HeatingReadings", sensors, readings, days);
    }

    /// <summary>The sensors known so far of a source (archived included).</summary>
    public IReadOnlyList<ClimateSensor> SensorsOf(string source)
    {
        lock (_sensors) return _sensors.Values.Where(s => s.Source == source).ToList();
    }

    /// <summary>The newest stored reading time of the sensors of a source (null without any).</summary>
    public DateTimeOffset? LastStoredUtc(string source)
    {
        lock (_sensors)
        {
            var ids = _sensors.Values.Where(s => s.Source == source).Select(s => s.Id).ToHashSet();
            var times = _lastStored.Where(kv => ids.Contains(kv.Key)).Select(kv => kv.Value.TsUtc).ToList();
            return times.Count == 0 ? null : times.Max();
        }
    }

    public async Task<ClimateRecordResult> RecordAsync(ClimateSample sample, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IClimateRepository>();
            var key = (sample.Source, sample.ExternalId);
            ClimateSensor? sensor;
            lock (_sensors) _sensors.TryGetValue(key, out sensor);
            if (sensor is null)
            {
                sensor = await repo.GetOrAddSensorAsync(sample.Source, sample.ExternalId, sample.DefaultName, sample.DefaultRoom, sample.DefaultOutdoor, ct);
                lock (_sensors) _sensors[key] = sensor;
                _log?.LogInformation("Climate: new sensor {Source} {Name}", sensor.Source, sensor.Name);
            }
            if (sensor.IsArchived) return ClimateRecordResult.Archived;

            var current = _state.Get(sensor.Id);
            if (!sample.Online || sample.TemperatureC is null)
            {
                _state.Publish((current ?? new ClimateSensorLive(sensor, null, null, sensor.BatteryPercent, null))
                    with { Sensor = sensor, SourceOnline = false, BatteryPowered = sample.BatteryPowered });
                return ClimateRecordResult.Offline;
            }

            var temp = sample.TemperatureC.Value;
            var battery = sample.BatteryPercent ?? sensor.BatteryPercent;
            await repo.MarkSeenAsync(sensor.Id, sample.TsUtc, sample.BatteryPercent, ct);
            sensor = sensor with { LastSeenUtc = sample.TsUtc, BatteryPercent = battery };
            lock (_sensors) _sensors[key] = sensor;

            var result = ClimateRecordResult.Unchanged;
            ClimateReading? last;
            lock (_sensors) _lastStored.TryGetValue(sensor.Id, out last);
            if (ClimateWriteRule.ShouldWrite(last, sample.TsUtc, temp, sample.HumidityPercent))
            {
                var reading = new ClimateReading(sensor.Id, sample.TsUtc, temp, sample.HumidityPercent, sample.BatteryPercent);
                await repo.AddReadingAsync(reading, ct);
                lock (_sensors) _lastStored[sensor.Id] = reading;
                result = ClimateRecordResult.Stored;
            }
            _state.Publish(new ClimateSensorLive(sensor, temp, sample.HumidityPercent, battery, sample.TsUtc, SourceOnline: true, sample.BatteryPowered));
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Saves name, room and „afară” edited in /clima.</summary>
    public Task<ClimateSensor> UpdateSensorAsync(Guid sensorId, string name, string room, bool isOutdoor, CancellationToken ct) =>
        ChangeAsync(repo => repo.UpdateSensorAsync(sensorId, name, room, isOutdoor, ct), ct);

    /// <summary>Archives a sensor (hidden, no longer stored) or brings it back.</summary>
    public Task<ClimateSensor> SetArchivedAsync(Guid sensorId, bool archived, CancellationToken ct) =>
        ChangeAsync(repo => repo.SetArchivedAsync(sensorId, archived, ct), ct);

    /// <summary>Archives / restores the sensor of (source, external id) if it exists (Govee device ticked or unticked).</summary>
    public async Task SetArchivedAsync(string source, string externalId, bool archived, CancellationToken ct)
    {
        await LoadAsync(ct);
        ClimateSensor? sensor;
        lock (_sensors) _sensors.TryGetValue((source, externalId), out sensor);
        if (sensor is not null && sensor.IsArchived != archived) await SetArchivedAsync(sensor.Id, archived, ct);
    }

    private async Task<ClimateSensor> ChangeAsync(Func<IClimateRepository, Task<ClimateSensor>> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            using var scope = _scopeFactory.CreateScope();
            var sensor = await change(scope.ServiceProvider.GetRequiredService<IClimateRepository>());
            lock (_sensors) _sensors[(sensor.Source, sensor.ExternalId)] = sensor;
            _state.UpdateSensor(sensor);
            return sensor;
        }
        finally
        {
            _gate.Release();
        }
    }
}
