using FluentAssertions;
using KasaMonitor.Core.Protocol.Tapo;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Tapo;

public class TapoDiscoveryProtocolTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static byte[] WithHeader(string json) =>
        TapoDiscoveryProtocol.BuildProbe(Encoding.UTF8.GetBytes(json), 0, 0); // any 16-byte header + JSON body

    [Fact]
    public void PlainProbe_MatchesPythonKasaConstant()
    {
        // python-kasa (< 0.7) DISCOVERY_QUERY_2 = unhexlify("020000010000000000000000463cb5d3")
        Convert.ToHexString(TapoDiscoveryProtocol.BuildPlainProbe()).ToLowerInvariant()
            .Should().Be("020000010000000000000000463cb5d3");
    }

    [Fact]
    public void Crc32_MatchesZlib()
    {
        Crc32.Compute("123456789"u8).Should().Be(0xCBF43926u);
    }

    [Fact]
    public void RsaProbe_HasValidHeaderAndCrc()
    {
        var probe = TapoDiscoveryProtocol.BuildRsaProbe("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----\n");

        probe[0].Should().Be(2);
        BinaryPrimitives.ReadUInt16BigEndian(probe.AsSpan(2)).Should().Be(1);
        BinaryPrimitives.ReadUInt16BigEndian(probe.AsSpan(4)).Should().Be((ushort)(probe.Length - 16));
        probe[6].Should().Be(17);

        var crc = BinaryPrimitives.ReadUInt32BigEndian(probe.AsSpan(12));
        var copy = probe.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(copy.AsSpan(12), 0x5A6B7C8D);
        Crc32.Compute(copy).Should().Be(crc);

        using var doc = JsonDocument.Parse(probe.AsMemory(16));
        doc.RootElement.GetProperty("params").GetProperty("rsa_key").GetString().Should().StartWith("-----BEGIN PUBLIC KEY-----");
    }

    [Fact]
    public void ParseResponse_P115Klap()
    {
        var r = TapoDiscoveryProtocol.ParseResponse(WithHeader(LoadFixture("tapo_discovery_p115_klap.json")), IPAddress.Loopback);

        r.Should().NotBeNull();
        r!.Mac.Should().Be("b01921c4c028");
        r.IpAddress.Should().Be(IPAddress.Parse("192.168.254.146"));
        r.DeviceType.Should().Be("SMART.TAPOPLUG");
        r.DeviceModel.Should().Be("P115(EU)");
        r.EncryptType.Should().Be("KLAP");
        r.HttpPort.Should().Be(80);
        r.LoginVersion.Should().Be(2);
        r.IsSupportHttps.Should().BeFalse();
        TapoSupport.Classify(r).Route.Should().Be(TapoSupport.Route.Klap);
    }

    [Fact]
    public void ParseResponse_Tpap_IsUnsupportedWithProtocolName()
    {
        var json = LoadFixture("tapo_discovery_p115_klap.json").Replace("\"KLAP\"", "\"TPAP\"");
        var r = TapoDiscoveryProtocol.ParseResponse(WithHeader(json), IPAddress.Loopback)!;

        var (route, reason) = TapoSupport.Classify(r);
        route.Should().Be(TapoSupport.Route.Unsupported);
        reason.Should().Be("protocol TPAP");
    }

    [Theory]
    [InlineData("IOT.SMARTPLUGSWITCH", "XOR", TapoSupport.Route.Legacy)]
    [InlineData("IOT.SMARTPLUGSWITCH", "KLAP", TapoSupport.Route.Unsupported)]
    [InlineData("SMART.TAPOPLUG", "AES", TapoSupport.Route.Aes)]
    [InlineData("SMART.TAPOPLUG", "TPAP", TapoSupport.Route.Unsupported)]
    [InlineData("SMART.TAPOPLUG", "KLAP", TapoSupport.Route.Klap)]
    public void Classify_Routes(string type, string encrypt, TapoSupport.Route expected)
    {
        var r = new TapoDiscoveryResponse(IPAddress.Loopback, "aa", type, "M", encrypt, 80, 2, false);
        TapoSupport.Classify(r).Route.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"error_code\":-1,\"result\":{\"mac\":\"aa\"}}")]
    [InlineData("{\"error_code\":0,\"result\":{}}")]
    public void ParseResponse_Garbage_ReturnsNull(string json)
    {
        TapoDiscoveryProtocol.ParseResponse(WithHeader(json), IPAddress.Loopback).Should().BeNull();
    }

    [Fact]
    public async Task TapoClient_DiscoverAsync_SendsBothProbes_AndParsesAnswer()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var probes = new List<byte[]>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var req = await server.ReceiveAsync(cts.Token);
                probes.Add(req.Buffer);
                await server.SendAsync(WithHeader(LoadFixture("tapo_discovery_p115_klap.json")), req.RemoteEndPoint, cts.Token);
            }
        }, cts.Token);

        var client = new TapoClient(Array.Empty<ISmartTransport>(), discoveryPortOverride: port);
        var found = new List<TapoDiscoveryResponse>();
        await foreach (var r in client.DiscoverAsync(new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(1500), CancellationToken.None))
            found.Add(r);
        await serverTask;

        probes.Should().HaveCount(2);
        probes[0].Length.Should().Be(16);
        probes[1].Length.Should().BeGreaterThan(16);
        found.Should().ContainSingle().Which.Mac.Should().Be("b01921c4c028"); // deduplicated by MAC
    }
}
