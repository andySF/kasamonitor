using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaClientDiscoveryTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static (UdpClient server, int port) StartUdpServer()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return (udp, ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
    }

    private static async Task RespondOnce(UdpClient server, string responsePayloadJson, CancellationToken ct)
    {
        var received = await server.ReceiveAsync(ct);
        var responseBytes = KasaWireProtocol.EncodeUdp(responsePayloadJson);
        await server.SendAsync(responseBytes, received.RemoteEndPoint, ct);
    }

    [Fact]
    public async Task DiscoverAsync_ReceivesAndMapsResponse()
    {
        var (server, port) = StartUdpServer();
        try
        {
            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var serverTask = RespondOnce(
                server,
                $"{{\"system\":{{\"get_sysinfo\":{LoadFixture("sysinfo_kp115_birou.json")}}}}}",
                serverCts.Token);

            var client = new KasaClient(TimeSpan.FromMilliseconds(500), portOverride: port);
            var found = new List<KasaMonitor.Core.Models.DiscoveredPlug>();
            await foreach (var p in client.DiscoverAsync(
                new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(800), CancellationToken.None))
            {
                found.Add(p);
            }
            await serverTask;

            found.Should().HaveCount(1);
            found[0].Info.Mac.Should().Be("c006c3702bfc");
            found[0].Info.Alias.Should().Be("Birou");
            found[0].IpAddress.Should().Be(IPAddress.Loopback);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task DiscoverAsync_NoResponse_YieldsNothing_AndCompletes()
    {
        var (server, port) = StartUdpServer(); // server bound but never replies
        try
        {
            var client = new KasaClient(TimeSpan.FromMilliseconds(200), portOverride: port);
            var found = new List<KasaMonitor.Core.Models.DiscoveredPlug>();
            await foreach (var p in client.DiscoverAsync(
                new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(300), CancellationToken.None))
            {
                found.Add(p);
            }
            found.Should().BeEmpty();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task DiscoverAsync_DedupesByMac_WhenSamePlugRespondsTwice()
    {
        var (server, port) = StartUdpServer();
        try
        {
            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var payload = $"{{\"system\":{{\"get_sysinfo\":{LoadFixture("sysinfo_kp115_birou.json")}}}}}";
            // Respond twice to the same caller endpoint.
            var serverTask = Task.Run(async () =>
            {
                var r1 = await server.ReceiveAsync(serverCts.Token);
                await server.SendAsync(KasaWireProtocol.EncodeUdp(payload), r1.RemoteEndPoint, serverCts.Token);
                await server.SendAsync(KasaWireProtocol.EncodeUdp(payload), r1.RemoteEndPoint, serverCts.Token);
            }, serverCts.Token);

            var client = new KasaClient(TimeSpan.FromMilliseconds(500), portOverride: port);
            var found = new List<KasaMonitor.Core.Models.DiscoveredPlug>();
            await foreach (var p in client.DiscoverAsync(
                new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(800), CancellationToken.None))
            {
                found.Add(p);
            }
            await serverTask;

            found.Should().HaveCount(1); // duplicate Mac was deduped
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task DiscoverAsync_MultipleBroadcastAddresses_AllSent()
    {
        // Bind one server on loopback and verify it receives two requests when the client
        // is given two broadcast addresses both pointing at IPAddress.Loopback.
        var (server, port) = StartUdpServer();
        try
        {
            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int requestCount = 0;
            var serverTask = Task.Run(async () =>
            {
                while (!serverCts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await server.ReceiveAsync(serverCts.Token);
                        Interlocked.Increment(ref requestCount);
                    }
                    catch (OperationCanceledException) { return; }
                }
            }, serverCts.Token);

            var client = new KasaClient(TimeSpan.FromMilliseconds(300), portOverride: port);
            var addrs = new[] { IPAddress.Loopback, IPAddress.Loopback };
            await foreach (var _ in client.DiscoverAsync(addrs, TimeSpan.FromMilliseconds(400), CancellationToken.None))
            { /* drain */ }

            // Give the receiver a beat to count any in-flight datagrams, then cancel.
            await Task.Delay(50);
            serverCts.Cancel();
            try { await serverTask; } catch { /* swallow */ }

            requestCount.Should().BeGreaterOrEqualTo(2);
        }
        finally { server.Dispose(); }
    }
}
