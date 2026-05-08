using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Dtos;
using System.Buffers.Binary;
using System.Linq;
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
        var dto = await TcpQueryAsync<SysInfoDto>(ip, "{\"system\":{\"get_sysinfo\":{}}}", "system", "get_sysinfo", ct);
        return MapSysInfo(dto);
    }

    public async Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct)
    {
        var dto = await TcpQueryAsync<EmeterRealtimeDto>(ip, "{\"emeter\":{\"get_realtime\":{}}}", "emeter", "get_realtime", ct);
        if (dto.ErrCode != 0)
            throw new KasaProtocolException($"Device returned err_code {dto.ErrCode} for get_realtime.", dto.ErrCode, "get_realtime");
        return new EmeterReading(dto.PowerMw, dto.VoltageMv, dto.CurrentMa, dto.TotalWh);
    }

    public async Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct)
    {
        var dto = await TcpQueryAsync<DayStatDto>(
            ip,
            $"{{\"emeter\":{{\"get_daystat\":{{\"month\":{month},\"year\":{year}}}}}}}",
            "emeter", "get_daystat", ct);
        if (dto.ErrCode != 0)
            throw new KasaProtocolException($"Device returned err_code {dto.ErrCode} for get_daystat.", dto.ErrCode, "get_daystat");
        return dto.DayList.Select(d => new DailyKwh(d.Year, d.Month, d.Day, d.EnergyWh)).ToList();
    }

    public async Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct)
    {
        var dto = await TcpQueryAsync<MonthStatDto>(
            ip,
            $"{{\"emeter\":{{\"get_monthstat\":{{\"year\":{year}}}}}}}",
            "emeter", "get_monthstat", ct);
        if (dto.ErrCode != 0)
            throw new KasaProtocolException($"Device returned err_code {dto.ErrCode} for get_monthstat.", dto.ErrCode, "get_monthstat");
        return dto.MonthList.Select(m => new MonthlyKwh(m.Year, m.Month, m.EnergyWh)).ToList();
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

    private static PlugInfo MapSysInfo(SysInfoDto dto)
    {
        var alias = dto.Alias ?? throw new KasaProtocolException("sysinfo missing alias", operation: "get_sysinfo");
        var mac   = NormalizeMac(dto.Mac ?? throw new KasaProtocolException("sysinfo missing mac", operation: "get_sysinfo"));
        return new PlugInfo(
            Mac:     mac,
            Alias:   alias,
            Model:   dto.Model ?? string.Empty,
            HwVer:   dto.HwVer ?? string.Empty,
            SwVer:   dto.SwVer ?? string.Empty,
            OemId:   dto.OemId ?? string.Empty,
            RelayOn: dto.RelayState != 0,
            RssiDbm: dto.Rssi,
            OnTime:  TimeSpan.FromSeconds(dto.OnTime));
    }

    private static string NormalizeMac(string raw) =>
        new string(raw.Where(c => c != ':' && c != '-').ToArray()).ToLowerInvariant();
}
