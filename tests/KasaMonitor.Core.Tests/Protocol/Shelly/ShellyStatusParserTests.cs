using FluentAssertions;
using KasaMonitor.Core.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class ShellyStatusParserTests
{
    [Fact]
    public void EmGen3_TwoEm1Channels_WithEnergyRelayAndTemperatureOnChannel0()
    {
        var r = ShellyStatusParser.Parse(ShellyFixtures.EmG3Status);

        r.Kind.Should().Be(ShellyMeterKind.Em1);
        r.HasDeviceHistory.Should().BeTrue();
        r.Channels.Select(c => c.Channel).Should().Equal(0, 1);

        var ch0 = r.For(0)!;
        ch0.PowerMw.Should().Be(556_200);
        ch0.VoltageMv.Should().Be(231_200);
        ch0.CurrentMa.Should().Be(3_155);
        ch0.TotalWh.Should().Be(43);              // 43.43 Wh, floored
        ch0.ReturnedWh.Should().Be(68.60);
        ch0.PowerFactor.Should().Be(0.81);
        ch0.ApparentPowerVa.Should().Be(731.4);
        ch0.FrequencyHz.Should().Be(50.0);
        ch0.RelayOn.Should().BeTrue();            // switch:0.output
        ch0.TemperatureC.Should().Be(47.0);       // switch:0.temperature.tC

        var ch1 = r.For(1)!;
        ch1.PowerMw.Should().Be(0);
        ch1.CurrentMa.Should().Be(20);
        ch1.TotalWh.Should().Be(0);
        ch1.RelayOn.Should().BeNull();            // the relay belongs to the device: only on channel 0
        ch1.TemperatureC.Should().BeNull();
    }

    [Fact]
    public void Em1_NegativePower_KeepsSign()
    {
        var status = ShellyFixtures.Parse("""{"em1:0":{"id":0,"voltage":230.0,"current":2.0,"act_power":-417.5,"aprt_power":460.0,"pf":-0.91,"freq":50.0},"em1data:0":{"id":0,"total_act_energy":0,"total_act_ret_energy":120.4}}""");

        var r = ShellyStatusParser.Parse(status).For(0)!;

        r.PowerMw.Should().Be(-417_500);
        r.ReturnedWh.Should().Be(120.4);
        r.RelayOn.Should().BeNull();               // no switch component
    }

    [Fact]
    public void PlusPlugS_MeteredSwitch_IsOneChannel()
    {
        var r = ShellyStatusParser.Parse(ShellyFixtures.Json("shelly_plusplugs_status.json"));

        r.Kind.Should().Be(ShellyMeterKind.Switch);
        r.HasDeviceHistory.Should().BeFalse();
        var ch = r.Channels.Should().ContainSingle().Subject;
        ch.Channel.Should().Be(0);
        ch.Reading.PowerMw.Should().Be(85_300);
        ch.Reading.VoltageMv.Should().Be(229_900);
        ch.Reading.CurrentMa.Should().Be(412);
        ch.Reading.TotalWh.Should().Be(12_345);
        ch.Reading.PowerFactor.Should().Be(0.9);
        ch.Reading.RelayOn.Should().BeTrue();
        ch.Reading.TemperatureC.Should().Be(36.4);
        ch.Reading.ApparentPowerVa.Should().BeNull();
    }

    [Fact]
    public void PmMini_Pm1_IsOneChannelWithoutRelay()
    {
        var r = ShellyStatusParser.Parse(ShellyFixtures.Json("shelly_pmmini_status.json"));

        r.Kind.Should().Be(ShellyMeterKind.Pm1);
        var reading = r.For(0)!;
        reading.PowerMw.Should().Be(240_500);
        reading.TotalWh.Should().Be(5_000);
        reading.ReturnedWh.Should().Be(1.6);
        reading.RelayOn.Should().BeNull();
    }

    [Fact]
    public void Pro3Em_ThreePhaseTotals_AreOneChannel()
    {
        var r = ShellyStatusParser.Parse(ShellyFixtures.Json("shelly_pro3em_status.json"));

        r.Kind.Should().Be(ShellyMeterKind.Em);
        var reading = r.Channels.Should().ContainSingle().Subject.Reading;
        reading.PowerMw.Should().Be(1_450_500);
        reading.CurrentMa.Should().Be(7_120);
        reading.VoltageMv.Should().BeNull();
        reading.ApparentPowerVa.Should().Be(1630.0);
        reading.TotalWh.Should().Be(175_000);
        reading.ReturnedWh.Should().Be(12.5);
    }

    [Fact]
    public void RelayWithoutMetering_HasNoChannels()
    {
        var status = ShellyFixtures.Parse("""{"switch:0":{"id":0,"output":false,"temperature":{"tC":40.1}},"sys":{"uptime":5}}""");

        var r = ShellyStatusParser.Parse(status);

        r.Kind.Should().Be(ShellyMeterKind.None);
        r.Channels.Should().BeEmpty();
    }

    [Fact]
    public void RssiAndUptime_FromWifiAndSys()
    {
        ShellyStatusParser.Rssi(ShellyFixtures.EmG3Status).Should().Be(-58);
        ShellyStatusParser.Uptime(ShellyFixtures.EmG3Status).Should().Be(TimeSpan.FromSeconds(522));
    }
    [Fact]
    public void MissingEnergyCounter_IsNull_NotZero()
    {
        var status = ShellyFixtures.Parse("""{"em1:0":{"id":0,"voltage":230.0,"current":1.0,"act_power":200.0}}""");

        ShellyStatusParser.Parse(status).For(0)!.TotalWh.Should().BeNull();
    }
}
