using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Workers.Mqtt;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// The <c>entity_*</c> families of the generic devices on /metrics (0.11, R5): one family per class with its canonical unit
/// (<see cref="GenericMetricFamilies"/>), <c>entity_state</c> for on/off, <c>entity_value{unit}</c> for the classes without a
/// family, <c>entity_online</c> per device. Labels <c>device</c>, <c>entity</c>, <c>room</c>, <c>integration</c> — never an
/// id or a MAC. Only the entities ticked „Metrici” (by default the main measured classes, not diagnostics nor texts); the
/// entities routed to the climate section appear only in <c>climate_*</c>. A renamed / withdrawn device's series are
/// removed. Above the series limit (setting, default 500) the rest is skipped with a warning (log + MQTT tab).
/// Refreshed right before every scrape, like <see cref="ClimateMetrics"/>.
/// </summary>
public static class GenericMetrics
{
    public static readonly string[] Labels = { "device", "entity", "room", "integration" };
    public static readonly string[] ValueLabels = { "device", "entity", "room", "integration", "unit" };
    public static readonly string[] OnlineLabels = { "device", "room", "integration" };

    /// <summary>The catalog shown on /integration.</summary>
    public static readonly IReadOnlyList<KasaMetrics.MetricInfo> Catalog = GenericMetricFamilies.All
        .Select(f => new KasaMetrics.MetricInfo(f.Name, f.Counter ? "counter" : "gauge", f.Unit, f.Help)).ToList();

    private static readonly Dictionary<string, Gauge> Gauges = GenericMetricFamilies.All.Where(f => !f.Counter)
        .ToDictionary(f => f.Name, f => Metrics.CreateGauge(f.Name, f.Help,
            f == GenericMetricFamilies.Value ? ValueLabels : f == GenericMetricFamilies.Online ? OnlineLabels : Labels));
    private static readonly Counter Energy = Metrics.CreateCounter(GenericMetricFamilies.Energy.Name, GenericMetricFamilies.Energy.Help, Labels);

    private static readonly object Sync = new();
    /// <summary>The series written last time: key (family, entity key or device id) → labels.</summary>
    private static readonly Dictionary<(string Family, string Key), string[]> Known = new();
    private static readonly Dictionary<string, double> LastCounter = new(StringComparer.Ordinal);
    private static IReadOnlyDictionary<string, EntityPrefs> _prefs = new Dictionary<string, EntityPrefs>();
    private static DateTimeOffset _prefsRead = DateTimeOffset.MinValue;
    private static bool _warned;

    public static void Register(MqttState mqtt, GenericRecorder recorder, IMqttSettingsService settings, IServiceScopeFactory scopes,
        Microsoft.Extensions.Logging.ILogger? log = null) =>
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() =>
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (now - _prefsRead > TimeSpan.FromMinutes(1))
                {
                    using var scope = scopes.CreateScope();
                    _prefs = scope.ServiceProvider.GetRequiredService<IEntityPrefsRepository>().ListAllAsync(CancellationToken.None).GetAwaiter().GetResult();
                    _prefsRead = now;
                }
                var limit = settings.GetAsync(CancellationToken.None).GetAwaiter().GetResult().MetricsSeriesLimit;
                var over = Update(mqtt, recorder, _prefs, limit, now);
                if (over > 0 && !_warned) log?.LogWarning("Generic metrics: {Over} series over the limit of {Limit} were skipped", over, limit);
                _warned = over > 0;
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Generic metrics could not be refreshed");
            }
        });

    /// <summary>Writes the current values; returns how many series were skipped over <paramref name="limit"/>.</summary>
    public static int Update(MqttState mqtt, GenericRecorder recorder, IReadOnlyDictionary<string, EntityPrefs> prefs, int limit, DateTimeOffset now)
    {
        lock (Sync)
        {
            var seen = new HashSet<(string, string)>();
            var routed = recorder.ClimateRoutedKeys();
            var series = 0;
            var skipped = 0;
            foreach (var d in recorder.Devices.Where(d => !d.IsArchived))
            {
                var snap = mqtt.Registry.Get(d.ExternalId, now);
                var live = snap?.Entities.ToDictionary(e => e.Spec.UniqueId, StringComparer.Ordinal);
                var deviceLabels = new[] { d.Name, d.Room, d.Integration };
                Put((GenericMetricFamilies.Online.Name, "dev:" + d.Id), deviceLabels, snap?.Available == true ? 1 : 0);
                foreach (var row in recorder.EntitiesOf(d.Id).Where(e => !e.IsRemoved))
                {
                    var e = row.Description;
                    var p = prefs.GetValueOrDefault(e.Key);
                    if (routed.Contains(e.Key) || !EntityDefaults.EffectiveInMetrics(e, p) || GenericMetricFamilies.For(e) is not { } family) continue;
                    if (series >= limit) { skipped++; continue; }
                    series++;
                    var name = EntityDefaults.Name(e, p);
                    var labels = family == GenericMetricFamilies.Value
                        ? new[] { d.Name, name, d.Room, d.Integration, e.Unit ?? "" }
                        : new[] { d.Name, name, d.Room, d.Integration };
                    var value = live is not null && live.TryGetValue(row.ExternalId, out var le) && le.Available ? le.Number : null;
                    if (family.Counter)
                    {
                        var key = (family.Name, e.Key);
                        seen.Add(key);
                        if (Known.TryGetValue(key, out var prev) && !prev.SequenceEqual(labels)) { Energy.RemoveLabelled(prev); LastCounter.Remove(e.Key); }
                        Known[key] = labels;
                        // The device's cumulative value into a counter: seeded once, then the increases (a reset is skipped).
                        if (value is { } v) KasaMetrics.AddCumulative(Energy, labels, LastCounter, e.Key, v);
                    }
                    else
                    {
                        Put((family.Name, e.Key), labels, value);
                    }
                }
            }
            foreach (var gone in Known.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                Remove(gone.Family, Known[gone]);
                Known.Remove(gone);
                LastCounter.Remove(gone.Key);
            }
            mqtt.SetMetricsWarning(skipped > 0 ? $"/metrics: {skipped} serii peste limita de {limit} nu sunt exportate (Setări → MQTT)." : null);
            return skipped;

            void Put((string Family, string Key) key, string[] labels, double? value)
            {
                seen.Add(key);
                if (Known.TryGetValue(key, out var prev) && !prev.SequenceEqual(labels)) Remove(key.Family, prev);
                Known[key] = labels;
                if (value is { } v) Gauges[key.Family].WithLabels(labels).Set(v);
                else Gauges[key.Family].RemoveLabelled(labels);
            }
        }
    }

    private static void Remove(string family, string[] labels)
    {
        if (family == GenericMetricFamilies.Energy.Name) Energy.RemoveLabelled(labels);
        else if (Gauges.TryGetValue(family, out var g)) g.RemoveLabelled(labels);
    }
}
