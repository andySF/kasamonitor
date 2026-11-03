using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Core.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers.Mqtt;

/// <summary>
/// The owner of the generic devices (0.11): adds a discovered device (tables <c>GenericDevices</c>, <c>GenericEntities</c>),
/// keeps their descriptions in step with the discovery, stores values by <see cref="GenericWriteRule"/> (batched, at most one
/// row per entity in 10 s, a change inside the window deferred, never lost), rolls them up per hour, purges the raw rows
/// after the retention, and routes the temperature / humidity / battery of a climate-capable device to
/// <see cref="ClimateRecorder"/> (source <c>mqtt</c>, at most one record per sensor in 10 s — R12). The single place that
/// edits, withdraws and restores a generic device (with its routed climate sensor — R2).
/// </summary>
public sealed class GenericRecorder
{
    public static readonly TimeSpan ClimateMinInterval = TimeSpan.FromSeconds(10);
    /// <summary>A number holds this long after it was stored, in the hourly rollup (then a gap).</summary>
    public static readonly TimeSpan NumberCarry = TimeSpan.FromHours(2);
    /// <summary>An on/off or a text holds this long (a door contact publishes only on change).</summary>
    public static readonly TimeSpan StateCarry = TimeSpan.FromDays(7);
    private const int PurgeBatch = 5000;
    /// <summary>R9: the most readings kept in memory while the database refuses them (≈ hours of a house's devices).</summary>
    public const int MaxBuffered = 10_000;
    private static readonly TimeSpan RollupBacklog = TimeSpan.FromDays(14);

    private readonly IServiceScopeFactory _scopes;
    private readonly MqttState _mqtt;
    private readonly ClimateRecorder? _climate;
    private readonly ILogger<GenericRecorder>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<long, GenericDevice> _devices = new();
    private readonly Dictionary<long, Dictionary<string, GenericEntityRow>> _entities = new();
    private readonly Dictionary<long, GenericClassification> _classes = new();
    private readonly Dictionary<long, GenericStored> _lastStored = new();
    private readonly Dictionary<long, GenericReading> _deferred = new();
    private readonly List<GenericReading> _buffer = new();
    private readonly Dictionary<long, DateTimeOffset> _climateRecorded = new();
    private readonly HashSet<long> _climatePending = new();
    private readonly Dictionary<long, bool> _climateAvailable = new();
    private bool _loaded;
    private DateOnly? _lastPurge;

    public GenericRecorder(IServiceScopeFactory scopes, MqttState mqtt, ClimateRecorder? climate = null, ILogger<GenericRecorder>? log = null)
    {
        _scopes = scopes;
        _mqtt = mqtt;
        _climate = climate;
        _log = log;
    }

    public event Action? Changed;

    public static string DeviceKey(long id) => DeviceRow.GenericKey(id);

    public async Task LoadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await LoadCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        if (_loaded) return;
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGenericRepository>();
        var devices = await repo.ListDevicesAsync(includeArchived: true, ct);
        var entities = await repo.ListEntitiesAsync(null, ct);
        lock (_sync)
        {
            foreach (var d in devices) _devices[d.Id] = d;
            foreach (var g in entities.GroupBy(e => e.DeviceId))
                _entities[g.Key] = g.ToDictionary(e => e.ExternalId, e => WithKey(e), StringComparer.Ordinal);
            foreach (var e in entities.Where(e => e.LastTsUtc is not null))
                _lastStored[e.Id] = new GenericStored(e.LastTsUtc!.Value, e.LastNumber, e.LastText);
            foreach (var d in devices) _classes[d.Id] = ClassifyCore(d.Id);
        }
        _loaded = true;
    }

    // ---- read side (the catalog, the generated page, the metrics, the history) ----

    public IReadOnlyList<GenericDevice> Devices
    {
        get { lock (_sync) return _devices.Values.OrderBy(d => d.Id).ToList(); }
    }

    public GenericDevice? Device(long id)
    {
        lock (_sync) return _devices.GetValueOrDefault(id);
    }

    public GenericDevice? ByExternal(string integration, string externalId)
    {
        lock (_sync) return _devices.Values.FirstOrDefault(d => d.Integration == integration && d.ExternalId == externalId);
    }

    /// <summary>The entities of a device with their keys (<c>dev:{id}/{slug}</c>), removed ones included.</summary>
    public IReadOnlyList<GenericEntityRow> EntitiesOf(long deviceId)
    {
        lock (_sync) return _entities.TryGetValue(deviceId, out var e) ? e.Values.OrderBy(x => x.Id).ToList() : Array.Empty<GenericEntityRow>();
    }

    public GenericClassification Classification(long deviceId)
    {
        lock (_sync) return _classes.TryGetValue(deviceId, out var c) ? c : ClassifyCore(deviceId);
    }

    /// <summary>The keys of the entities routed to the climate section (shown there, not repeated in „Alte dispozitive” and /metrics).</summary>
    public IReadOnlySet<string> ClimateRoutedKeys()
    {
        lock (_sync)
            return _classes.Values.SelectMany(c => new[] { c.TemperatureKey, c.HumidityKey, c.BatteryKey }).Where(k => k is not null)
                .Select(k => k!).ToHashSet(StringComparer.Ordinal);
    }

    private GenericClassification ClassifyCore(long deviceId) =>
        GenericClassifier.Classify(_entities.TryGetValue(deviceId, out var e) ? e.Values.Where(x => !x.IsRemoved).Select(x => x.Description).ToList() : []);

    private static GenericEntityRow WithKey(GenericEntityRow e) =>
        e with { Description = e.Description with { Key = EntityKeys.Of(DeviceKey(e.DeviceId), MqttEntityMapper.Slug(e.ExternalId)) } };

    // ---- discovery side ----

    /// <summary>Adds a discovered device and its entities (idempotent: an existing device is brought in step).</summary>
    public async Task<GenericDevice> AdoptAsync(MqttDeviceSnapshot snap, DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            GenericDevice device;
            using (var scope = _scopes.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IGenericRepository>();
                var auto = GenericClassifier.Classify(snap.Entities.Select(e => e.Spec.Describe("dev:0")).ToList()).Category;
                device = await repo.AddDeviceAsync(IntegrationKeys.Mqtt, snap.ExternalId, snap.Name, snap.Info?.Manufacturer, snap.Info?.Model,
                    snap.Info?.SwVersion, snap.Info?.ViaDevice, snap.Info?.SuggestedArea ?? "", auto, now, ct);
                lock (_sync) _devices[device.Id] = device;
            }
            await SyncCoreAsync(device, snap, Array.Empty<string>(), now, ct);
            _log?.LogInformation("MQTT: device added {Name} ({Entities} entities)", device.Name, snap.Entities.Count);
            Raise();
            return Device(device.Id)!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A discovery message changed an added device: descriptions rewritten, removed entities marked, category recomputed.</summary>
    public async Task SyncAsync(MqttDeviceSnapshot snap, IReadOnlyCollection<string> removedUniqueIds, DateTimeOffset now, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            if (ByExternal(IntegrationKeys.Mqtt, snap.ExternalId) is not { } device) return;
            await SyncCoreAsync(device, snap, removedUniqueIds, now, ct);
            Raise();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Only entities were removed (their device may be gone from the registry).</summary>
    public async Task MarkRemovedAsync(string externalId, IReadOnlyCollection<string> removedUniqueIds, CancellationToken ct)
    {
        if (removedUniqueIds.Count == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            await LoadCoreAsync(ct);
            if (ByExternal(IntegrationKeys.Mqtt, externalId) is not { } device) return;
            using var scope = _scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IGenericRepository>().MarkEntitiesRemovedAsync(device.Id, removedUniqueIds, ct);
            lock (_sync)
            {
                if (_entities.TryGetValue(device.Id, out var map))
                    foreach (var uid in removedUniqueIds)
                        if (map.TryGetValue(uid, out var e)) map[uid] = e with { IsRemoved = true };
                _classes[device.Id] = ClassifyCore(device.Id);
            }
            Raise();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SyncCoreAsync(GenericDevice device, MqttDeviceSnapshot snap, IReadOnlyCollection<string> removed, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGenericRepository>();
        var key = DeviceKey(device.Id);
        var upserts = snap.Entities.Select(e => new GenericEntityUpsert(e.Spec.UniqueId, e.Spec.Component, e.Spec.Describe(key), e.Spec.DiscoveryJson)).ToList();
        var rows = await repo.UpsertEntitiesAsync(device.Id, IntegrationKeys.Mqtt, upserts, ct);
        if (removed.Count > 0) await repo.MarkEntitiesRemovedAsync(device.Id, removed, ct);
        var auto = GenericClassifier.Classify(upserts.Select(u => u.Description).ToList()).Category;
        await repo.UpdateDeviceInfoAsync(device.Id, snap.Name, snap.Info?.Manufacturer, snap.Info?.Model, snap.Info?.SwVersion, snap.Info?.ViaDevice, auto, now, ct);
        var fresh = await repo.GetDeviceAsync(device.Id, ct) ?? device;
        var all = await repo.ListEntitiesAsync(device.Id, ct);
        lock (_sync)
        {
            _devices[device.Id] = fresh;
            _entities[device.Id] = all.ToDictionary(e => e.ExternalId, WithKey, StringComparer.Ordinal);
            _classes[device.Id] = ClassifyCore(device.Id);
            // Entities moved here from another device (same unique id): drop them from the old one.
            foreach (var (other, map) in _entities.Where(kv => kv.Key != device.Id))
                foreach (var r in rows.Where(r => map.ContainsKey(r.ExternalId))) map.Remove(r.ExternalId);
        }
    }

    // ---- values ----

    /// <summary>Values of a state message: the ones of added, not withdrawn devices go through the write rule (buffered).</summary>
    public async Task OnValuesAsync(IReadOnlyList<MqttValueUpdate> updates, CancellationToken ct)
    {
        if (updates.Count == 0) return;
        await LoadAsync(ct);
        // R3 (round 2): a value of an entity whose availability says „unavailable” is shown, never stored.
        var unavailable = updates.Select(u => u.DeviceExternalId).Distinct(StringComparer.Ordinal)
            .Select(ext => _mqtt.Registry.Get(ext, updates[0].TsUtc)).Where(d => d is not null)
            .SelectMany(d => d!.Entities.Where(e => e.Spec.Availability.Count > 0 && !e.Available).Select(e => (d!.ExternalId, e.Spec.UniqueId)))
            .ToHashSet();
        lock (_sync)
        {
            foreach (var u in updates)
            {
                var device = _devices.Values.FirstOrDefault(d => d.Integration == IntegrationKeys.Mqtt && d.ExternalId == u.DeviceExternalId);
                if (device is null || device.IsArchived) continue;
                if (unavailable.Contains((u.DeviceExternalId, u.Spec.UniqueId))) continue;
                if (!_entities.TryGetValue(device.Id, out var map) || !map.TryGetValue(u.Spec.UniqueId, out var row)) continue;
                var reading = new GenericReading(row.Id, u.TsUtc, u.Value.Number, u.Value.Text);
                GenericStored? last = _lastStored.TryGetValue(row.Id, out var l) ? l : null;
                var threshold = GenericWriteRule.Threshold(row.Description, last?.Number);
                switch (GenericWriteRule.Decide(last, u.TsUtc, u.Value.Number, u.Value.Text, threshold))
                {
                    case GenericWriteDecision.Write:
                        _buffer.Add(reading);
                        _lastStored[row.Id] = new GenericStored(u.TsUtc, u.Value.Number, u.Value.Text);
                        _deferred.Remove(row.Id);
                        break;
                    case GenericWriteDecision.Defer:
                        _deferred[row.Id] = reading;
                        break;
                    default:
                        // An unchanged value cancels a deferred change back to the stored value.
                        if (_deferred.TryGetValue(row.Id, out var pending) && last is { } st && pending.Number == st.Number && pending.Text == st.Text) _deferred.Remove(row.Id);
                        break;
                }
                var c = _classes.GetValueOrDefault(device.Id);
                if (c?.TemperatureKey is not null && row.Description.Key is var k && (k == c.TemperatureKey || k == c.HumidityKey || k == c.BatteryKey))
                    _climatePending.Add(device.Id);
            }
        }
    }

    /// <summary>
    /// Writes the buffered values and the deferred changes whose 10 s window passed (stored at the end of the window: the
    /// value held until then), and records the climate of the routed devices (new values, availability changes).
    /// </summary>
    public async Task FlushAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<GenericReading> batch;
        lock (_sync)
        {
            foreach (var (id, d) in _deferred.ToList())
            {
                var last = _lastStored.GetValueOrDefault(id);
                var at = last.TsUtc + GenericWriteRule.MinInterval;
                if (now < at) continue;
                var reading = d with { TsUtc = at > d.TsUtc ? at : d.TsUtc };
                _buffer.Add(reading);
                _lastStored[id] = new GenericStored(reading.TsUtc, reading.Number, reading.Text);
                _deferred.Remove(id);
            }
            batch = _buffer.ToList();
            _buffer.Clear();
        }
        if (batch.Count > 0)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IGenericRepository>().AddReadingsAsync(batch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // R9 (round 2): the batch goes back to the front of the buffer and is written at the next flush (bounded).
                lock (_sync)
                {
                    _buffer.InsertRange(0, batch);
                    if (_buffer.Count > MaxBuffered) _buffer.RemoveRange(0, _buffer.Count - MaxBuffered);
                }
                _log?.LogWarning(ex, "MQTT: {Count} generic readings could not be stored; kept for the next flush", batch.Count);
            }
        }
        await RecordClimateAsync(now, ct);
    }

    private async Task RecordClimateAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_climate is null) return;
        List<(GenericDevice Device, GenericClassification Class)> routed;
        lock (_sync)
            routed = _devices.Values.Where(d => !d.IsArchived && _classes.GetValueOrDefault(d.Id)?.TemperatureKey is not null)
                .Select(d => (d, _classes[d.Id])).ToList();
        foreach (var (device, cls) in routed)
        {
            if (_mqtt.Registry.Get(device.ExternalId, now) is not { } snap) continue;
            MqttEntitySnapshot? Find(string? key) => key is null ? null : snap.Entities.FirstOrDefault(e => EntityKeys.Of(DeviceKey(device.Id), e.Spec.Slug) == key);
            var temp = Find(cls.TemperatureKey);
            // R3: a temperature known only from a retained message has no reading time: nothing to record.
            if (temp is null || temp.Ts is null) continue;
            bool pending, availabilityChanged;
            lock (_sync)
            {
                pending = _climatePending.Contains(device.Id);
                availabilityChanged = _climateAvailable.TryGetValue(device.Id, out var was) && was != temp.Available;
                if (_climateRecorded.TryGetValue(device.Id, out var at) && now - at < ClimateMinInterval) continue;
                if (!pending && !availabilityChanged) continue;
                _climatePending.Remove(device.Id);
                _climateRecorded[device.Id] = now;
                _climateAvailable[device.Id] = temp.Available;
            }
            var hum = Find(cls.HumidityKey);
            var bat = Find(cls.BatteryKey);
            var humidity = hum?.Number;
            var battery = bat?.Number is { } b ? (int?)(int)Math.Round(b) : null;
            // The newest of the three: a humidity change alone is a new reading too.
            var ts = new[] { temp.Ts, hum?.Ts, bat?.Ts }.Max() ?? now;
            var sample = new ClimateSample(ClimateSources.Mqtt, device.ExternalId, device.Name, ts, temp.Number, humidity, battery,
                Online: temp.Available && temp.Number is not null, DefaultRoom: device.Room, BatteryPowered: battery is not null,
                ExpectedInterval: ClimateFreshness.MqttInterval(temp.Spec.ExpireAfterSeconds));
            try { await _climate.RecordAsync(sample, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "MQTT: climate of {Device} could not be recorded", device.Name); }
        }
    }

    /// <summary>Rolls the closed hours of every entity up (the last two again, for late values); returns the rows written.</summary>
    public async Task<int> RollupAsync(DateTimeOffset now, CancellationToken ct)
    {
        await LoadAsync(ct);
        var closed = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        List<GenericEntityRow> entities;
        lock (_sync) entities = _entities.Values.SelectMany(m => m.Values).ToList();
        var written = 0;
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGenericRepository>();
        var last = await repo.LastHourlyAsync(ct);
        foreach (var e in entities)
        {
            if (e.Description.DataType == EntityDataType.Text && e.Description.Kind != EntityKind.Binary) continue;
            // From the hour before the last rolled one (late values), else from the hour the device was added.
            var first = Device(e.DeviceId)?.FirstSeenUtc ?? closed - RollupBacklog;
            var from = last.TryGetValue(e.Id, out var l) ? l.AddHours(-1) : new DateTimeOffset(first.Year, first.Month, first.Day, first.Hour, 0, 0, TimeSpan.Zero);
            if (from < closed - RollupBacklog) from = closed - RollupBacklog;
            if (from >= closed) continue;
            var points = (await repo.ListReadingsAsync(e.Id, from, closed, ct)).Select(r => new GenericPoint(r.TsUtc, r.Number, r.Text)).ToList();
            var before = await repo.LastReadingBeforeAsync(e.Id, from, ct);
            var carry = e.Description.DataType == EntityDataType.Number ? NumberCarry : StateCarry;
            var hours = GenericRollup.Hours(points, before is null ? null : new GenericPoint(before.TsUtc, before.Number, before.Text),
                e.Description.StateClass, from, closed, carry);
            await repo.UpsertHourlyAsync(e.Id, hours, ct);
            written += hours.Count;
        }
        return written;
    }

    /// <summary>The current (open) hour of an entity, rolled up from its stored readings (the last bar of the page charts).</summary>
    public async Task<GenericHour?> CurrentHourAsync(GenericEntityRow e, DateTimeOffset now, CancellationToken ct)
    {
        var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGenericRepository>();
        var points = (await repo.ListReadingsAsync(e.Id, hour, hour.AddHours(1), ct)).Select(r => new GenericPoint(r.TsUtc, r.Number, r.Text)).ToList();
        var before = await repo.LastReadingBeforeAsync(e.Id, hour, ct);
        var carry = e.Description.DataType == EntityDataType.Number ? NumberCarry : StateCarry;
        return GenericRollup.Hours(points, before is null ? null : new GenericPoint(before.TsUtc, before.Number, before.Text), e.Description.StateClass,
            hour, hour.AddHours(1), carry).FirstOrDefault();
    }

    /// <summary>Deletes the raw readings older than the retention, once a day (0 days = never).</summary>
    public async Task<int> PurgeAsync(DateTimeOffset now, int retentionDays, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (retentionDays <= 0 || _lastPurge == today) return 0;
        _lastPurge = today;
        using var scope = _scopes.CreateScope();
        var n = await scope.ServiceProvider.GetRequiredService<IGenericRepository>().DeleteReadingsBeforeAsync(now.AddDays(-retentionDays), PurgeBatch, ct);
        if (n > 0) _log?.LogInformation("MQTT: {Rows} generic readings older than {Days} days deleted (the hourly rollups stay)", n, retentionDays);
        return n;
    }

    // ---- edits (the expanded row) ----

    /// <summary>Name (empty = the device's own), room, category override; the routed climate sensor follows the name and the room.</summary>
    public async Task<GenericDevice> UpdateDeviceAsync(long id, string name, string room, DeviceCategory? categoryOverride, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        GenericDevice device;
        try
        {
            await LoadCoreAsync(ct);
            using var scope = _scopes.CreateScope();
            device = await scope.ServiceProvider.GetRequiredService<IGenericRepository>().UpdateDeviceAsync(id, name, room, categoryOverride, ct);
            lock (_sync) _devices[id] = device;
        }
        finally
        {
            _gate.Release();
        }
        if (_climate?.SensorsOf(ClimateSources.Mqtt).FirstOrDefault(s => s.ExternalId == device.ExternalId) is { } sensor)
            await _climate.UpdateSensorAsync(sensor.Id, device.Name, device.Room, sensor.IsOutdoor, ct);
        Raise();
        return device;
    }

    /// <summary>R2: withdraws (no longer stored, hidden) or brings back a device together with its routed climate sensor.</summary>
    public async Task<GenericDevice?> SetArchivedAsync(long id, bool archived, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        GenericDevice device;
        try
        {
            await LoadCoreAsync(ct);
            if (Device(id) is null) return null;
            using var scope = _scopes.CreateScope();
            device = await scope.ServiceProvider.GetRequiredService<IGenericRepository>().SetArchivedAsync(id, archived, ct);
            lock (_sync)
            {
                _devices[id] = device;
                foreach (var e in _entities.GetValueOrDefault(id)?.Values ?? Enumerable.Empty<GenericEntityRow>()) _deferred.Remove(e.Id);
            }
        }
        finally
        {
            _gate.Release();
        }
        if (_climate is not null) await _climate.SetArchivedAsync(ClimateSources.Mqtt, device.ExternalId, archived, ct);
        Raise();
        return device;
    }

    private void Raise()
    {
        try { Changed?.Invoke(); } catch { /* a UI handler must never break the worker */ }
    }
}
