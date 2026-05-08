using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Dtos;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol;

public sealed class KasaClient : IKasaClient
{
    private const int DefaultPort = 9999;
    private readonly TimeSpan _timeout;
    private readonly int _port;

    public KasaClient() : this(TimeSpan.FromSeconds(2.5), DefaultPort) { }

    /// <param name="portOverride">Override the standard 9999 port. Test-only — production code uses <see cref="DefaultPort"/>.</param>
    public KasaClient(TimeSpan timeout, int portOverride = DefaultPort)
    {
        _timeout = timeout;
        _port = portOverride;
    }

    public async Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct)
    {
        _ = await TcpQueryAsync<SysInfoDto>(ip, "{\"system\":{\"get_sysinfo\":{}}}", "system", "get_sysinfo", ct);
        // TODO(Task 2.2): map dto to PlugInfo. Sentinel keeps Chunk-2 Task 2.1 compilable.
        return new PlugInfo(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, 0, TimeSpan.Zero);
    }

    public async Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct)
    {
        _ = await TcpQueryAsync<EmeterRealtimeDto>(ip, "{\"emeter\":{\"get_realtime\":{}}}", "emeter", "get_realtime", ct);
        return new EmeterReading(0, 0, 0, 0);
    }

    public async Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct)
    {
        _ = await TcpQueryAsync<DayStatDto>(
            ip,
            $"{{\"emeter\":{{\"get_daystat\":{{\"month\":{month},\"year\":{year}}}}}}}",
            "emeter", "get_daystat", ct);
        return Array.Empty<DailyKwh>();
    }

    public async Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct)
    {
        _ = await TcpQueryAsync<MonthStatDto>(
            ip,
            $"{{\"emeter\":{{\"get_monthstat\":{{\"year\":{year}}}}}}}",
            "emeter", "get_monthstat", ct);
        return Array.Empty<MonthlyKwh>();
    }

    public async IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(
        IReadOnlyList<IPAddress> broadcastAddresses,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Full implementation lands in Chunk 2 (Task 2.x: KasaClient.Udp.cs partial).
        await Task.CompletedTask;
        yield break;
    }

    private async Task<T> TcpQueryAsync<T>(
        IPAddress ip,
        string requestJson,
        string outerKey,
        string innerKey,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, _port, cts.Token);
            await using var stream = tcp.GetStream();

            var encoded = KasaWireProtocol.EncodeTcp(requestJson);
            await stream.WriteAsync(encoded, cts.Token);

            var hdr = new byte[4];
            await stream.ReadExactlyAsync(hdr, cts.Token);
            int len = BinaryPrimitives.ReadInt32BigEndian(hdr);
            if (len < 0 || len > 1 << 20)
                throw new InvalidDataException($"Bad frame length {len}");

            var body = new byte[len];
            await stream.ReadExactlyAsync(body, cts.Token);

            var responseJson = Encoding.UTF8.GetString(KasaCodec.Decrypt(body));
            using var doc = JsonDocument.Parse(responseJson);
            var inner = doc.RootElement.GetProperty(outerKey).GetProperty(innerKey);
            return inner.Deserialize<T>()
                ?? throw new InvalidDataException($"Null inner payload for {outerKey}.{innerKey}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Kasa query to {ip}:{_port} timed out after {_timeout.TotalMilliseconds:0} ms.");
        }
    }
}
