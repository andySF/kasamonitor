using KasaMonitor.Core.Miele;
using KasaMonitor.Workers;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// The <c>miele_*</c> family on /metrics (labels <c>device</c> = alias, <c>type</c> = washing_machine|tumble_dryer|…),
/// refreshed from <see cref="MieleState"/> right before every scrape. A value the appliance does not report has no
/// series; the info gauges keep one series per appliance (the current status / phase). <c>miele_connected</c> has no
/// labels (the SSE stream is shared by all appliances). The serial (deviceId) is never a label.
/// </summary>
public static class MieleMetrics
{
    public static readonly string[] Labels = { "device", "type" };

    private static KasaMetrics.MetricInfo G(string name, string unit, string help) => new(name, "gauge", unit, help);

    public static readonly KasaMetrics.MetricInfo OnlineInfo = G("miele_online", "0/1", "The appliance is connected to Miele (status not 255) and the stream or polling delivered recently.");
    public static readonly KasaMetrics.MetricInfo StatusInfo = G("miele_status_info", "1", "Current status (label status: off, on, programmed, waiting_to_start, running, pause, end_programmed, failure, programme_interrupted, idle, …).");
    public static readonly KasaMetrics.MetricInfo PhaseInfo = G("miele_program_phase_info", "1", "Current program phase (label phase, Romanian text; community list, see README).");
    public static readonly KasaMetrics.MetricInfo RemainingInfo = G("miele_remaining_seconds", "s", "Remaining program time.");
    public static readonly KasaMetrics.MetricInfo ElapsedInfo = G("miele_elapsed_seconds", "s", "Elapsed program time.");
    public static readonly KasaMetrics.MetricInfo TargetTempInfo = G("miele_target_temperature_c", "°C", "Program target temperature.");
    public static readonly KasaMetrics.MetricInfo SpinInfo = G("miele_spin_rpm", "rpm", "Selected spin speed.");
    public static readonly KasaMetrics.MetricInfo EcoEnergyInfo = G("miele_eco_energy_kwh", "kWh", "Energy used by the running program so far (ecoFeedback; resets when the program ends).");
    public static readonly KasaMetrics.MetricInfo EcoWaterInfo = G("miele_eco_water_l", "l", "Water used by the running program so far (ecoFeedback; resets when the program ends).");
    public static readonly KasaMetrics.MetricInfo EnergyForecastInfo = G("miele_eco_energy_forecast_ratio", "0..1", "Energy forecast of the selected program, relative 0..1 (ecoFeedback.energyForecast; not kWh).");
    public static readonly KasaMetrics.MetricInfo WaterForecastInfo = G("miele_eco_water_forecast_ratio", "0..1", "Water forecast of the selected program, relative 0..1 (ecoFeedback.waterForecast; not litres).");
    public static readonly KasaMetrics.MetricInfo CyclesInfo = new("miele_cycles_total", "counter", "cycles", "Program cycles completed (stored in MieleCycles).");
    public static readonly KasaMetrics.MetricInfo LastEnergyInfo = G("miele_last_cycle_energy_kwh", "kWh", "Energy of the last completed cycle reported by the appliance (ecoFeedback).");
    public static readonly KasaMetrics.MetricInfo LastWaterInfo = G("miele_last_cycle_water_l", "l", "Water of the last completed cycle reported by the appliance (ecoFeedback).");
    public static readonly KasaMetrics.MetricInfo DoorInfo = G("miele_door_open", "0/1", "Door open (signalDoor).");
    public static readonly KasaMetrics.MetricInfo ConnectedInfo = G("miele_connected", "0/1", "The SSE stream to Miele is open (no labels).");

    public static readonly IReadOnlyList<KasaMetrics.MetricInfo> Catalog = new[]
    {
        OnlineInfo, StatusInfo, PhaseInfo, RemainingInfo, ElapsedInfo, TargetTempInfo, SpinInfo, EcoEnergyInfo, EcoWaterInfo,
        EnergyForecastInfo, WaterForecastInfo, CyclesInfo, LastEnergyInfo, LastWaterInfo, DoorInfo, ConnectedInfo,
    };

    private static Gauge Gauge(KasaMetrics.MetricInfo i, string[]? labels = null) => Metrics.CreateGauge(i.Name, i.Help, labels ?? Labels);

    private static readonly Gauge Online = Gauge(OnlineInfo);
    private static readonly Gauge Status = Gauge(StatusInfo, new[] { "device", "type", "status" });
    private static readonly Gauge Phase = Gauge(PhaseInfo, new[] { "device", "type", "phase" });
    private static readonly Gauge Remaining = Gauge(RemainingInfo);
    private static readonly Gauge Elapsed = Gauge(ElapsedInfo);
    private static readonly Gauge TargetTemp = Gauge(TargetTempInfo);
    private static readonly Gauge Spin = Gauge(SpinInfo);
    private static readonly Gauge EcoEnergy = Gauge(EcoEnergyInfo);
    private static readonly Gauge EcoWater = Gauge(EcoWaterInfo);
    private static readonly Gauge EnergyForecast = Gauge(EnergyForecastInfo);
    private static readonly Gauge WaterForecast = Gauge(WaterForecastInfo);
    private static readonly Counter Cycles = Metrics.CreateCounter(CyclesInfo.Name, CyclesInfo.Help, Labels);
    private static readonly Gauge LastEnergy = Gauge(LastEnergyInfo);
    private static readonly Gauge LastWater = Gauge(LastWaterInfo);
    private static readonly Gauge Door = Gauge(DoorInfo);
    private static readonly Gauge Connected = Metrics.CreateGauge(ConnectedInfo.Name, ConnectedInfo.Help);

    private static readonly Collector[] PerDevice =
    {
        Online, Status, Phase, Remaining, Elapsed, TargetTemp, Spin, EcoEnergy, EcoWater, EnergyForecast, WaterForecast, Cycles,
        LastEnergy, LastWater, Door,
    };

    private static readonly object Sync = new();
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.Ordinal);

    public static void Register(MieleState state) =>
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => Update(state, DateTimeOffset.UtcNow));

    /// <summary>Writes the current state into the families; series of appliances gone (or renamed) are removed.</summary>
    public static void Update(MieleState state, DateTimeOffset now)
    {
        lock (Sync)
        {
            Connected.Set(state.StreamConnected ? 1 : 0);
            var appliances = state.Appliances;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in appliances)
            {
                var labels = new[] { a.Device.Alias, MieleTexts.TypeCode(a.Device.TypeRaw) };
                seen.Add(a.Device.Id);
                if (Known.TryGetValue(a.Device.Id, out var previous) && !previous.SequenceEqual(labels)) RemoveDevice(previous[0]);
                Known[a.Device.Id] = labels;

                Online.WithLabels(labels).Set(state.IsOnline(a, now) ? 1 : 0);
                Cycles.WithLabels(labels).IncTo(a.CyclesTotal);
                Set(LastEnergy, labels, a.LastCycle?.EnergyKwh);
                Set(LastWater, labels, a.LastCycle?.WaterL);

                var v = a.Values;
                Info(Status, labels, v?.StatusRaw is { } st ? MieleTexts.StatusCode(st) : null);
                Info(Phase, labels, v?.PhaseText);
                Set(Remaining, labels, v?.RemainingSec);
                Set(Elapsed, labels, v?.ElapsedSec);
                Set(TargetTemp, labels, v?.TargetTempC);
                Set(Spin, labels, v?.SpinRpm);
                Set(EcoEnergy, labels, v?.EcoEnergyKwh);
                Set(EcoWater, labels, v?.EcoWaterL);
                Set(EnergyForecast, labels, v?.EcoEnergyForecast);
                Set(WaterForecast, labels, v?.EcoWaterForecast);
                Set(Door, labels, v?.DoorOpen is { } open ? (open ? 1 : 0) : null);
            }
            foreach (var gone in Known.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                RemoveDevice(Known[gone][0]);
                Known.Remove(gone);
            }
        }
    }

    private static void Set(Gauge g, string[] labels, double? value)
    {
        if (value is { } v) g.WithLabels(labels).Set(v);
        else g.RemoveLabelled(labels);
    }

    /// <summary>An info gauge (=1) whose third label follows the current value; the previous value's series is removed.</summary>
    private static void Info(Gauge g, string[] labels, string? value)
    {
        foreach (var lv in g.GetAllLabelValues().ToList())
            if (lv[0] == labels[0] && lv[2] != value) g.RemoveLabelled(lv);
        if (value is not null) g.WithLabels(labels[0], labels[1], value).Set(1);
    }

    private static void RemoveDevice(string device)
    {
        foreach (var family in PerDevice)
        {
            switch (family)
            {
                case Gauge g:
                    foreach (var lv in g.GetAllLabelValues().Where(l => l[0] == device).ToList()) g.RemoveLabelled(lv);
                    break;
                case Counter c:
                    foreach (var lv in c.GetAllLabelValues().Where(l => l[0] == device).ToList()) c.RemoveLabelled(lv);
                    break;
            }
        }
    }
}
