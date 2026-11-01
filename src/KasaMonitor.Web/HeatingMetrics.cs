using KasaMonitor.Core.Heating;
using KasaMonitor.Workers;
using Prometheus;

namespace KasaMonitor.Web;

/// <summary>
/// The <c>heating_*</c> family on /metrics (labels <c>device</c>, <c>model</c>; consumption also <c>purpose</c> =
/// heating|dhw and <c>period</c>), refreshed from <see cref="HeatingState"/> right before every scrape. A value the
/// boiler does not expose has no series (e.g. return/outside temperature and pressure on a Vitodens 050-W). The
/// <c>_total</c> counters are built by KasaMonitor (Σ stored days + today) and only grow, for <c>increase()</c>.
/// </summary>
public static class HeatingMetrics
{
    public static readonly string[] Labels = { "device", "model" };
    private static readonly string[] PurposeLabels = { "device", "model", "purpose" };
    private static readonly string[] PeriodLabels = { "device", "model", "purpose", "period" };

    private static KasaMetrics.MetricInfo G(string name, string unit, string help) => new(name, "gauge", unit, help);
    private static KasaMetrics.MetricInfo C(string name, string unit, string help) => new(name, "counter", unit, help);

    public static readonly KasaMetrics.MetricInfo BurnerActiveInfo = G("heating_burner_active", "0/1", "Burner on (1) or off (0).");
    public static readonly KasaMetrics.MetricInfo ModulationInfo = G("heating_burner_modulation_percent", "%", "Burner modulation.");
    public static readonly KasaMetrics.MetricInfo BoilerTempInfo = G("heating_boiler_temperature_c", "°C", "Boiler water temperature (only boilers with this sensor).");
    public static readonly KasaMetrics.MetricInfo BoilerTargetInfo = G("heating_boiler_target_c", "°C", "Boiler target temperature (heating.boiler.temperature; 20 °C when idle on a Vitodens 050-W).");
    public static readonly KasaMetrics.MetricInfo SupplyTempInfo = G("heating_supply_temperature_c", "°C", "Supply (flow) temperature.");
    public static readonly KasaMetrics.MetricInfo ReturnTempInfo = G("heating_return_temperature_c", "°C", "Return temperature (only boilers with this sensor).");
    public static readonly KasaMetrics.MetricInfo OutsideTempInfo = G("heating_outside_temperature_c", "°C", "Outside temperature (only with an outside sensor).");
    public static readonly KasaMetrics.MetricInfo DhwTempInfo = G("heating_dhw_temperature_c", "°C", "Domestic hot water temperature.");
    public static readonly KasaMetrics.MetricInfo DhwTargetInfo = G("heating_dhw_target_c", "°C", "Domestic hot water target temperature.");
    public static readonly KasaMetrics.MetricInfo RoomTempInfo = G("heating_room_temperature_c", "°C", "Room temperature (room sensor of circuit 0).");
    public static readonly KasaMetrics.MetricInfo PumpInfo = G("heating_pump_percent", "%", "Internal pump target.");
    public static readonly KasaMetrics.MetricInfo PressureInfo = G("heating_pressure_bar", "bar", "Supply pressure (only when the sensor is connected).");
    public static readonly KasaMetrics.MetricInfo StartsInfo = C("heating_burner_starts_total", "starts", "Burner starts (boiler counter).");
    public static readonly KasaMetrics.MetricInfo HoursInfo = C("heating_burner_hours_total", "h", "Burner operating hours (boiler counter).");
    public static readonly KasaMetrics.MetricInfo GasInfo = G("heating_gas_consumption_m3", "m³", "Gas consumption reported by the boiler per purpose (heating|dhw) and period (day|last7days|month|lastmonth|year|lastyear); estimated by the boiler, not a gas meter.");
    public static readonly KasaMetrics.MetricInfo GasTotalInfo = C("heating_gas_consumption_m3_total", "m³", "Cumulative gas per purpose built by KasaMonitor (sum of stored days + today).");
    public static readonly KasaMetrics.MetricInfo HeatInfo = G("heating_heat_production_kwh", "kWh", "Heat produced per purpose and period (boiler values).");
    public static readonly KasaMetrics.MetricInfo HeatTotalInfo = C("heating_heat_production_kwh_total", "kWh", "Cumulative heat produced per purpose built by KasaMonitor.");
    public static readonly KasaMetrics.MetricInfo PowerInfo = G("heating_power_consumption_kwh", "kWh", "Electricity used by the boiler per purpose and period (boiler values).");
    public static readonly KasaMetrics.MetricInfo PowerTotalInfo = C("heating_power_consumption_kwh_total", "kWh", "Cumulative boiler electricity per purpose built by KasaMonitor.");
    public static readonly KasaMetrics.MetricInfo DhwModeInfo = G("heating_dhw_mode_info", "1", "Active hot water mode (label mode).");
    public static readonly KasaMetrics.MetricInfo ProgramInfo = G("heating_program_info", "1", "Active heating program of circuit 0 (label program).");
    public static readonly KasaMetrics.MetricInfo WifiInfo = G("heating_gateway_wifi_dbm", "dBm", "Wi-Fi signal of the boiler's gateway.");
    public static readonly KasaMetrics.MetricInfo BatteryInfo = G("heating_room_sensor_battery_percent", "%", "Battery of the Zigbee room sensor.");
    public static readonly KasaMetrics.MetricInfo ErrorInfo = G("heating_error_active", "0/1", "An error message (priority other than status) or the malfunction lock is active.");
    public static readonly KasaMetrics.MetricInfo CallsInfo = G("heating_api_calls_today", "calls", "HTTP calls to Viessmann (API and token endpoint) actually made today, local calendar day; resets at local midnight and at a restart. The 1450 limit is a sliding 24 h window.");
    public static readonly KasaMetrics.MetricInfo LastSuccessInfo = G("heating_api_last_success_timestamp_seconds", "s", "Unix time of the last successful boiler read (0 = none).");
    public static readonly KasaMetrics.MetricInfo OnlineInfo = G("heating_online", "0/1", "Last read succeeded within three poll intervals.");

    public static readonly IReadOnlyList<KasaMetrics.MetricInfo> Catalog = new[]
    {
        BurnerActiveInfo, ModulationInfo, BoilerTempInfo, BoilerTargetInfo, SupplyTempInfo, ReturnTempInfo, OutsideTempInfo, DhwTempInfo, DhwTargetInfo,
        RoomTempInfo, PumpInfo, PressureInfo, StartsInfo, HoursInfo, GasInfo, GasTotalInfo, HeatInfo, HeatTotalInfo, PowerInfo,
        PowerTotalInfo, DhwModeInfo, ProgramInfo, WifiInfo, BatteryInfo, ErrorInfo, CallsInfo, LastSuccessInfo, OnlineInfo,
    };

    private static Gauge Gauge(KasaMetrics.MetricInfo i, string[]? labels = null) => Metrics.CreateGauge(i.Name, i.Help, labels ?? Labels);
    private static Counter Counter(KasaMetrics.MetricInfo i, string[]? labels = null) => Metrics.CreateCounter(i.Name, i.Help, labels ?? Labels);

    private static readonly Gauge BurnerActive = Gauge(BurnerActiveInfo);
    private static readonly Gauge Modulation = Gauge(ModulationInfo);
    private static readonly Gauge BoilerTemp = Gauge(BoilerTempInfo);
    private static readonly Gauge BoilerTarget = Gauge(BoilerTargetInfo);
    private static readonly Gauge SupplyTemp = Gauge(SupplyTempInfo);
    private static readonly Gauge ReturnTemp = Gauge(ReturnTempInfo);
    private static readonly Gauge OutsideTemp = Gauge(OutsideTempInfo);
    private static readonly Gauge DhwTemp = Gauge(DhwTempInfo);
    private static readonly Gauge DhwTarget = Gauge(DhwTargetInfo);
    private static readonly Gauge RoomTemp = Gauge(RoomTempInfo);
    private static readonly Gauge Pump = Gauge(PumpInfo);
    private static readonly Gauge Pressure = Gauge(PressureInfo);
    private static readonly Counter Starts = Counter(StartsInfo);
    private static readonly Counter Hours = Counter(HoursInfo);
    private static readonly Gauge Gas = Gauge(GasInfo, PeriodLabels);
    private static readonly Counter GasTotal = Counter(GasTotalInfo, PurposeLabels);
    private static readonly Gauge Heat = Gauge(HeatInfo, PeriodLabels);
    private static readonly Counter HeatTotal = Counter(HeatTotalInfo, PurposeLabels);
    private static readonly Gauge Power = Gauge(PowerInfo, PeriodLabels);
    private static readonly Counter PowerTotal = Counter(PowerTotalInfo, PurposeLabels);
    private static readonly Gauge DhwMode = Gauge(DhwModeInfo, new[] { "device", "model", "mode" });
    private static readonly Gauge Program = Gauge(ProgramInfo, new[] { "device", "model", "program" });
    private static readonly Gauge Wifi = Gauge(WifiInfo);
    private static readonly Gauge Battery = Gauge(BatteryInfo);
    private static readonly Gauge Error = Gauge(ErrorInfo);
    private static readonly Gauge Calls = Gauge(CallsInfo);
    private static readonly Gauge LastSuccess = Gauge(LastSuccessInfo);
    private static readonly Gauge Online = Gauge(OnlineInfo);

    private static readonly Collector[] All =
    {
        BurnerActive, Modulation, BoilerTemp, BoilerTarget, SupplyTemp, ReturnTemp, OutsideTemp, DhwTemp, DhwTarget, RoomTemp, Pump, Pressure,
        Starts, Hours, Gas, GasTotal, Heat, HeatTotal, Power, PowerTotal, DhwMode, Program, Wifi, Battery, Error, Calls, LastSuccess, Online,
    };

    private static readonly object Sync = new();
    private static string[]? _lastLabels;

    public static void Register(HeatingState state) =>
        Metrics.DefaultRegistry.AddBeforeCollectCallback(() => Update(state, DateTimeOffset.UtcNow));

    /// <summary>Writes the current state into the families (no series at all while no device is known).</summary>
    public static void Update(HeatingState state, DateTimeOffset now)
    {
        lock (Sync)
        {
            var live = state.Current;
            if (live is null)
            {
                if (_lastLabels is { } gone) RemoveDevice(gone[0]);
                _lastLabels = null;
                return;
            }
            var labels = new[] { live.Device.Name, live.Device.ModelId };
            if (_lastLabels is { } previous && !previous.SequenceEqual(labels)) RemoveDevice(previous[0]);
            _lastLabels = labels;

            Online.WithLabels(labels).Set(state.IsOnline(now) ? 1 : 0);
            Calls.WithLabels(labels).Set(state.Calls.Today);
            LastSuccess.WithLabels(labels).Set(live.LastSuccessUtc?.ToUnixTimeSeconds() ?? 0);

            var s = live.Snapshot;
            if (s is null) return;
            Set(BurnerActive, labels, s.BurnerActive is { } on ? (on ? 1 : 0) : null);
            Set(Modulation, labels, s.ModulationPct);
            Set(BoilerTemp, labels, s.BoilerTempC);
            Set(BoilerTarget, labels, s.BoilerTargetC);
            Set(SupplyTemp, labels, s.SupplyTempC);
            Set(ReturnTemp, labels, s.ReturnTempC);
            Set(OutsideTemp, labels, s.OutsideTempC);
            Set(DhwTemp, labels, s.DhwTempC);
            Set(DhwTarget, labels, s.DhwTargetC);
            Set(RoomTemp, labels, s.RoomTempC);
            Set(Pump, labels, s.PumpPct);
            Set(Pressure, labels, s.PressureBar);
            Set(Wifi, labels, s.WifiDbm);
            Set(Battery, labels, s.RoomSensorBatteryPct);
            Error.WithLabels(labels).Set(s.ActiveError is null ? 0 : 1);
            if (s.BurnerStarts is { } starts) Starts.WithLabels(labels).IncTo(starts);
            if (s.BurnerHours is { } hours) Hours.WithLabels(labels).IncTo(hours);

            Info(DhwMode, labels, s.DhwMode);
            Info(Program, labels, s.ActiveProgram);

            foreach (var p in live.Periods ?? Array.Empty<HeatingEnergyPeriod>())
            {
                var target = p.Kind switch { HeatingKinds.Gas => Gas, HeatingKinds.Heat => Heat, HeatingKinds.Power => Power, _ => null };
                target?.WithLabels(labels[0], labels[1], p.Purpose, p.Period).Set(p.Value);
            }
            if (live.Totals is { } t)
            {
                Total(GasTotal, labels, HeatingKinds.Heating, t.GasHeatingM3, s.GasHeating);
                Total(GasTotal, labels, HeatingKinds.Dhw, t.GasDhwM3, s.GasDhw);
                Total(HeatTotal, labels, HeatingKinds.Heating, t.HeatHeatingKwh, s.HeatHeating);
                Total(HeatTotal, labels, HeatingKinds.Dhw, t.HeatDhwKwh, s.HeatDhw);
                Total(PowerTotal, labels, HeatingKinds.Heating, t.PowerHeatingKwh, s.PowerHeating);
                Total(PowerTotal, labels, HeatingKinds.Dhw, t.PowerDhwKwh, s.PowerDhw);
            }
        }
    }

    private static void Set(Gauge g, string[] labels, double? value)
    {
        if (value is { } v) g.WithLabels(labels).Set(v);
        else g.RemoveLabelled(labels);
    }

    private static void Total(Counter c, string[] labels, string purpose, double value, EnergySummary? feature)
    {
        // Only for purposes the boiler reports at all (no series for a feature it lacks).
        if (feature is null) return;
        c.WithLabels(labels[0], labels[1], purpose).IncTo(value);
    }

    /// <summary>An info gauge (=1) whose label value follows the current mode; the series of the previous mode is removed.</summary>
    private static void Info(Gauge g, string[] labels, string? value)
    {
        foreach (var lv in g.GetAllLabelValues().ToList())
            if (lv[0] == labels[0] && lv[2] != value) g.RemoveLabelled(lv);
        if (value is not null) g.WithLabels(labels[0], labels[1], value).Set(1);
    }

    private static void RemoveDevice(string device)
    {
        foreach (var family in All)
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
