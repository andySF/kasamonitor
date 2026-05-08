using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaClientTests
{
    private static (TcpListener listener, IPEndPoint endpoint) StartLoopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, (IPEndPoint)listener.LocalEndpoint);
    }

    [Fact]
    public async Task GetSysInfoAsync_SendsCorrectRequestAndDecodesResponse()
    {
        var (listener, endpoint) = StartLoopback();
        try
        {
            var sysinfoJson = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "sysinfo_kp115_birou.json"));
            var responseJson = $"{{\"system\":{{\"get_sysinfo\":{sysinfoJson}}}}}";

            var serverTask = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                await using var stream = tcp.GetStream();

                var hdr = new byte[4];
                await stream.ReadExactlyAsync(hdr);
                int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                var body = new byte[len];
                await stream.ReadExactlyAsync(body);

                var requestJson = KasaWireProtocol.DecodeTcp(hdr.Concat(body).ToArray());
                requestJson.Should().Contain("get_sysinfo");

                var resp = KasaWireProtocol.EncodeTcp(responseJson);
                await stream.WriteAsync(resp);
            });

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var clientTask = client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);

            await Task.WhenAll(clientTask, serverTask);
            var result = await clientTask;
            result.Mac.Should().Be("c006c3702bfc");
            result.Alias.Should().Be("Birou");
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetSysInfoAsync_TimeoutRaisesTimeoutException()
    {
        var (listener, endpoint) = StartLoopback();
        try
        {
            // Listener doesn't accept; let the client time out.
            var client = new KasaClient(TimeSpan.FromMilliseconds(150), portOverride: endpoint.Port);
            var act = async () => await client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);
            await act.Should().ThrowAsync<TimeoutException>();
        }
        finally { listener.Stop(); }
    }
}
