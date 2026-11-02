using FluentAssertions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Web;
using KasaMonitor.Workers;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class MieleMetricsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static MieleStateValues Running(string phase) => new(5, "În funcțiune", 0, "Normal", 1, "Cottons", 260, phase, 1800, 1200, null, 40, 1400,
        null, false, false, false, 0.35, 12, 0.4, 0.3, true, true, false);

    private static MieleApplianceLive Live(string id, string alias, MieleStateValues? v, int cycles = 3) =>
        new(new MieleDevice(id, 1, "Mașină de spălat", "WWV980", "", alias, null, false, Now), v, Now, null,
            new MieleCycle(1, id, Now.AddHours(-3), Now.AddHours(-2), Now.AddHours(-2), 1, "Cottons", Array.Empty<MielePhaseSpan>(), 0.9, 45, null, MieleEndReasons.End),
            cycles);

    private static async Task<List<string>> LinesAsync(string device)
    {
        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray()).Split('\n')
            .Where(l => !l.StartsWith('#') && (l.Contains($"device=\"{device}\"") || l.StartsWith("miele_connected"))).ToList();
    }

    [Fact]
    public async Task State_BecomesMieleSeries_InfoFollowsThePhase_SerialNeverALabel()
    {
        var state = new MieleState();
        state.SetStream(true, MieleStreamMode.Sse, Now);
        state.Publish(Live("000148508574", "Masina test A", Running("Spălare principală")));
        MieleMetrics.Update(state, Now);

        const string l = "device=\"Masina test A\",type=\"washing_machine\"";
        var lines = await LinesAsync("Masina test A");
        lines.Should().Contain("miele_connected 1");
        lines.Should().Contain("miele_online{" + l + "} 1");
        lines.Should().Contain("miele_status_info{" + l + ",status=\"running\"} 1");
        lines.Should().Contain("miele_program_phase_info{" + l + ",phase=\"Spălare principală\"} 1");
        lines.Should().Contain("miele_remaining_seconds{" + l + "} 1800");
        lines.Should().Contain("miele_elapsed_seconds{" + l + "} 1200");
        lines.Should().Contain("miele_target_temperature_c{" + l + "} 40");
        lines.Should().Contain("miele_spin_rpm{" + l + "} 1400");
        lines.Should().Contain("miele_eco_energy_kwh{" + l + "} 0.35");
        lines.Should().Contain("miele_eco_water_l{" + l + "} 12");
        lines.Should().Contain("miele_eco_energy_forecast_ratio{" + l + "} 0.4");
        lines.Should().Contain("miele_eco_water_forecast_ratio{" + l + "} 0.3");
        lines.Should().Contain("miele_cycles_total{" + l + "} 3");
        lines.Should().Contain("miele_last_cycle_energy_kwh{" + l + "} 0.9");
        lines.Should().Contain("miele_last_cycle_water_l{" + l + "} 45");
        lines.Should().Contain("miele_door_open{" + l + "} 0");
        lines.Should().NotContain(x => x.Contains("000148508574"));

        state.Publish(Live("000148508574", "Masina test A", Running("Centrifugare") with { SpinRpm = null }, cycles: 4));
        MieleMetrics.Update(state, Now);
        lines = await LinesAsync("Masina test A");
        lines.Count(x => x.StartsWith("miele_program_phase_info{")).Should().Be(1, "one series per appliance");
        lines.Should().Contain("miele_program_phase_info{" + l + ",phase=\"Centrifugare\"} 1");
        lines.Should().NotContain(x => x.StartsWith("miele_spin_rpm{"), "no value, no series");
        lines.Should().Contain("miele_cycles_total{" + l + "} 4");

        // Once the API history has the run, the last-cycle metrics take its values (the ecoFeedback ones stay in the cycle).
        var live = Live("000148508574", "Masina test A", Running("Centrifugare"), cycles: 4);
        state.Publish(live with { LastCycle = live.LastCycle! with { Source = MieleCycleSources.Merged, ApiEnergyKwh = 0.82, ApiWaterL = 44.5 } });
        MieleMetrics.Update(state, Now);
        lines = await LinesAsync("Masina test A");
        lines.Should().Contain("miele_last_cycle_energy_kwh{" + l + "} 0.82");
        lines.Should().Contain("miele_last_cycle_water_l{" + l + "} 44.5");
    }

    [Fact]
    public async Task RenamedAppliance_OldSeriesRemoved()
    {
        var state = new MieleState();
        state.Publish(Live("000111222333", "Masina test B", Running("Clătire")));
        MieleMetrics.Update(state, Now);
        (await LinesAsync("Masina test B")).Should().Contain(x => x.StartsWith("miele_status_info{"));

        state.Publish(Live("000111222333", "Masina test C", Running("Clătire")));
        MieleMetrics.Update(state, Now);
        (await LinesAsync("Masina test B")).Where(x => !x.StartsWith("miele_connected")).Should().BeEmpty();
        (await LinesAsync("Masina test C")).Should().Contain(x => x.StartsWith("miele_online{"));
    }

    [Fact]
    public void Catalog_HasEveryFamilyOfThePackage()
    {
        MieleMetrics.Catalog.Select(c => c.Name).Should().BeEquivalentTo(
            "miele_online", "miele_status_info", "miele_program_phase_info", "miele_remaining_seconds", "miele_elapsed_seconds",
            "miele_target_temperature_c", "miele_spin_rpm", "miele_eco_energy_kwh", "miele_eco_water_l", "miele_eco_energy_forecast_ratio",
            "miele_eco_water_forecast_ratio", "miele_cycles_total", "miele_last_cycle_energy_kwh", "miele_last_cycle_water_l", "miele_door_open",
            "miele_connected");
    }
}
