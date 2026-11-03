using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// Prometheus exporter. Every series carries alias, mac, model (e.g. KP115, P115, S3EM-002CXCEU), protocol
/// (legacy|klap|aes|shelly-rpc) and role (plug|main|sub, <see cref="PlugRoles.Label"/>): totals use
/// <c>role="plug"</c>, the house is <c>role="main"</c>; nothing is left out of /metrics. The mac label is the MAC for channel 0 and "&lt;mac&gt;-&lt;channel&gt;" for the other
/// channels of a multi-channel meter. Series of plugs that are withdrawn (archived) or unsupported are removed, and a
/// plug whose labels change (rename) drops its old series instead of leaving it frozen. Power factor, apparent power,
/// frequency, returned energy and device temperature exist only for devices that report them (Shelly).
/// </summary>
public sealed class KasaMetrics : IHostedService, IDisposable
{
    public static readonly string[] LabelNames = PlugMetricLabels.Names;

    /// <summary>One exported metric family: single owner of names, types, units and help texts (also shown on /integration).</summary>
    public sealed record MetricInfo(string Name, string Type, string Unit, string Help);

    public static readonly MetricInfo PowerLoadInfo = new("kasa_power_load", "gauge", "W", "Instantaneous active power (watts).");
    public static readonly MetricInfo VoltageInfo = new("kasa_voltage", "gauge", "V", "Voltage (volts).");
    public static readonly MetricInfo CurrentInfo = new("kasa_current", "gauge", "A", "Current (amps).");
    public static readonly MetricInfo RelayStateInfo = new("kasa_relay_state", "gauge", "0/1", "Relay state (1=on, 0=off).");
    public static readonly MetricInfo OnlineInfo = new("kasa_online", "gauge", "0/1", "Reachability (1=online, 0=offline).");
    public static readonly MetricInfo RssiInfo = new("kasa_rssi", "gauge", "dBm", "Wi-Fi signal strength (dBm).");
    public static readonly MetricInfo PowerTotalInfo = new("kasa_power_total", "counter", "Wh", "Cumulative energy (watt-hours).");
    public static readonly MetricInfo PowerFactorInfo = new("kasa_power_factor", "gauge", "ratio", "Power factor (Shelly meters).");
    public static readonly MetricInfo ApparentPowerInfo = new("kasa_apparent_power_va", "gauge", "VA", "Apparent power (volt-amperes).");
    public static readonly MetricInfo FrequencyInfo = new("kasa_frequency_hz", "gauge", "Hz", "Grid frequency (hertz).");
    public static readonly MetricInfo EnergyReturnedInfo = new("kasa_energy_returned_wh_total", "counter", "Wh", "Cumulative returned (exported) energy (watt-hours).");
    public static readonly MetricInfo TemperatureInfo = new("kasa_device_temperature_c", "gauge", "°C", "Device internal temperature (degrees Celsius).");

    public static readonly IReadOnlyList<MetricInfo> Catalog =
        new[] { PowerLoadInfo, VoltageInfo, CurrentInfo, RelayStateInfo, OnlineInfo, RssiInfo, PowerTotalInfo,
                PowerFactorInfo, ApparentPowerInfo, FrequencyInfo, EnergyReturnedInfo, TemperatureInfo };

    private static readonly Gauge PowerLoad = Metrics.CreateGauge(PowerLoadInfo.Name, PowerLoadInfo.Help, LabelNames);
    private static readonly Gauge Voltage = Metrics.CreateGauge(VoltageInfo.Name, VoltageInfo.Help, LabelNames);
    private static readonly Gauge Current = Metrics.CreateGauge(CurrentInfo.Name, CurrentInfo.Help, LabelNames);
    private static readonly Gauge RelayState = Metrics.CreateGauge(RelayStateInfo.Name, RelayStateInfo.Help, LabelNames);
    private static readonly Gauge OnlineState = Metrics.CreateGauge(OnlineInfo.Name, OnlineInfo.Help, LabelNames);
    private static readonly Gauge Rssi = Metrics.CreateGauge(RssiInfo.Name, RssiInfo.Help, LabelNames);
    private static readonly Counter PowerTotal = Metrics.CreateCounter(PowerTotalInfo.Name, PowerTotalInfo.Help, LabelNames);
    private static readonly Gauge PowerFactor = Metrics.CreateGauge(PowerFactorInfo.Name, PowerFactorInfo.Help, LabelNames);
    private static readonly Gauge ApparentPower = Metrics.CreateGauge(ApparentPowerInfo.Name, ApparentPowerInfo.Help, LabelNames);
    private static readonly Gauge Frequency = Metrics.CreateGauge(FrequencyInfo.Name, FrequencyInfo.Help, LabelNames);
    private static readonly Counter EnergyReturned = Metrics.CreateCounter(EnergyReturnedInfo.Name, EnergyReturnedInfo.Help, LabelNames);
    private static readonly Gauge Temperature = Metrics.CreateGauge(TemperatureInfo.Name, TemperatureInfo.Help, LabelNames);

    /// <summary>
    /// The power of each single-appliance plug („un singur aparat pe priză”) under its appliance's name. A family of its own
    /// (labels <see cref="AppliancePlugLabels"/>), so the <c>kasa_*</c> series above keep their labels and their history;
    /// not in <see cref="Catalog"/> (which lists the per-plug families with <see cref="LabelNames"/>).
    /// </summary>
    public static readonly MetricInfo AppliancePlugPowerInfo = new("kasa_appliance_plug_power_w", "gauge", "W",
        "Active power of the plugs that measure a single appliance, by appliance name (watts).");

    public static readonly string[] AppliancePlugLabels = { "appliance", "alias" };

    private static readonly Gauge AppliancePlugPower = Metrics.CreateGauge(AppliancePlugPowerInfo.Name, AppliancePlugPowerInfo.Help, AppliancePlugLabels);

    private static readonly Collector[] AllFamilies =
        { PowerLoad, Voltage, Current, RelayState, OnlineState, Rssi, PowerTotal, PowerFactor, ApparentPower, Frequency, EnergyReturned, Temperature };

    private readonly IPlugStateBroadcaster _broadcaster;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, double> _lastTotalWh = new();
    private readonly Dictionary<Guid, double> _lastReturnedWh = new();
    private readonly Dictionary<Guid, string[]> _labels = new();
    private readonly Dictionary<Guid, string[]> _applianceLabels = new();

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

    public static string[] LabelValues(PlugLiveState s) => PlugMetricLabels.For(s);

    /// <summary>
    /// The labels of <see cref="AppliancePlugPowerInfo"/>: the appliance name normalised like <c>kasa_appliance_*</c>
    /// (<see cref="NilmMetrics.Label"/>) and the alias of <see cref="LabelValues"/>; null for a plug without a single appliance.
    /// </summary>
    public static string[]? ApplianceLabelValues(PlugLiveState s) =>
        string.IsNullOrEmpty(s.ApplianceName) ? null : new[] { NilmMetrics.Label(s.ApplianceName), LabelValues(s)[0] };

    private void Update(PlugLiveState s)
    {
        lock (_sync)
        {
            var labels = LabelValues(s);
            if (_labels.TryGetValue(s.PlugId, out var previous) && !previous.SequenceEqual(labels))
            {
                RemoveSeries(previous);
                _lastTotalWh.Remove(s.PlugId);
                _lastReturnedWh.Remove(s.PlugId);
            }
            _labels[s.PlugId] = labels;
            var appliance = ApplianceLabelValues(s);
            if (_applianceLabels.TryGetValue(s.PlugId, out var previousAppliance) && (appliance is null || !previousAppliance.SequenceEqual(appliance)))
            {
                AppliancePlugPower.RemoveLabelled(previousAppliance);
                _applianceLabels.Remove(s.PlugId);
            }

            OnlineState.WithLabels(labels).Set(s.IsOnline ? 1 : 0);

            if (s.LastReading is null) return;

            PowerLoad.WithLabels(labels).Set(s.LastReading.PowerMw / 1000.0);
            if (appliance is not null)
            {
                AppliancePlugPower.WithLabels(appliance).Set(s.LastReading.PowerMw / 1000.0);
                _applianceLabels[s.PlugId] = appliance;
            }
            if (s.LastReading.VoltageMv is { } mv) Voltage.WithLabels(labels).Set(mv / 1000.0);
            if (s.LastReading.CurrentMa is { } ma) Current.WithLabels(labels).Set(ma / 1000.0);
            if (s.LastReading.RelayOn is { } relayOn)
                RelayState.WithLabels(labels).Set(relayOn ? 1 : 0);
            else if (s.Protocol != PlugProtocols.ShellyRpc)
                // Kasa/Tapo: PlugLiveState doesn't carry the relay; if online and a reading exists, treat as on.
                RelayState.WithLabels(labels).Set(s.IsOnline ? 1 : 0);
            // Shelly without a relay on this channel: no kasa_relay_state series.

            if (s.LastReading.PowerFactor is { } pf) PowerFactor.WithLabels(labels).Set(pf);
            if (s.LastReading.ApparentPowerVa is { } va) ApparentPower.WithLabels(labels).Set(va);
            if (s.LastReading.FrequencyHz is { } hz) Frequency.WithLabels(labels).Set(hz);
            if (s.LastReading.TemperatureC is { } tc) Temperature.WithLabels(labels).Set(tc);

            // Convert cumulative TotalWh into a monotonically increasing counter.
            // Counter.Inc(delta) requires delta >= 0. Reset (device reboot, or month rollover for Tapo month_energy)
            // is handled by skipping the delta if negative.
            // No total in this answer (Shelly component missing): keep the counter as it is.
            if (s.LastReading.TotalWh is { } totalWh)
                AddCumulative(PowerTotal, labels, _lastTotalWh, s.PlugId, totalWh);
            if (s.LastReading.ReturnedWh is { } returned)
                AddCumulative(EnergyReturned, labels, _lastReturnedWh, s.PlugId, returned);
        }
    }

    /// <summary>
    /// Feeds a device's cumulative Wh into a Prometheus counter: seeded at the device value on first sighting, then
    /// increased by the positive differences (a decrease = device counter reset, skipped).
    /// </summary>
    internal static void AddCumulative<TKey>(Counter counter, string[] labels, Dictionary<TKey, double> last, TKey plugId, double value) where TKey : notnull
    {
        if (last.TryGetValue(plugId, out var prev))
        {
            var delta = value - prev;
            if (delta > 0) counter.WithLabels(labels).Inc(delta);
        }
        else
        {
            counter.WithLabels(labels).Inc(value);
        }
        last[plugId] = value;
    }

    private void Remove(Guid plugId)
    {
        lock (_sync)
        {
            if (_labels.Remove(plugId, out var labels)) RemoveSeries(labels);
            if (_applianceLabels.Remove(plugId, out var appliance)) AppliancePlugPower.RemoveLabelled(appliance);
            _lastTotalWh.Remove(plugId);
            _lastReturnedWh.Remove(plugId);
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
