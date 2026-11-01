using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

/// <summary>
/// Minimal WebSocket server on loopback that plays a Shelly device: it does the HTTP upgrade by hand, then hands every
/// received JSON frame to <see cref="Handler"/>, which may answer (in any order) through <see cref="SendAsync"/>.
/// </summary>
internal sealed class FakeShellyWsServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private WebSocket? _ws;
    private readonly SemaphoreSlim _send = new(1, 1);

    public List<JsonElement> Received { get; } = new();

    /// <summary>Called for each request frame; default: no answer.</summary>
    public Func<JsonElement, Task> Handler { get; set; } = _ => Task.CompletedTask;

    public Uri Uri => new($"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/rpc");

    public TaskCompletionSource Connected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeShellyWsServer()
    {
        _listener.Start();
        _accept = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
            var stream = client.GetStream();
            var request = await ReadHeadersAsync(stream);
            var key = request.Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _cts.Token);
            _ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
            Connected.TrySetResult();

            var buffer = new byte[64 * 1024];
            while (!_cts.IsCancellationRequested && _ws.State == WebSocketState.Open)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                using var doc = JsonDocument.Parse(ms.ToArray());
                var frame = doc.RootElement.Clone();
                lock (Received) Received.Add(frame);
                _ = Task.Run(() => Handler(frame));
            }
        }
        catch (Exception) { /* test ended */ }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (!sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one) == 0) break;
            sb.Append((char)one[0]);
        }
        return sb.ToString();
    }

    public async Task SendAsync(string json)
    {
        await _send.WaitAsync();
        try { await _ws!.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _send.Release(); }
    }

    public async Task CloseAsync()
    {
        if (_ws is { State: WebSocketState.Open })
            await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reboot", CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _accept.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        _ws?.Dispose();
    }
}
