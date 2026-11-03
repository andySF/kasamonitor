using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class ShellySupportTests
{
    private static readonly IPAddress Ip = IPAddress.Parse("192.168.254.141");

    private static ShellyDeviceInfo EmInfo => ShellyDeviceInfo.Parse(ShellyFixtures.EmG3DeviceInfo);

    [Fact]
    public void DeviceInfo_ParsesRealAnswer()
    {
        var info = EmInfo;

        info.Id.Should().Be("shellyemg3-d885ac0b743c");
        info.Mac.Should().Be("d885ac0b743c");      // normalized like every other plug
        info.Model.Should().Be("S3EM-002CXCEU");
        info.Gen.Should().Be(3);
        info.Version.Should().Be("1.6.99-emg3prod2");
        info.App.Should().Be("EMG3");
        info.AuthEnabled.Should().BeFalse();
    }

    [Fact]
    public void DeviceInfo_Gen1_IsRejected()
    {
        var act = () => ShellyDeviceInfo.Parse(ShellyFixtures.Parse("""{"mac":"AABBCCDDEEFF","gen":1}"""));
        act.Should().Throw<KasaMonitor.Core.Protocol.KasaProtocolException>();
    }

    [Fact]
    public void EmGen3_MapsToOnePlugPerChannel_WithDefaultAliases()
    {
        var plugs = ShellySupport.ToPlugs(Ip, EmInfo, ShellyFixtures.EmG3Status, ShellyFixtures.EmG3Config);

        plugs.Select(p => p.Channel).Should().Equal(0, 1);
        plugs.Should().OnlyContain(p => p.Protocol == PlugProtocols.ShellyRpc && p.IsSupported && p.Info.Mac == "d885ac0b743c");
        plugs.Select(p => p.Info.Alias).Should().Equal("shellyemg3-d885ac0b743c ch0", "shellyemg3-d885ac0b743c ch1");
        plugs[0].Info.Model.Should().Be("S3EM-002CXCEU");
        plugs[0].Info.HwVer.Should().Be("EMG3");
        plugs[0].Info.SwVer.Should().Be("1.6.99-emg3prod2");
        plugs[0].Info.OemId.Should().Be("gen3");
        plugs[0].Info.RelayOn.Should().BeTrue();
        plugs[0].Info.RssiDbm.Should().Be(-58);
    }

    [Fact]
    public void ChannelName_AndDeviceName_WinOverTheId()
    {
        var config = ShellyFixtures.Parse("""{"em1:0":{"id":0,"name":"Tablou general"},"em1:1":{"id":1,"name":null},"sys":{"device":{"name":"Contor casa"}}}""");

        var plugs = ShellySupport.ToPlugs(Ip, EmInfo, ShellyFixtures.EmG3Status, config);

        plugs.Select(p => p.Info.Alias).Should().Equal("Tablou general", "Contor casa ch1");
    }

    [Fact]
    public void SingleChannelDevice_HasNoChannelSuffix()
    {
        var info = EmInfo with { Id = "shellyplusplugs-80646f000001", Model = "SNPL-00112EU", App = "PlugS", Mac = "80646f000001" };

        var plug = ShellySupport.ToPlugs(Ip, info, ShellyFixtures.Json("shelly_plusplugs_status.json"), null).Should().ContainSingle().Subject;

        plug.Info.Alias.Should().Be("shellyplusplugs-80646f000001");
        plug.Channel.Should().Be(0);
    }

    [Fact]
    public void DeviceWithoutMeter_IsOneUnsupportedEntry()
    {
        var status = ShellyFixtures.Parse("""{"switch:0":{"id":0,"output":true}}""");

        var plug = ShellySupport.ToPlugs(Ip, EmInfo, status, null).Should().ContainSingle().Subject;

        plug.IsSupported.Should().BeFalse();
        plug.UnsupportedReason.Should().Be(ShellySupport.NoMeterReason);
        plug.Protocol.Should().Be(PlugProtocols.ShellyRpc);
    }

    [Fact]
    public void ClimateSensorWithoutMeter_IsNoPlugAtAll()
    {
        // R4 (0.10.0): a Shelly H&T Gen3 (temperature:0, humidity:0, no meter) is read by the climate worker; the plug
        // discovery skips it instead of listing (and, with auto-add, storing) an unsupported plug.
        var status = KasaMonitor.Core.Tests.Climate.ClimateFixtures.Json("shelly_htg3_status.json");
        var info = ShellyDeviceInfo.Parse(KasaMonitor.Core.Tests.Climate.ClimateFixtures.Json("shelly_htg3_deviceinfo.json"));

        ShellySupport.IsClimateSensor(status).Should().BeTrue();
        ShellySupport.ToPlugs(Ip, info, status, null).Should().BeEmpty();
        ShellySupport.IsClimateSensor(ShellyFixtures.Parse("""{"switch:0":{"id":0,"output":true}}""")).Should().BeFalse("no temperature: still listed as unsupported");
    }

    [Theory]
    [InlineData("S3EM-002CXCEU", "EMG3", "Shelly EM Gen3 (EMG3)")]
    [InlineData("XYZ-1", "NewThing", "Shelly NewThing (NewThing)")]
    [InlineData("XYZ-1", "", "XYZ-1")]
    public void FriendlyModel(string model, string app, string expected) =>
        ShellySupport.FriendlyModel(model, app).Should().Be(expected);

    [Fact]
    public void ProbeHosts_IpsRangesAndCidr()
    {
        var (hosts, invalid) = ShellySupport.ParseProbeHosts("192.168.1.141, 192.168.1.150-152; 10.0.0.0/30 192.168.1.141 nope 1.2.3.4-1.2.3.1");

        hosts.Select(h => h.ToString()).Should().Equal(
            "192.168.1.141", "192.168.1.150", "192.168.1.151", "192.168.1.152", "10.0.0.1", "10.0.0.2");
        invalid.Should().Equal("nope", "1.2.3.4-1.2.3.1");
    }

    [Fact]
    public void ProbeHosts_TooLargeBlock_IsRejected()
    {
        var (hosts, invalid) = ShellySupport.ParseProbeHosts("10.0.0.0/8, 10.1.0.0/21");

        invalid.Should().Equal("10.0.0.0/8", "10.1.0.0/21");   // /8 outside /16../32, /21 = 2046 hosts > 1024
        hosts.Should().BeEmpty();
    }
    [Fact]
    public void ProbeHosts_Cidr22_IsTheLargestBlock()
    {
        var (hosts, invalid) = ShellySupport.ParseProbeHosts("10.1.0.0/22");

        invalid.Should().BeEmpty();
        hosts.Should().HaveCount(1022);
    }
}
