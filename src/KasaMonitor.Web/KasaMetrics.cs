using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using Prometheus;

namespace KasaMonitor.Web;

public sealed class KasaMetrics : IHostedService, IDisposable
{
    private static readonly string[] LabelNames = { "alias", "mac" };

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

    private readonly IPlugStateBroadcaster _broadcaster;
    private readonly Dictionary<Guid, double> _lastTotalWh = new();

    public KasaMetrics(IPlugStateBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Seed from any existing snapshot.
        foreach (var s in _broadcaster.Snapshot().Values) Update(s);
        _broadcaster.StateChanged += Update;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _broadcaster.StateChanged -= Update;
        return Task.CompletedTask;
    }

    private void Update(PlugLiveState s)
    {
        var alias = string.IsNullOrEmpty(s.Alias) ? s.Mac : s.Alias;
        var mac = s.Mac;

        OnlineState.WithLabels(alias, mac).Set(s.IsOnline ? 1 : 0);

        if (s.LastReading is null) return;

        var watts = s.LastReading.PowerMw / 1000.0;
        var volts = s.LastReading.VoltageMv / 1000.0;
        var amps = s.LastReading.CurrentMa / 1000.0;

        PowerLoad.WithLabels(alias, mac).Set(watts);
        Voltage.WithLabels(alias, mac).Set(volts);
        Current.WithLabels(alias, mac).Set(amps);
        // RelayState: PlugLiveState doesn't carry it directly; if online and reading exists, treat as on.
        RelayState.WithLabels(alias, mac).Set(s.IsOnline ? 1 : 0);

        // Convert cumulative TotalWh into a monotonically increasing counter.
        // Counter.Inc(delta) requires delta >= 0. Reset (e.g., device reboot) is handled by skipping the delta if negative.
        var totalWh = (double)s.LastReading.TotalWh;
        if (_lastTotalWh.TryGetValue(s.PlugId, out var prev))
        {
            var delta = totalWh - prev;
            if (delta > 0) PowerTotal.WithLabels(alias, mac).Inc(delta);
        }
        else
        {
            // First sighting — seed the counter at the device's current cumulative value.
            PowerTotal.WithLabels(alias, mac).Inc(totalWh);
        }
        _lastTotalWh[s.PlugId] = totalWh;
    }

    public void Dispose() => _broadcaster.StateChanged -= Update;
}
