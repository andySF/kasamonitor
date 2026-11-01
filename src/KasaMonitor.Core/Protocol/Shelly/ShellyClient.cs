using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Shelly;

public sealed class ShellyClient : IShellyClient, IDisposable
{
    /// <summary>Gen2+ devices accept only this user name for digest authentication.</summary>
    public const string User = "admin";

    /// <summary>
    /// Hard cap on EM1Data.GetData pages per call. A page holds about 28 one-minute records (measured on EM Gen3
    /// fw 2.1.1), so one local day (1440–1500 records) needs ~52–54 pages; see <see cref="MaxPagesFor"/>.
    /// </summary>
    public const int MaxDataPagesCap = 200;

    /// <summary>Pages allowed for a window: one per minute of the window plus 10, at most <see cref="MaxDataPagesCap"/>.</summary>
    public static int MaxPagesFor(long fromTs, long toTs) => (int)Math.Min(MaxDataPagesCap, Math.Max(0, toTs - fromTs) / 60 + 10);

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly IPEndPoint _mdnsEndpoint;
    private readonly Func<IReadOnlyList<IPAddress>> _localAddresses;
    private readonly ConcurrentDictionary<string, NonceState> _nonces = new(StringComparer.OrdinalIgnoreCase);

    private sealed class NonceState(DigestAuth.Challenge challenge)
    {
        public DigestAuth.Challenge Challenge { get; } = challenge;
        private int _count;
        public int Next() => Interlocked.Increment(ref _count);
    }

    /// <param name="timeout">Per-request timeout (3 s for polling).</param>
    /// <param name="handler">Test hook; production uses a default <see cref="SocketsHttpHandler"/>.</param>
    /// <param name="mdnsEndpoint">Test hook; production queries 224.0.0.251:5353.</param>
    /// <param name="localAddresses">Test hook; production sends the mDNS question from every IPv4 interface.</param>
    public ShellyClient(TimeSpan? timeout = null, HttpMessageHandler? handler = null, IPEndPoint? mdnsEndpoint = null,
        Func<IReadOnlyList<IPAddress>>? localAddresses = null)
    {
        _localAddresses = localAddresses ?? LocalIPv4Addresses;
        _timeout = timeout ?? TimeSpan.FromSeconds(3);
        _http = handler is null
            ? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan }
            : new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _mdnsEndpoint = mdnsEndpoint ?? new IPEndPoint(ShellyMdns.MulticastAddress, ShellyMdns.Port);
    }

    public async Task<ShellyDeviceInfo> GetDeviceInfoAsync(IPAddress ip, CancellationToken ct, TimeSpan? timeout = null) =>
        ShellyDeviceInfo.Parse(await CallAsync(ip, "Shelly.GetDeviceInfo", "", null, ct, timeout));

    public Task<JsonElement> GetStatusAsync(IPAddress ip, string? password, CancellationToken ct) =>
        CallAsync(ip, "Shelly.GetStatus", "", password, ct);

    public Task<JsonElement> GetConfigAsync(IPAddress ip, string? password, CancellationToken ct) =>
        CallAsync(ip, "Shelly.GetConfig", "", password, ct);

    /// <summary>The single write: GET /rpc/Switch.Set?id=N&amp;on=true|false (digest like every other call).</summary>
    public async Task SetSwitchAsync(IPAddress ip, string? password, int switchId, bool on, CancellationToken ct) =>
        await CallAsync(ip, "Switch.Set", string.Create(CultureInfo.InvariantCulture, $"?id={switchId}&on={(on ? "true" : "false")}"), password, ct);

    public async Task<IReadOnlyList<ShellyDataBlock>> GetEm1RecordsAsync(IPAddress ip, string? password, int id, CancellationToken ct) =>
        ShellyEnergyData.ParseRecords(await CallAsync(ip, "EM1Data.GetRecords", $"?id={id}", password, ct));

    public async Task<IReadOnlyList<ShellyEnergyRow>> GetEm1DataAsync(IPAddress ip, string? password, int id, long fromTs, long toTs, CancellationToken ct)
    {
        var rows = new List<ShellyEnergyRow>();
        var from = fromTs;
        var maxPages = MaxPagesFor(fromTs, toTs);
        for (var page = 0; from <= toTs; page++)
        {
            if (page >= maxPages)
                throw new ShellyIncompleteDataException(
                    $"EM1Data.GetData id={id}: more records after {maxPages} pages (next at {from}); the window {fromTs}..{toTs} is incomplete");
            var query = string.Create(CultureInfo.InvariantCulture, $"?id={id}&ts={from}&end_ts={toTs}");
            var result = ShellyEnergyData.ParseGetData(await CallAsync(ip, "EM1Data.GetData", query, password, ct));
            rows.AddRange(result.Rows.Where(r => r.Ts >= fromTs && r.Ts <= toTs));
            if (result.NextRecordTs is not { } next || next <= from) break;
            from = next;
        }
        return rows;
    }

    /// <summary>
    /// Sends the mDNS question from every IPv4 interface (one socket bound to each local address, with the multicast
    /// interface set to it), because on a multi-homed host (Docker/Hyper-V bridges, VPN) the default multicast route
    /// often points at the wrong network. Responders answer by unicast to the socket. The question is repeated once
    /// after 1 s (mDNS is lossy).
    /// </summary>
    public async IAsyncEnumerable<ShellyMdnsAnswer> DiscoverAsync(TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct)
    {
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var local in _localAddresses())
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(local, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                    sockets.Add(udp);
                }
                catch (SocketException) { /* interface without multicast / address gone: skip it */ }
            }
            if (sockets.Count == 0)
            {
                var any = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                sockets.Add(any);
            }

            var query = ShellyMdns.BuildQuery();
            var answers = System.Threading.Channels.Channel.CreateUnbounded<ShellyMdnsAnswer>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var readers = sockets.Select(udp => Task.Run(() => ListenAsync(udp, query, answers.Writer, deadline.Token))).ToArray();
            _ = Task.WhenAll(readers).ContinueWith(_ => answers.Writer.TryComplete(), TaskScheduler.Default);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var answer in answers.Reader.ReadAllAsync(ct))
                if (seen.Add(answer.Address + "|" + answer.Instance)) yield return answer;
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
    }

    private async Task ListenAsync(UdpClient udp, byte[] query, System.Threading.Channels.ChannelWriter<ShellyMdnsAnswer> answers, CancellationToken deadline)
    {
        try { await udp.SendAsync(query, _mdnsEndpoint, deadline); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return; }   // no route on this interface
        var resendAt = DateTime.UtcNow.AddSeconds(1);
        var resent = false;
        while (!deadline.IsCancellationRequested)
        {
            if (!resent && DateTime.UtcNow >= resendAt)
            {
                resent = true;
                try { await udp.SendAsync(query, _mdnsEndpoint, deadline); } catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
            }
            UdpReceiveResult result;
            using (var slice = CancellationTokenSource.CreateLinkedTokenSource(deadline))
            {
                if (!resent) slice.CancelAfter(TimeSpan.FromMilliseconds(250));
                try { result = await udp.ReceiveAsync(slice.Token); }
                catch (OperationCanceledException) { continue; }
                catch (SocketException)
                {
                    // e.g. ICMP port unreachable reported on the socket (Windows 10054): do not spin until the deadline.
                    try { await Task.Delay(50, deadline); } catch (OperationCanceledException) { }
                    continue;
                }
                catch (ObjectDisposedException) { return; }
            }
            foreach (var answer in ShellyMdns.ParseResponse(result.Buffer, result.RemoteEndPoint.Address))
                if (answer.Gen is null or >= 2) answers.TryWrite(answer);
        }
    }

    /// <summary>IPv4 addresses of the interfaces that are up and support multicast (loopback excluded).</summary>
    public static IReadOnlyList<IPAddress> LocalIPv4Addresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.SupportsMulticast
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Distinct()
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return Array.Empty<IPAddress>();
        }
    }

    /// <summary>GET /rpc/<paramref name="method"/><paramref name="query"/>; answers 401 with digest auth when a password is set.</summary>
    public async Task<JsonElement> CallAsync(IPAddress ip, string method, string query, string? password, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? _timeout);
        var path = $"/rpc/{method}{query}";
        var url = new Uri($"http://{ip}{path}");
        var host = ip.ToString();

        try
        {
            // Reuse the last challenge of this device (nonce count increments); a stale nonce just costs one 401.
            using (var first = await SendAsync(url, path, host, password, _nonces.TryGetValue(host, out var known) ? known : null, cts.Token))
            {
                if (first.StatusCode != HttpStatusCode.Unauthorized)
                    return await ReadAsync(first, method, cts.Token);

                var challenge = DigestAuth.ParseChallenge(first.Headers.WwwAuthenticate.ToString());
                if (string.IsNullOrEmpty(password))
                    throw new ShellyAuthenticationException($"{method}: the device requires a password", passwordMissing: true);
                if (challenge is null)
                    throw new ShellyRpcException(method, "401 without a usable Digest challenge", 401);
                var state = new NonceState(challenge);
                _nonces[host] = state;
                using var second = await SendAsync(url, path, host, password, state, cts.Token);
                if (second.StatusCode == HttpStatusCode.Unauthorized)
                {
                    _nonces.TryRemove(host, out _);
                    throw new ShellyAuthenticationException($"{method}: password rejected", passwordMissing: false);
                }
                return await ReadAsync(second, method, cts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{method} at {ip}: no answer within {(timeout ?? _timeout).TotalSeconds:0.#} s");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, string path, string host, string? password, NonceState? auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth is not null && !string.IsNullOrEmpty(password))
            req.Headers.TryAddWithoutValidation("Authorization",
                DigestAuth.BuildAuthorization(auth.Challenge, User, password, "GET", path, auth.Next()));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage resp, string method, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body.Length == 0 ? "null" : body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ShellyRpcException(method, $"HTTP {(int)resp.StatusCode}, not JSON", (int)resp.StatusCode);
        }

        // RPC errors come as {"code":-114,"message":"Method ... failed: not found"} (HTTP 4xx/5xx).
        if (!resp.IsSuccessStatusCode || (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("code", out _) && root.TryGetProperty("message", out _) && root.EnumerateObject().Count() <= 2))
        {
            var message = ShellyJson.Str(root, "message") ?? $"HTTP {(int)resp.StatusCode}";
            throw new ShellyRpcException(method, message, (int)resp.StatusCode);
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new ShellyRpcException(method, "answer is not a JSON object", (int)resp.StatusCode);
        return root;
    }

    public void Dispose() => _http.Dispose();
}
