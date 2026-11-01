using FluentAssertions;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using System.Net;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class ShellyDetailsAndMeterTests
{
    private static readonly DateTimeOffset ReadAt = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    [Fact]
    public void Details_EmGen3_RealAnswers()
    {
        var info = ShellyDeviceInfo.Parse(ShellyFixtures.EmG3DeviceInfo);

        var d = ShellyDetailsParser.Parse(info, ShellyFixtures.EmG3Status, ShellyFixtures.EmG3Config, 0, Array.Empty<string>(), ReadAt);

        d.Get("Relay").Should().Be("on");
        d.Get("Uptime").Should().Be("8 min 42 s");
        d.Get("Default state after power loss").Should().Be("off");
        d.Get("Auto-on").Should().Be("disabled");
        d.Get("Auto-off").Should().Be("disabled");
        d.Get("Temperature").Should().Be("47.0 °C");
        d.Get("CT type").Should().Be("50AEMG3");
        d.Get("Reversed CT").Should().Be("no");
        d.Get("Power factor").Should().Be("0.81");
        d.Get("SSID").Should().Be("TestNet");
        d.Get("Wi-Fi signal").Should().Be("-58 dBm");
        d.Get("Cloud").Should().Be("connected");
        d.Get("MQTT").Should().Be("not connected");
        d.Get("Model").Should().Be("Shelly EM Gen3 (EMG3), S3EM-002CXCEU");
        d.Get("Firmware").Should().Be("1.6.99-emg3prod2");
        d.Get("Update available").Should().Be("2.1.1");
        d.Get("Device time zone").Should().Be("Europe/Bucharest");
        d.Get("Device time").Should().StartWith("2026-11-01 08:50:56 +02:00");
        d.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Details_MissingAnswers_OnlyDropTheirItems()
    {
        var d = ShellyDetailsParser.Parse(null, ShellyFixtures.EmG3Status, null, 1, new[] { "Shelly.GetConfig: timeout" }, ReadAt);

        d.Get("CT type").Should().BeNull();
        d.Get("Power factor").Should().Be("0.00");   // em1:1
        d.Get("Model").Should().BeNull();
        d.Errors.Should().ContainSingle();
    }

    [Theory]
    [InlineData(PlugRole.Plug, true, "plug")]
    [InlineData(PlugRole.MainMeter, false, "main")]
    [InlineData(PlugRole.SubMeter, false, "sub")]
    public void Roles_DecideTotals_AndTheMetricLabel(PlugRole role, bool counts, string label)
    {
        PlugRoles.CountsInTotals(role).Should().Be(counts);
        PlugRoles.IsMeter(role).Should().Be(!counts);
        PlugRoles.Label(role).Should().Be(label);
    }

    [Theory]
    [InlineData(0, PlugRole.Plug)]
    [InlineData(1, PlugRole.MainMeter)]
    [InlineData(2, PlugRole.SubMeter)]
    [InlineData(7, PlugRole.Plug)]   // unknown stored value (newer version) reads as a plug
    public void Roles_FromStoredInt(int value, PlugRole role) => PlugRoles.FromInt(value).Should().Be(role);

    [Fact]
    public void MacLabel_PerChannel()
    {
        PlugChannel.MacLabel("d885ac0b743c", 0).Should().Be("d885ac0b743c");
        PlugChannel.MacLabel("d885ac0b743c", 1).Should().Be("d885ac0b743c-1");
    }

    [Fact]
    public void AllPlugsHistory_LeavesMetersOut_AndShowsTheHouseSeparately()
    {
        Plug Make(string mac, string alias, string model, int channel = 0, PlugRole role = PlugRole.Plug) => new(Guid.NewGuid(), mac, IPAddress.Loopback, alias, false, model,
            "", "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, null, PlugProtocols.Legacy, null, channel, role);
        var plug = Make("aa", "Birou", "KP115(EU)");
        var shellyPlug = Make("bb", "Frigider", "SNPL-00112EU") with { Protocol = PlugProtocols.ShellyRpc };   // a Shelly plug counts
        var meter0 = Make("d885ac0b743c", "Tablou ch0", "S3EM-002CXCEU", 0, PlugRole.MainMeter) with { Protocol = PlugProtocols.ShellyRpc };
        var meter1 = Make("d885ac0b743c", "Tablou ch1", "S3EM-002CXCEU", 1, PlugRole.SubMeter) with { Protocol = PlugProtocols.ShellyRpc };
        var rows = new[]
        {
            new PlugDailyKwh(plug.Id, new DailyKwh(2026, 11, 1, 500)),
            new PlugDailyKwh(shellyPlug.Id, new DailyKwh(2026, 11, 1, 200)),
            new PlugDailyKwh(meter0.Id, new DailyKwh(2026, 11, 1, 9000)),
            new PlugDailyKwh(meter0.Id, new DailyKwh(2026, 11, 2, 8000)),   // a day with only the house
            new PlugDailyKwh(meter1.Id, new DailyKwh(2026, 11, 1, 100)),
        };

        var all = AllPlugsDailyEnergy.Build(new[] { plug, shellyPlug, meter0, meter1 }, rows);

        all.Series.Select(s => s.Alias).Should().Equal("Birou", "Frigider");
        all.Days.Should().Equal(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 2));
        all.DayTotalsWh.Should().Equal(700, 0);
        all.TotalWh.Should().Be(700);
        all.House.Should().NotBeNull();
        all.House!.Alias.Should().Be("Casă");
        all.House.DailyWh.Should().Equal(9000, 8000);
        all.House.TotalWh.Should().Be(17000);
    }

    [Fact]
    public void AllPlugsHistory_WithoutMainMeter_HasNoHouse()
    {
        var meter = new Plug(Guid.NewGuid(), "cc", IPAddress.Loopback, "EM", false, "S3EM-002CXCEU", "", "", "",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, false, null, PlugProtocols.ShellyRpc, null, 0, PlugRole.SubMeter);
        var all = AllPlugsDailyEnergy.Build(new[] { meter }, new[] { new PlugDailyKwh(meter.Id, new DailyKwh(2026, 11, 1, 100)) });
        all.House.Should().BeNull();
        all.Series.Should().BeEmpty();
    }

    [Fact]
    public void Details_MeteredSwitches_UseTheChannelsOwnRelay()
    {
        var status = ShellyFixtures.Parse("""{"switch:0":{"id":0,"output":true,"apower":10,"temperature":{"tC":40.0}},"switch:1":{"id":1,"output":false,"apower":0,"temperature":{"tC":41.5}}}""");
        var config = ShellyFixtures.Parse("""{"switch:0":{"id":0,"initial_state":"on"},"switch:1":{"id":1,"initial_state":"restore_last"}}""");

        var d = ShellyDetailsParser.Parse(null, status, config, 1, Array.Empty<string>(), ReadAt);

        d.Get("Relay").Should().Be("off");
        d.Get("Temperature").Should().Be("41.5 °C");
        d.Get("Default state after power loss").Should().Be("last state");
    }
}
