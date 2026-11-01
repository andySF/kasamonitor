using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>A persistent RPC channel to one Shelly device (WebSocket <c>ws://&lt;ip&gt;/rpc</c>).</summary>
public interface IShellyRpcSocket : IAsyncDisposable
{
    /// <summary>Sends one request and waits for the answer with the same <c>id</c>; <see cref="TimeoutException"/> after <paramref name="timeout"/>.</summary>
    Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct);

    /// <summary>Unsolicited frames (<c>NotifyStatus</c>, <c>NotifyFullStatus</c>, <c>NotifyEvent</c>): method name and <c>params</c>.</summary>
    event Action<string, JsonElement>? Notification;

    /// <summary>Completes when the connection is closed (by the device, the network or <see cref="IAsyncDisposable.DisposeAsync"/>).</summary>
    Task Completion { get; }
}

public interface IShellyRpcSocketFactory
{
    Task<IShellyRpcSocket> ConnectAsync(IPAddress ip, string? password, CancellationToken ct);
}

/// <summary>Production factory: <c>ws://&lt;ip&gt;/rpc</c>, 3 s to connect.</summary>
public sealed class ShellyRpcSocketFactory : IShellyRpcSocketFactory
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    public async Task<IShellyRpcSocket> ConnectAsync(IPAddress ip, string? password, CancellationToken ct) =>
        await ShellyRpcSocket.ConnectAsync(new Uri($"ws://{ip}/rpc"), password, ConnectTimeout, ct);
}

/// <summary>
/// Shelly Gen2+ JSON-RPC over WebSocket (inbound: we connect to the device). Frames are
/// <c>{"id":n,"src":"&lt;client&gt;","method":"EM1.GetStatus","params":{"id":0}}</c>; the answer carries the same <c>id</c>
/// (<c>result</c> or <c>error</c>). Frames without <c>id</c> are notifications. Every request carries a valid <c>src</c>,
/// which is what makes the device send notifications on this connection. Authentication (when the device has a
/// password): the device answers an RPC error 401 whose message is a JSON digest challenge; the request is repeated
/// with an <c>auth</c> object (<see cref="DigestAuth.BuildWsAuth"/>), which is then added to every later frame.
/// Requests may be in flight concurrently; sending is serialised. Reconnecting is the caller's job.
/// </summary>
public sealed class ShellyRpcSocket : IShellyRpcSocket
{
    /// <summary>Largest accepted frame (a full status is a few kB).</summary>
    public const int MaxMessageBytes = 1 << 20;

    private readonly WebSocket _ws;
    private readonly string? _password;
    private readonly string _src;
    private readonly ConcurrentDictionary<long, (string Method, TaskCompletionSource<JsonElement> Tcs)> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _receive;
    private long _nextId;
    private DigestAuth.WsChallenge? _challenge;
    private int _disposed;

    public event Action<string, JsonElement>? Notification;

    public Task Completion => _receive;

    /// <summary>Why the connection ended (null while open).</summary>
    public string? CloseReason { get; private set; }

    /// <summary>The <c>src</c> sent in every frame.</summary>
    public string Source => _src;

    /// <param name="ws">An open WebSocket (tests pass one built with <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>).</param>
    /// <param name="source">The <c>src</c> of every frame; a unique "kasamonitor-xxxxxxxx" by default.</param>
    public ShellyRpcSocket(WebSocket ws, string? password, string? source = null)
    {
        _ws = ws;
        _password = string.IsNullOrEmpty(password) ? null : password;
        _src = source ?? "kasamonitor-" + Guid.NewGuid().ToString("N")[..8];
        _receive = Task.Run(ReceiveLoopAsync);
    }

    public static async Task<ShellyRpcSocket> ConnectAsync(Uri uri, string? password, TimeSpan connectTimeout, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(connectTimeout);
        try
        {
            await ws.ConnectAsync(uri, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TimeoutException($"{uri}: WebSocket not connected within {connectTimeout.TotalSeconds:0.#} s");
        }
        catch
        {
            ws.Dispose();
            throw;
        }
        return new ShellyRpcSocket(ws, password);
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            return await SendAndWaitAsync(method, parameters, timeout, ct);
        }
        catch (ShellyRpcException ex) when (ex.HttpStatus == 401)
        {
            if (_password is null)
                throw new ShellyAuthenticationException($"{method}: the device requires a password", passwordMissing: true);
            var challenge = DigestAuth.ParseWsChallenge(ex.Detail)
                ?? throw new ShellyRpcException(method, "401 without a usable digest challenge", 401);
            _challenge = challenge;
            try
            {
                return await SendAndWaitAsync(method, parameters, timeout, ct);
            }
            catch (ShellyRpcException again) when (again.HttpStatus == 401)
            {
                _challenge = null;
                throw new ShellyAuthenticationException($"{method}: password rejected", passwordMissing: false);
            }
        }
    }

    private async Task<JsonElement> SendAndWaitAsync(string method, object? parameters, TimeSpan timeout, CancellationToken ct)
    {
        if (_receive.IsCompleted) throw new WebSocketException($"{method}: connection closed ({CloseReason})");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (method, tcs);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            var frame = new Dictionary<string, object?> { ["id"] = id, ["src"] = _src, ["method"] = method };
            if (parameters is not null) frame["params"] = parameters;
            if (_challenge is { } c && _password is not null) frame["auth"] = DigestAuth.BuildWsAuth(c, ShellyClient.User, _password);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
            await _sendLock.WaitAsync(cts.Token);
            try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cts.Token); }
            finally { _sendLock.Release(); }
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{method}: no answer within {timeout.TotalSeconds:0.#} s");
        }
        finally
        {
            _pending.TryRemove(id, out _);   // a late answer to this id is ignored
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        var message = new MemoryStream();
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _closing.Token);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        CloseReason = $"closed by the device ({r.CloseStatus} {r.CloseStatusDescription})";
                        return;
                    }
                    message.Write(buffer, 0, r.Count);
                    if (message.Length > MaxMessageBytes)
                        throw new InvalidDataException($"frame larger than {MaxMessageBytes} bytes");
                } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Text) Dispatch(message.ToArray());
            }
            CloseReason ??= "closed";
        }
        catch (OperationCanceledException) { CloseReason ??= "closed"; }
        catch (Exception ex) { CloseReason ??= ex.Message; }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            foreach (var (id, p) in _pending.ToArray())
                if (_pending.TryRemove(id, out _))
                    p.Tcs.TrySetException(new WebSocketException($"{p.Method}: connection closed ({CloseReason})"));
        }
    }

    private void Dispatch(byte[] utf8)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return; }   // not JSON: ignore the frame
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number && idEl.TryGetInt64(out var id)
            && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
        {
            if (!_pending.TryGetValue(id, out var p)) return;   // unknown or timed-out id
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var code = ShellyJson.Num(err, "code") is { } n ? (int)n : (int?)null;
                p.Tcs.TrySetException(new ShellyRpcException(p.Method, ShellyJson.Str(err, "message") ?? $"error {code}", code));
            }
            else
            {
                p.Tcs.TrySetResult(root.GetProperty("result").Clone());
            }
            return;
        }

        if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
        {
            var parameters = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            try { Notification?.Invoke(m.GetString()!, parameters); }
            catch (Exception) { /* a faulty handler must not stop the receive loop */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
        }
        catch (Exception) { /* already broken */ }
        _closing.Cancel();
        try { await _receive.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { /* receive loop never throws; timeout only */ }
        _ws.Dispose();
        _closing.Dispose();
        _sendLock.Dispose();
    }
}
