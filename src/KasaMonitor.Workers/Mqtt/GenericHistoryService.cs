using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Core.Reports;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers.Mqtt;

/// <summary>How a generic entity is drawn in the history: a measurement (line), a counter (bars of Σ) or an on/off (time on).</summary>
public enum GenericHistoryKind
{
    Measurement,
    Counter,
    Binary,
}

/// <summary>One entity of „Alte dispozitive”: its device, names, unit, kind, one stat per bar and the whole period.</summary>
public sealed record HistoryGenericEntity(string DeviceKey, string Device, string? Room, string Entity, string? Unit, int Decimals, GenericHistoryKind Kind,
    IReadOnlyList<GenericBucketStat> Buckets, GenericBucketStat Period);

/// <summary>The „Alte dispozitive” section of /history (0.11).</summary>
public sealed record HistoryGeneric(IReadOnlyList<ReportBucket> Buckets, IReadOnlyList<HistoryGenericEntity> Entities);

/// <summary>
/// The generic devices in /history (0.11): the entities ticked „Istoric” (by default the measurements and counters) of the added,
/// not withdrawn devices, from <c>GenericHourly</c> (+ the open hour) — except the ones routed to the climate section, which
/// stay there (not repeated). Their power / energy never enters the house totals (demarcation rule).
/// </summary>
public sealed class GenericHistoryService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly GenericRecorder _recorder;
    private readonly Func<DateTimeOffset> _clock;

    public GenericHistoryService(IServiceScopeFactory scopes, GenericRecorder recorder, Func<DateTimeOffset>? clock = null)
    {
        _scopes = scopes;
        _recorder = recorder;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<HistoryGeneric> GetAsync(ReportPeriod period, TimeZoneInfo tz, CancellationToken ct)
    {
        await _recorder.LoadAsync(ct);
        var buckets = period.Buckets(tz);
        var routed = _recorder.ClimateRoutedKeys();
        using var scope = _scopes.CreateScope();
        var prefs = await scope.ServiceProvider.GetRequiredService<IEntityPrefsRepository>().ListAllAsync(ct);
        var picked = new List<(GenericDevice Device, GenericEntityRow Row, EntityPrefs? Prefs, GenericHistoryKind Kind)>();
        foreach (var d in _recorder.Devices.Where(d => !d.IsArchived).OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            foreach (var row in _recorder.EntitiesOf(d.Id).Where(e => !e.IsRemoved && e.Description.Problem is null)
                         .OrderBy(e => EntityViewRules.ClassRank(e.Description.Class)).ThenBy(e => e.Description.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var e = row.Description;
                var p = prefs.GetValueOrDefault(e.Key);
                if (routed.Contains(e.Key) || !EntityDefaults.EffectiveInHistory(e, p)) continue;
                GenericHistoryKind? kind = e.DataType == EntityDataType.Boolean ? GenericHistoryKind.Binary
                    : e.DataType != EntityDataType.Number ? null
                    : e.StateClass is StateClass.Total or StateClass.TotalIncreasing ? GenericHistoryKind.Counter : GenericHistoryKind.Measurement;
                if (kind is { } k) picked.Add((d, row, p, k));
            }
        if (picked.Count == 0 || buckets.Count == 0) return new HistoryGeneric(buckets, Array.Empty<HistoryGenericEntity>());

        var from = buckets[0].StartUtc;
        var to = buckets[^1].EndUtc;
        var hours = (await scope.ServiceProvider.GetRequiredService<IGenericRepository>().ListHourlyAsync(picked.Select(x => x.Row.Id).ToList(), from, to, ct))
            .GroupBy(h => h.EntityId).ToDictionary(g => g.Key, g => g.Select(x => x.Hour).ToList());
        var now = _clock();
        var windows = buckets.Select(b => (b.StartUtc, b.EndUtc)).ToList();
        var result = new List<HistoryGenericEntity>();
        foreach (var (d, row, p, kind) in picked)
        {
            var list = hours.GetValueOrDefault(row.Id) ?? new List<GenericHour>();
            if (await _recorder.CurrentHourAsync(row, now, ct) is { } open && open.HourUtc >= from && open.HourUtc < to)
            {
                list.RemoveAll(h => h.HourUtc == open.HourUtc);
                list.Add(open);
            }
            var e = row.Description;
            result.Add(new HistoryGenericEntity(DeviceRow.GenericKey(d.Id), d.Name, d.Room.Length > 0 ? d.Room : null, EntityDefaults.Name(e, p),
                EntityDefaults.Unit(e, p), EntityDefaults.Decimals(e, p), kind, GenericHistory.Aggregate(list, windows),
                GenericHistory.Aggregate(list, new[] { (from, to) })[0]));
        }
        return new HistoryGeneric(buckets, result);
    }
}
