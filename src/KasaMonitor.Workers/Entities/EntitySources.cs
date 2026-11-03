using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers.Entities;

/// <summary>
/// Plugs and meter channels as entities (0.11): the live state's last reading (<see cref="EntityProjections.Plug"/>); the
/// power and voltage history from <c>LiveMinuteAggregates</c> (streamed channels only). The relay stays commanded by
/// <see cref="RelayControlService"/> on the plug's own pages.
/// </summary>
public sealed class PlugEntitySource : IEntitySource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IPlugStateBroadcaster _live;

    public PlugEntitySource(IServiceScopeFactory scopes, IPlugStateBroadcaster live)
    {
        _scopes = scopes;
        _live = live;
    }

    public string KeyPrefix => DeviceRow.PlugPrefix;

    public async Task<EntityDevice?> GetAsync(string deviceKey, DateTimeOffset now, CancellationToken ct)
    {
        if (!DeviceRow.TryGuid(deviceKey, DeviceRow.PlugPrefix, out var id)) return null;
        Plug? plug;
        using (var scope = _scopes.CreateScope())
            plug = (await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct)).FirstOrDefault(p => p.Id == id);
        if (plug is null) return null;
        _live.Snapshot().TryGetValue(id, out var st);
        var online = st is { IsOnline: true };
        var read = st is { LastReading: not null } ? st.LastSampleUtc : (DateTimeOffset?)null;
        var set = EntityProjections.Plug(deviceKey, st?.LastReading, read, online);
        return new EntityDevice(deviceKey, PlugProtocols.Integration(plug.Protocol), plug.Alias, null, DeviceCatalogRules.Model(plug),
            plug.SwVer.Length > 0 ? plug.SwVer : null, null, online, read, set.Entities, set.Values, DeviceCatalogRules.PlugDataUrl(plug),
            plug.IsSupported ? null : plug.UnsupportedReason, plug.IsArchived);
    }

    public async Task<IReadOnlyList<EntityBucket>?> HistoryAsync(string deviceKey, string entityKey, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (!DeviceRow.TryGuid(deviceKey, DeviceRow.PlugPrefix, out var id)) return null;
        var power = entityKey == EntityKeys.Of(deviceKey, "power");
        var voltage = entityKey == EntityKeys.Of(deviceKey, "voltage");
        if (!power && !voltage) return null;
        using var scope = _scopes.CreateScope();
        var minutes = await scope.ServiceProvider.GetRequiredService<ILiveRepository>().ListMinutesAsync(id, fromUtc, toUtc, ct);
        var points = power
            ? minutes.Select(m => new EntityPoint(m.MinuteUtc, m.MinW, m.AvgW, m.MaxW, m.CoveredSeconds))
            : minutes.Where(m => m.AvgVoltageV is not null)
                .Select(m => new EntityPoint(m.MinuteUtc, m.AvgVoltageV!.Value, m.AvgVoltageV!.Value, m.AvgVoltageV!.Value, m.CoveredSeconds));
        return EntityHistoryBuckets.Build(points, fromUtc, toUtc, TimeSpan.FromHours(1));
    }
}

/// <summary>Climate sensors as entities: the value in <see cref="ClimateState"/>; temperature and humidity history from <c>ClimateReadings</c>.</summary>
public sealed class ClimateEntitySource : IEntitySource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ClimateState _climate;

    public ClimateEntitySource(IServiceScopeFactory scopes, ClimateState climate)
    {
        _scopes = scopes;
        _climate = climate;
    }

    public string KeyPrefix => DeviceRow.ClimatePrefix;

    public async Task<EntityDevice?> GetAsync(string deviceKey, DateTimeOffset now, CancellationToken ct)
    {
        if (!DeviceRow.TryGuid(deviceKey, DeviceRow.ClimatePrefix, out var id)) return null;
        var st = _climate.Get(id);
        var sensor = st?.Sensor;
        if (sensor is null)
        {
            using var scope = _scopes.CreateScope();
            sensor = (await scope.ServiceProvider.GetRequiredService<IClimateRepository>().ListSensorsAsync(includeArchived: true, ct)).FirstOrDefault(s => s.Id == id);
        }
        if (sensor is null) return null;
        var online = st is not null && _climate.IsOnline(st, now);
        var set = EntityProjections.Climate(deviceKey, st?.TemperatureC, st?.HumidityPercent, st?.BatteryPercent ?? sensor.BatteryPercent, st?.ReadUtc, online);
        return new EntityDevice(deviceKey, sensor.Source, sensor.Name, ClimateSources.DisplayName(sensor.Source), null, null,
            DeviceCatalogRules.Room(sensor), online, st?.ReadUtc ?? sensor.LastSeenUtc, set.Entities, set.Values, "/clima", null, sensor.IsArchived);
    }

    public async Task<IReadOnlyList<EntityBucket>?> HistoryAsync(string deviceKey, string entityKey, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        if (!DeviceRow.TryGuid(deviceKey, DeviceRow.ClimatePrefix, out var id)) return null;
        var temperature = entityKey == EntityKeys.Of(deviceKey, "temperature");
        var humidity = entityKey == EntityKeys.Of(deviceKey, "humidity");
        if (!temperature && !humidity) return null;
        using var scope = _scopes.CreateScope();
        var readings = await scope.ServiceProvider.GetRequiredService<IClimateRepository>().ListReadingsAsync(new[] { id }, fromUtc, toUtc, ct);
        IReadOnlyList<ClimateReading> rows = readings.TryGetValue(id, out var list) ? list : Array.Empty<ClimateReading>();
        var points = temperature
            ? rows.Select(r => EntityPoint.Of(r.TsUtc, r.TemperatureC))
            : rows.Where(r => r.HumidityPercent is not null).Select(r => EntityPoint.Of(r.TsUtc, r.HumidityPercent!.Value));
        return EntityHistoryBuckets.Build(points, fromUtc, toUtc, TimeSpan.FromHours(1));
    }
}

/// <summary>The boiler as entities (the ~35 values of <c>HeatingSnapshot</c>); its history stays on /centrala.</summary>
public sealed class HeatingEntitySource : IEntitySource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly HeatingState _heating;

    public HeatingEntitySource(IServiceScopeFactory scopes, HeatingState heating)
    {
        _scopes = scopes;
        _heating = heating;
    }

    public string KeyPrefix => DeviceRow.HeatingPrefix;

    public async Task<EntityDevice?> GetAsync(string deviceKey, DateTimeOffset now, CancellationToken ct)
    {
        if (!DeviceRow.TryGuid(deviceKey, DeviceRow.HeatingPrefix, out var id)) return null;
        KasaMonitor.Core.Heating.HeatingDevice? device;
        using (var scope = _scopes.CreateScope())
            device = (await scope.ServiceProvider.GetRequiredService<IHeatingRepository>().ListDevicesAsync(ct)).FirstOrDefault(d => d.Id == id);
        if (device is null) return null;
        var current = _heating.Current is { } c && c.Device.Id == id ? c : null;
        var online = current is not null && device.Enabled && _heating.IsOnline(now);
        var set = EntityProjections.Heating(deviceKey, current?.Snapshot, online);
        return new EntityDevice(deviceKey, IntegrationKeys.Viessmann, device.Name, "Viessmann", device.ModelId.Length > 0 ? device.ModelId : "ViCare", null, null,
            online, current?.Snapshot?.ReadUtc ?? current?.LastSuccessUtc ?? device.LastSeenUtc, set.Entities, set.Values, "/centrala",
            device.LastError, !device.Enabled);
    }

    public Task<IReadOnlyList<EntityBucket>?> HistoryAsync(string deviceKey, string entityKey, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EntityBucket>?>(null);
}

/// <summary>A Miele appliance as entities (its <c>MieleStateValues</c>); cycles and history stay on /aparate.</summary>
public sealed class MieleEntitySource : IEntitySource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly MieleState _miele;

    public MieleEntitySource(IServiceScopeFactory scopes, MieleState miele)
    {
        _scopes = scopes;
        _miele = miele;
    }

    public string KeyPrefix => DeviceRow.MielePrefix;

    public async Task<EntityDevice?> GetAsync(string deviceKey, DateTimeOffset now, CancellationToken ct)
    {
        if (!DeviceRow.TryMiele(deviceKey, out var id)) return null;
        var live = _miele.Get(id);
        var device = live?.Device;
        if (device is null)
        {
            using var scope = _scopes.CreateScope();
            device = (await scope.ServiceProvider.GetRequiredService<IMieleRepository>().ListDevicesAsync(includeArchived: true, ct)).FirstOrDefault(d => d.Id == id);
        }
        if (device is null) return null;
        var online = live is not null && _miele.IsOnline(live, now);
        var set = EntityProjections.Miele(deviceKey, live?.Values, live?.StateUtc, online);
        var model = device.TechType.Length > 0 ? $"{device.TypeText} · {device.TechType}" : device.TypeText;
        return new EntityDevice(deviceKey, IntegrationKeys.Miele, device.Alias, "Miele", model, null, null, online,
            live?.StateUtc ?? device.LastSeenUtc, set.Entities, set.Values, "/aparate", null, device.IsArchived);
    }

    public Task<IReadOnlyList<EntityBucket>?> HistoryAsync(string deviceKey, string entityKey, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EntityBucket>?>(null);
}

/// <summary>
/// The generated device page's service (0.11): dispatches a /dispozitive key to its <see cref="IEntitySource"/> by prefix,
/// and reads / saves the per-entity preferences (<c>EntityPrefs</c>).
/// </summary>
public sealed class DeviceEntityService
{
    private readonly IReadOnlyList<IEntitySource> _sources;
    private readonly IServiceScopeFactory _scopes;

    public DeviceEntityService(IEnumerable<IEntitySource> sources, IServiceScopeFactory scopes)
    {
        _sources = sources.ToList();
        _scopes = scopes;
    }

    public IReadOnlyList<IEntitySource> Sources => _sources;

    public IEntitySource? SourceFor(string? deviceKey) =>
        deviceKey is null ? null : _sources.FirstOrDefault(s => deviceKey.StartsWith(s.KeyPrefix, StringComparison.Ordinal));

    public Task<EntityDevice?> GetAsync(string deviceKey, DateTimeOffset now, CancellationToken ct) =>
        SourceFor(deviceKey)?.GetAsync(deviceKey, now, ct) ?? Task.FromResult<EntityDevice?>(null);

    public Task<IReadOnlyList<EntityBucket>?> HistoryAsync(string deviceKey, string entityKey, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        SourceFor(deviceKey)?.HistoryAsync(deviceKey, entityKey, fromUtc, toUtc, ct) ?? Task.FromResult<IReadOnlyList<EntityBucket>?>(null);

    /// <summary>What a counter counted per local day (generic counters; null for the other sources).</summary>
    public Task<EntityCounterSummary?> CounterAsync(string deviceKey, string entityKey, DateTimeOffset now, TimeZoneInfo tz, CancellationToken ct) =>
        SourceFor(deviceKey) is IEntityCounterSource c ? c.CounterAsync(deviceKey, entityKey, now, tz, ct) : Task.FromResult<EntityCounterSummary?>(null);

    /// <summary>The stored preferences of one device's entities, by entity key.</summary>
    public async Task<IReadOnlyDictionary<string, EntityPrefs>> PrefsAsync(string deviceKey, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IEntityPrefsRepository>().ListAsync(deviceKey + EntityKeys.Separator, ct);
    }

    /// <summary>Saves one entity's preferences („comenzi permise” stays off in 0.11, R8).</summary>
    public async Task SavePrefsAsync(EntityPrefs prefs, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEntityPrefsRepository>().SaveAsync(prefs with { CommandsEnabled = false }, ct);
    }
}
