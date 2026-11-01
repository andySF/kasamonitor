using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Web;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class HeatingMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);

    /// <summary>A few features in the shape of the Vitodens 050-W answer (pressure sensor not connected, gas in m³).</summary>
    private static string Json(string dhwMode) => """
        {"data":[
          {"feature":"heating.boiler.sensors.temperature.commonSupply","deviceId":"0","properties":{"value":{"type":"number","value":36.2,"unit":"celsius"},"status":{"type":"string","value":"connected"}}},
          {"feature":"heating.boiler.temperature","deviceId":"0","properties":{"value":{"type":"number","value":20,"unit":"celsius"}}},
          {"feature":"heating.sensors.pressure.supply","deviceId":"0","properties":{"status":{"type":"string","value":"notConnected"}}},
          {"feature":"heating.burners.0","deviceId":"0","properties":{"active":{"type":"boolean","value":false}}},
          {"feature":"heating.burners.0.statistics","deviceId":"0","properties":{"hours":{"type":"number","value":2891,"unit":"hour"},"starts":{"type":"number","value":31089,"unit":""}}},
          {"feature":"heating.dhw.operating.modes.active","deviceId":"0","properties":{"value":{"type":"string","value":"__MODE__"}}},
          {"feature":"heating.gas.consumption.summary.heating","deviceId":"0","properties":{"currentDay":{"type":"number","value":1.3,"unit":"cubicMeter"},"lastMonth":{"type":"number","value":14.6,"unit":"cubicMeter"}}},
          {"feature":"heating.gas.consumption.summary.dhw","deviceId":"0","properties":{"currentDay":{"type":"number","value":0.1,"unit":"cubicMeter"}}}
        ]}
        """.Replace("__MODE__", dhwMode);

    private static HeatingState State(string device, string dhwMode, double totalHeating)
    {
        var state = new HeatingState(new ApiCallBudget(() => Now));
        state.Calls.Record();
        var dev = new HeatingDevice(Guid.NewGuid(), "viessmann", "1", "2", "0", "E3_Test_050", device, true, Now, null);
        var s = HeatingFeatureParser.Parse(Json(dhwMode), Now);
        state.Publish(new HeatingLive(dev, s, new HeatingEnergyTotals(totalHeating, 0.1, 0, 0, 0, 0), Now, null, true,
            HeatingEnergyBuilder.Periods(dev.Id, s, 10.55, EnergyUnit.CubicMeter)));
        return state;
    }

    private static async Task<List<string>> LinesAsync(string device)
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray()).Split('\n')
            .Where(l => !l.StartsWith('#') && l.Contains($"device=\"{device}\"")).ToList();
    }

    [Fact]
    public async Task Snapshot_BecomesHeatingSeries_OnlyForWhatTheBoilerHas()
    {
        var state = State("Centrala test A", "eco", 4.5);
        HeatingMetrics.Update(state, Now);

        var lines = await LinesAsync("Centrala test A");
        const string l = "device=\"Centrala test A\",model=\"E3_Test_050\"";
        lines.Should().Contain("heating_supply_temperature_c{" + l + "} 36.2");
        lines.Should().Contain("heating_burner_active{" + l + "} 0");
        lines.Should().Contain("heating_boiler_target_c{" + l + "} 20");
        lines.Should().Contain("heating_burner_starts_total{" + l + "} 31089");
        lines.Should().Contain("heating_burner_hours_total{" + l + "} 2891");
        lines.Should().Contain("heating_gas_consumption_m3{" + l + ",purpose=\"heating\",period=\"day\"} 1.3");
        lines.Should().Contain("heating_gas_consumption_m3{" + l + ",purpose=\"heating\",period=\"lastmonth\"} 14.6");
        lines.Should().Contain("heating_gas_consumption_m3{" + l + ",purpose=\"dhw\",period=\"day\"} 0.1");
        lines.Should().Contain("heating_gas_consumption_m3_total{" + l + ",purpose=\"heating\"} 4.5");
        lines.Should().Contain("heating_dhw_mode_info{" + l + ",mode=\"eco\"} 1");
        lines.Should().Contain("heating_error_active{" + l + "} 0");
        lines.Should().Contain("heating_online{" + l + "} 1");
        lines.Should().Contain("heating_api_calls_today{" + l + "} 1");
        lines.Should().Contain("heating_api_last_success_timestamp_seconds{" + l + "} " + Now.ToUnixTimeSeconds());
        lines.Should().NotContain(x => x.StartsWith("heating_pressure_bar{"), "the pressure sensor is not connected");
        lines.Should().NotContain(x => x.StartsWith("heating_outside_temperature_c{") || x.StartsWith("heating_return_temperature_c{"));
        lines.Should().NotContain(x => x.StartsWith("heating_heat_production_kwh_total{"), "the boiler reports no heat production here");
    }

    [Fact]
    public async Task ModeChange_MovesTheInfoSeries_TotalNeverGoesDown()
    {
        HeatingMetrics.Update(State("Centrala test B", "eco", 5.0), Now);
        HeatingMetrics.Update(State("Centrala test B", "comfort", 4.0), Now);

        var lines = await LinesAsync("Centrala test B");
        lines.Should().Contain(x => x.StartsWith("heating_dhw_mode_info{") && x.Contains("mode=\"comfort\"") && x.EndsWith(" 1"));
        lines.Should().NotContain(x => x.Contains("mode=\"eco\""));
        lines.Should().Contain(x => x.StartsWith("heating_gas_consumption_m3_total{") && x.Contains("purpose=\"heating\"") && x.EndsWith(" 5"));
    }

    [Fact]
    public async Task NoDevice_NoSeries()
    {
        HeatingMetrics.Update(State("Centrala test C", "eco", 1), Now);
        HeatingMetrics.Update(new HeatingState(), Now);

        (await LinesAsync("Centrala test C")).Should().BeEmpty();
    }

    [Fact]
    public void Catalog_HasEveryFamilyOfThePackage()
    {
        HeatingMetrics.Catalog.Select(c => c.Name).Should().Contain(new[]
        {
            "heating_burner_active", "heating_burner_modulation_percent", "heating_boiler_temperature_c", "heating_boiler_target_c", "heating_supply_temperature_c",
            "heating_return_temperature_c", "heating_outside_temperature_c", "heating_dhw_temperature_c", "heating_dhw_target_c",
            "heating_room_temperature_c", "heating_pump_percent", "heating_pressure_bar", "heating_burner_starts_total", "heating_burner_hours_total",
            "heating_gas_consumption_m3", "heating_gas_consumption_m3_total", "heating_heat_production_kwh", "heating_heat_production_kwh_total",
            "heating_power_consumption_kwh", "heating_power_consumption_kwh_total", "heating_dhw_mode_info", "heating_program_info",
            "heating_gateway_wifi_dbm", "heating_room_sensor_battery_percent", "heating_error_active", "heating_api_calls_today",
            "heating_api_last_success_timestamp_seconds", "heating_online",
        });
    }

    [Fact]
    public void Dashboard_HasTheCentralaRow_UsingOnlyExportedHeatingMetrics()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(KasaMonitor.Web.Integration.DeployFiles.Read(KasaMonitor.Web.Integration.DeployFiles.Dashboard));
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray().ToList();
        panels.Should().Contain(p => p.GetProperty("type").GetString() == "row" && p.GetProperty("title").GetString() == "Centrală");
        panels.Select(p => p.GetProperty("id").GetInt32()).Should().OnlyHaveUniqueItems();

        var exprs = panels.Where(p => p.TryGetProperty("targets", out _))
            .SelectMany(p => p.GetProperty("targets").EnumerateArray().Select(t => t.GetProperty("expr").GetString()!)).ToList();
        var used = exprs.SelectMany(e => System.Text.RegularExpressions.Regex.Matches(e, @"heating_[a-z0-9_]+").Select(m => m.Value)).Distinct().ToList();
        used.Should().Contain(new[] { "heating_burner_active", "heating_burner_modulation_percent", "heating_supply_temperature_c",
            "heating_dhw_temperature_c", "heating_gas_consumption_m3", "heating_gas_consumption_m3_total" });
        used.Should().BeSubsetOf(HeatingMetrics.Catalog.Select(c => c.Name));
    }

    [Fact]
    public void GasTable_AndCost()
    {
        var id = Guid.NewGuid();
        var periods = new[]
        {
            new HeatingEnergyPeriod(id, "gas", "dhw", "day", 0.1, "m3", Now),
            new HeatingEnergyPeriod(id, "gas", "heating", "day", 1.3, "m3", Now),
            new HeatingEnergyPeriod(id, "gas", "heating", "year", 368.8, "m3", Now),
            new HeatingEnergyPeriod(id, "heat", "heating", "day", 14, "kWh", Now),
        };

        var rows = HeatingFormat.GasRows(periods);

        rows.Select(r => r.Period).Should().Equal("day", "year");
        rows[0].TotalM3.Should().BeApproximately(1.4, 1e-9);
        rows[1].DhwM3.Should().BeNull();
        HeatingFormat.GasCost(1.4, 10.55, 0.31m).Should().BeApproximately(4.5787m, 0.0001m);
        HeatingFormat.M3(1.4).Should().Be("1,40 m³");
        HeatingFormat.Temp(36.2).Should().Be("36,2 °C");
        HeatingFormat.Sum(periods, "heat", "day").Should().Be(14);
        HeatingFormat.Sum(periods, "power", "day").Should().BeNull();
    }
}
