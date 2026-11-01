using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// Prometheus exporter. Every series carries alias, mac, model (e.g. KP115, P115) and protocol (legacy|klap|aes).
/// Series of plugs that are withdrawn (archived) or unsupported are removed, and a plug whose labels change
/// (rename) drops its old series instead of leaving it frozen.
/// </summary>
public sealed class KasaMetrics : IHostedService, IDisposable
{
    private static readonly string[] LabelNames = { "alias", "mac", "model", "protocol" };

    private static readonly Gauge PowerLoad = Metrics.CreateGauge(
        "kasa_power_load", "Instantaneous active power (watts).", LabelNames);

    private static readonly Gauge Voltage = Metrics.CreateGauge(
        "kasa_voltage", "Voltage (volts).", LabelNames);

    private static readonly Gauge Current = Metrics.CreateGauge(
        "kasa_current", "Current (amps).", LabelNames);

    private static readonly Gauge RelayState = Metrics.CreateGauge(
        "kasa_relay_state", "Relay state (1=on, 0=off).", LabelNames);

    private static readonly Gauge OnlineState = Metrics.CreateGauge(
        "kasa_online", "Reachability (1=online, 0=offline).", LabelNames);

    private static readonly Gauge Rssi = Metrics.CreateGauge(
        "kasa_rssi", "Wi-Fi signal strength (dBm).", LabelNames);

    private static readonly Counter PowerTotal = Metrics.CreateCounter(
        "kasa_power_total", "Cumulative energy (watt-hours).", LabelNames);

    private static readonly Collector[] AllFamilies = { PowerLoad, Voltage, Current, RelayState, OnlineState, Rssi, PowerTotal };

    private readonly IPlugStateBroadcaster _broadcaster;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, double> _lastTotalWh = new();
    private readonly Dictionary<Guid, string[]> _labels = new();

    public KasaMetrics(IPlugStateBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Seed from any existing snapshot.
        foreach (var s in _broadcaster.Snapshot().Values) Update(s);
        _broadcaster.StateChanged += Update;
        _broadcaster.StateRemoved += Remove;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public static string[] LabelValues(PlugLiveState s) => new[]
    {
        string.IsNullOrEmpty(s.Alias) ? s.Mac : s.Alias,
        s.Mac,
        PlugModelName.Short(s.Model),
        string.IsNullOrEmpty(s.Protocol) ? PlugProtocols.Legacy : s.Protocol,
    };

    private void Update(PlugLiveState s)
    {
        lock (_sync)
        {
            var labels = LabelValues(s);
            if (_labels.TryGetValue(s.PlugId, out var previous) && !previous.SequenceEqual(labels))
            {
                RemoveSeries(previous);
                _lastTotalWh.Remove(s.PlugId);
            }
            _labels[s.PlugId] = labels;

            OnlineState.WithLabels(labels).Set(s.IsOnline ? 1 : 0);

            if (s.LastReading is null) return;

            PowerLoad.WithLabels(labels).Set(s.LastReading.PowerMw / 1000.0);
            if (s.LastReading.VoltageMv is { } mv) Voltage.WithLabels(labels).Set(mv / 1000.0);
            if (s.LastReading.CurrentMa is { } ma) Current.WithLabels(labels).Set(ma / 1000.0);
            // RelayState: PlugLiveState doesn't carry it directly; if online and reading exists, treat as on.
            RelayState.WithLabels(labels).Set(s.IsOnline ? 1 : 0);

            // Convert cumulative TotalWh into a monotonically increasing counter.
            // Counter.Inc(delta) requires delta >= 0. Reset (device reboot, or month rollover for Tapo month_energy)
            // is handled by skipping the delta if negative.
            var totalWh = (double)s.LastReading.TotalWh;
            if (_lastTotalWh.TryGetValue(s.PlugId, out var prev))
            {
                var delta = totalWh - prev;
                if (delta > 0) PowerTotal.WithLabels(labels).Inc(delta);
            }
            else
            {
                // First sighting — seed the counter at the device's current cumulative value.
                PowerTotal.WithLabels(labels).Inc(totalWh);
            }
            _lastTotalWh[s.PlugId] = totalWh;
        }
    }

    private void Remove(Guid plugId)
    {
        lock (_sync)
        {
            if (_labels.Remove(plugId, out var labels)) RemoveSeries(labels);
            _lastTotalWh.Remove(plugId);
        }
    }

    private static void RemoveSeries(string[] labels)
    {
        foreach (var family in AllFamilies)
        {
            switch (family)
            {
                case Gauge g: g.RemoveLabelled(labels); break;
                case Counter c: c.RemoveLabelled(labels); break;
            }
        }
    }

    private void Unsubscribe()
    {
        _broadcaster.StateChanged -= Update;
        _broadcaster.StateRemoved -= Remove;
    }

    public void Dispose() => Unsubscribe();
}
