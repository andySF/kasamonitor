using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Workers.Mqtt;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers.Entities;

/// <summary>
/// The generic devices (0.11, <c>dev:{id}</c>): the descriptions stored by <see cref="GenericRecorder"/>, the live values of
/// the <see cref="MqttRegistry"/> (or the last stored ones while the broker is not connected), the history from
/// <c>GenericHourly</c> (+ the open hour from the raw readings) and what the counters counted per local day.
/// </summary>
public sealed class GenericEntitySource : IEntitySource, IEntityCounterSource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly GenericRecorder _recorder;
    private readonly MqttState _mqtt;

    public GenericEntitySource(IServiceScopeFactory scopes, GenericRecorder recorder, MqttState mqtt)
    {
        _scopes = scopes;
        _recorder = recorder;
        _mqtt = mqtt;
    }

    public string KeyPrefix => DeviceRow.GenericPrefix;

    public async Task<EntityDevice?> GetAsync(string deviceKey, DateTimeOffset now, CancellationToken ct)
    {
        if (!DeviceRow.TryGeneric(deviceKey, out var id)) return null;
        await _recorder.LoadAsync(ct);
        if (_recorder.Device(id) is not { } device) return null;
        var snap = _mqtt.Registry.Get(device.ExternalId, now);
        var live = snap?.Entities.ToDictionary(e => e.Spec.UniqueId, StringComparer.Ordinal);
        var entities = new List<DeviceEntity>();
        var values = new Dictionary<string, EntityValue>(StringComparer.Ordinal);
        foreach (var row in _recorder.EntitiesOf(id).Where(r => !r.IsRemoved))
        {
            MqttEntitySnapshotOrNull(live, row.ExternalId, out var e);
            // The live description carries the command (topic, optimistic); the stored one is the same otherwise.
            var description = e is not null ? e.Spec.Describe(deviceKey) : row.Description;
            entities.Add(description);
            values[description.Key] = e is not null && e.HasValue
                ? new EntityValue(description.Key, e.Number, e.Text, e.Ts, e.Available, e.Late, e.LastReadError, e.ReadErrors, e.LastReadErrorUtc)
                : new EntityValue(description.Key, row.LastNumber, row.LastText, row.LastTsUtc, false, false,
                    e?.LastReadError, e?.ReadErrors ?? 0, e?.LastReadErrorUtc);
        }
        var available = !device.IsArchived && (snap?.Available ?? false);
        var last = values.Values.Select(v => v.TsUtc).Max() ?? device.LastSeenUtc;
        // A device routed to the climate section links it (its temperature is there, with the other rooms).
        var rich = _recorder.Classification(id).TemperatureKey is not null ? "/clima" : null;
        return new EntityDevice(deviceKey, device.Integration, device.Name, device.Manufacturer, device.Model, device.Firmware,
            device.Room.Length > 0 ? device.Room : null, available, last, entities, values, rich,
            snap is null && !device.IsArchived ? "Dispozitivul nu e anunțat acum pe broker: valorile sunt ultimele salvate." : null, device.IsArchived,
            available && (snap?.Late ?? false));
    }

    private static void MqttEntitySnapshotOrNull(Dictionary<string, KasaMonitor.Core.Mqtt.MqttEntitySnapshot>? live, string uid,
        out KasaMonitor.Core.Mqtt.MqttEntitySnapshot? e) => e = live is not null && live.TryGetValue(uid, out var x) ? x : null;

    private GenericEntityRow? Row(string deviceKey, string entityKey) =>
        DeviceRow.TryGeneric(deviceKey, out var id) ? _recorder.EntitiesOf(id).FirstOrDefault(r => r.Description.Key == entityKey) : null;

    public async Task<IReadOnlyList<EntityBucket>?> HistoryAsync(string deviceKey, string entityKey, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        await _recorder.LoadAsync(ct);
        if (Row(deviceKey, entityKey) is not { } row) return null;
        var counter = row.Description.StateClass is StateClass.Total or StateClass.TotalIncreasing;
        List<GenericHour> hours;
        using (var scope = _scopes.CreateScope())
            hours = (await scope.ServiceProvider.GetRequiredService<IGenericRepository>().ListHourlyAsync(new[] { row.Id }, fromUtc, toUtc, ct))
                .Select(h => h.Hour).ToList();
        var now = DateTimeOffset.UtcNow;
        if (await _recorder.CurrentHourAsync(row, now, ct) is { } open && open.HourUtc >= fromUtc && open.HourUtc < toUtc)
        {
            hours.RemoveAll(h => h.HourUtc == open.HourUtc);
            hours.Add(open);
        }
        // Counters: what was counted in the hour; the rest: min / time-weighted mean / max (a binary's mean = its share on).
        var points = hours.Where(h => counter ? h.Delta is not null : h.Avg is not null)
            .Select(h => counter ? EntityPoint.Of(h.HourUtc, h.Delta!.Value) : new EntityPoint(h.HourUtc, h.Min ?? h.Avg!.Value, h.Avg!.Value, h.Max ?? h.Avg!.Value));
        return EntityHistoryBuckets.Build(points, fromUtc, toUtc, TimeSpan.FromHours(1));
    }

    public async Task<EntityCounterSummary?> CounterAsync(string deviceKey, string entityKey, DateTimeOffset now, TimeZoneInfo tz, CancellationToken ct)
    {
        await _recorder.LoadAsync(ct);
        if (Row(deviceKey, entityKey) is not { } row || row.Description.StateClass is not (StateClass.Total or StateClass.TotalIncreasing)) return null;
        var localNow = TimeZoneInfo.ConvertTime(now, tz);
        var today = DateOnly.FromDateTime(localNow.DateTime);
        var firstDay = new[] { today.AddDays(-29), new DateOnly(today.Year, today.Month, 1) }.Min();
        var from = Start(firstDay, tz);
        List<GenericHour> hours;
        using (var scope = _scopes.CreateScope())
            hours = (await scope.ServiceProvider.GetRequiredService<IGenericRepository>().ListHourlyAsync(new[] { row.Id }, from, now, ct)).Select(h => h.Hour).ToList();
        if (await _recorder.CurrentHourAsync(row, now, ct) is { } open)
        {
            hours.RemoveAll(h => h.HourUtc == open.HourUtc);
            hours.Add(open);
        }
        // Local days are sums of UTC hours (Romania's offsets are whole hours, DST included).
        var byDay = hours.Where(h => h.Delta is not null)
            .GroupBy(h => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(h.HourUtc, tz).DateTime))
            .ToDictionary(g => g.Key, g => g.Sum(h => h.Delta!.Value));
        double? Sum(Func<DateOnly, bool> pick) => byDay.Where(kv => pick(kv.Key)).Select(kv => (double?)kv.Value).Sum() is { } s && byDay.Keys.Any(pick) ? s : null;
        var days = Enumerable.Range(0, 30).Select(i => today.AddDays(i - 29)).Select(d => (d, byDay.GetValueOrDefault(d))).ToList();
        return new EntityCounterSummary(Sum(d => d == today), Sum(d => d == today.AddDays(-1)), Sum(d => d.Year == today.Year && d.Month == today.Month), days);
    }

    private static DateTimeOffset Start(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
    }
}
