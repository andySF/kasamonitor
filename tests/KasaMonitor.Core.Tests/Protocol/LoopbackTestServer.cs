using KasaMonitor.Core.Protocol;
using System.Net;
using System.Net.Sockets;

namespace KasaMonitor.Core.Tests.Protocol;

internal static class LoopbackTestServer
{
    public static (TcpListener listener, IPEndPoint endpoint) Start()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return (l, (IPEndPoint)l.LocalEndpoint);
    }

    public static async Task<string> ReceiveRequestAsync(TcpListener listener, string responseJson)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        await using var stream = tcp.GetStream();

        var hdr = new byte[4];
        await stream.ReadExactlyAsync(hdr);
        int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
        var body = new byte[len];
        await stream.ReadExactlyAsync(body);

        var requestJson = KasaWireProtocol.DecodeTcp(hdr.Concat(body).ToArray());

        var resp = KasaWireProtocol.EncodeTcp(responseJson);
        await stream.WriteAsync(resp);
        return requestJson;
    }
}
