using KasaMonitor.Core.Miele;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// Reads the Miele appliances through the SSE stream <c>GET /v1/devices/all/events</c> (one connection for all of
/// them). At every (re)connection it first makes one full <c>GET /v1/devices</c>. No line for 60 s (PING comes after
/// ~20 s of silence) = dead connection; reconnection waits 5 s, 10 s, 20 s … at most 5 minutes after consecutive
/// failures. After 3 failed streams in a row it falls back to <c>GET /v1/devices</c> every 60 s until a stream works
/// again. The stream is also closed (and reopened with the new token) when the access token is due for refresh, and
/// at „Deconectează”. An API 401 triggers one forced refresh and one retry. No other periodic call is made.
/// </summary>
public sealed class MieleStreamWorker : BackgroundService
{
    /// <summary>Wait while nothing can be read (not configured, not connected): only the settings are re-read.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);

    private readonly IMieleSettingsService _settings;
    private readonly MieleAuthService _auth;
    private readonly IMieleClient _client;
    private readonly MieleRecorder _recorder;
    private readonly MieleState _state;
    private readonly ILogger<MieleStreamWorker> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private CancellationTokenSource? _connection;
    private bool _loaded;

    /// <param name="wait">Test hook replacing the wait between attempts (default: the delay, cut short by <see cref="Wake"/>).</param>
    /// <param name="idleTimeout">Test hook (default <see cref="MieleApi.StreamIdleTimeout"/>).</param>
    public MieleStreamWorker(IMieleSettingsService settings, MieleAuthService auth, IMieleClient client, MieleRecorder recorder, MieleState state,
        ILogger<MieleStreamWorker> log, Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? wait = null, TimeSpan? idleTimeout = null)
    {
        _settings = settings;
        _auth = auth;
        _client = client;
        _recorder = recorder;
        _state = state;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _wait = wait ?? (async (d, c) => await _wake.WaitAsync(d, c));
        _idleTimeout = idleTimeout ?? MieleApi.StreamIdleTimeout;
        _auth.Connected += Wake;
        _auth.Disconnected += () =>
        {
            try { _connection?.Cancel(); } catch (ObjectDisposedException) { }
            Wake();
        };
    }

    /// <summary>SSE failures in a row (≥ <see cref="MieleApi.StreamFailuresBeforePolling"/> = polling mode).</summary>
    public int ConsecutiveFailures { get; private set; }

    public bool Polling => ConsecutiveFailures >= MieleApi.StreamFailuresBeforePolling;

    /// <summary>Connect now (after connecting the account or saving the settings) instead of waiting.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await RunOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Unexpected (e.g. database): counted as a failure, never retried faster than the backoff.
                ConsecutiveFailures++;
                delay = MieleApi.ReconnectDelay(ConsecutiveFailures);
                _state.SetError(ex.Message, _clock());
                _log.LogWarning(ex, "Miele worker failed; next attempt in {Delay}", delay);
            }
            try { await WaitAsync(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One attempt: settings check, token, full GET, then the stream until it ends. Returns the wait before the next
    /// attempt (in polling mode <see cref="WaitAsync"/> polls during it).
    /// </summary>
    public async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        if (!_loaded)
        {
            await _recorder.LoadAsync(ct);
            _loaded = true;
        }
        var s = await _settings.GetAsync(ct);
        string? idle = !s.HasClientId ? "Lipsește Client ID-ul Miele (Setări → Miele)."
            : !s.HasSecret ? "Lipsește secretul clientului Miele (" + (s.SecretProblem ?? "necunoscut") + ")."
            : s.Token is null ? s.AuthError ?? "Contul Miele nu e conectat."
            : null;
        if (idle is not null)
        {
            _state.SetStatus(idle);
            _state.SetStream(false, MieleStreamMode.Off, _clock());
            return IdleDelay;
        }

        try
        {
            await FullReadAsync(ct);
        }
        catch (MieleDisconnectedException ex)
        {
            _state.SetStatus(ex.Message);
            _state.SetStream(false, MieleStreamMode.Off, _clock());
            return IdleDelay;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return Failed("GET /devices: " + ex.Message, gotEvents: false);
        }
        _state.SetStatus(null);
        if (Polling) _state.SetStream(false, MieleStreamMode.Polling, _clock());

        var got = false;
        try
        {
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _connection = connection;
            var planned = await StreamAsync(connection, () => got = true, ct);
            if (planned)
            {
                ConsecutiveFailures = 0;
                return TimeSpan.Zero;
            }
            return Failed("stream închis de server", got);
        }
        catch (MieleDisconnectedException ex)
        {
            _state.SetStatus(ex.Message);
            return IdleDelay;
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return Failed("SSE: " + ex.Message, got);
        }
        finally
        {
            _connection = null;
            _state.SetStream(false, Polling ? MieleStreamMode.Polling : MieleStreamMode.Off, _clock());
        }
    }

    private TimeSpan Failed(string reason, bool gotEvents)
    {
        // A stream that delivered events and then dropped starts the count again.
        ConsecutiveFailures = gotEvents ? 1 : ConsecutiveFailures + 1;
        var delay = MieleApi.ReconnectDelay(ConsecutiveFailures);
        _state.SetError(reason, _clock());
        if (ConsecutiveFailures == MieleApi.StreamFailuresBeforePolling)
            _log.LogWarning("Miele: SSE failed {Count} times in a row ({Reason}); polling GET /devices every {Interval} until it works again",
                ConsecutiveFailures, reason, MieleApi.PollInterval);
        else
            _log.LogInformation("Miele: {Reason}; failure {Count} in a row, next connection in {Delay}", reason, ConsecutiveFailures, delay);
        return delay;
    }

    /// <summary>The wait between attempts; in polling mode at least 60 s, with a GET /devices every 60 s (the last one is the next attempt's).</summary>
    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (!Polling)
        {
            await _wait(delay, ct);
            return;
        }
        var remaining = delay < MieleApi.PollInterval ? MieleApi.PollInterval : delay;
        while (remaining > MieleApi.PollInterval)
        {
            await _wait(MieleApi.PollInterval, ct);
            remaining -= MieleApi.PollInterval;
            try
            {
                await FullReadAsync(ct);
                _state.SetStream(false, MieleStreamMode.Polling, _clock());
            }
            catch (Exception ex) when (ex is MieleDisconnectedException || IsTransient(ex, ct))
            {
                _state.SetError("polling: " + ex.Message, _clock());
                if (ex is MieleDisconnectedException) return;
            }
        }
        await _wait(remaining, ct);
    }

    /// <summary>One GET /v1/devices, processed like a devices event.</summary>
    public async Task FullReadAsync(CancellationToken ct)
    {
        var json = await WithTokenAsync(t => _client.GetDevicesJsonAsync(t, ct), ct);
        var n = await _recorder.ProcessAsync(json, _clock(), ct);
        _log.LogDebug("Miele: GET /devices returned {Count} appliances", n);
    }

    /// <summary>Reads the stream until it ends. True = closed on purpose (token refresh due, disconnect); false = ended by the server.</summary>
    private async Task<bool> StreamAsync(CancellationTokenSource connection, Action gotEvent, CancellationToken ct)
    {
        await using var stream = await WithTokenAsync(t => _client.OpenEventsAsync(t, connection.Token), connection.Token);
        var s = await _settings.GetAsync(ct);
        if (s.Token is { } token)
        {
            var untilRefresh = token.RefreshDueUtc - _clock();
            if (untilRefresh < TimeSpan.Zero) untilRefresh = TimeSpan.Zero;
            if (untilRefresh < TimeSpan.FromDays(20)) connection.CancelAfter(untilRefresh + TimeSpan.FromSeconds(1));
        }
        _state.SetStream(true, MieleStreamMode.Sse, _clock());
        _state.SetError(null, _clock());
        _log.LogInformation("Miele: SSE stream open");
        var parser = new MieleSseParser();
        var first = true;
        while (true)
        {
            string? line;
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(connection.Token))
            {
                idle.CancelAfter(_idleTimeout);
                try
                {
                    line = await stream.ReadLineAsync(idle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (connection.IsCancellationRequested) return true;
                    throw new TimeoutException("niciun eveniment în " + (int)_idleTimeout.TotalSeconds + " s");
                }
            }
            if (line is null) return false;
            if (parser.Feed(line) is not { } ev) continue;
            var now = _clock();
            _state.MarkEvent(string.IsNullOrEmpty(ev.Name) ? ev.Kind.ToString().ToLowerInvariant() : ev.Name, now);
            if (first)
            {
                first = false;
                ConsecutiveFailures = 0;
                gotEvent();
                _state.SetStream(true, MieleStreamMode.Sse, now);
            }
            await HandleAsync(ev, now, ct);
        }
    }

    private async Task HandleAsync(MieleSseEvent ev, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            switch (ev.Kind)
            {
                case MieleEventKind.Devices:
                    await _recorder.ProcessAsync(ev.Data, now, ct);
                    break;
                case MieleEventKind.Ping:
                    await _recorder.TickAsync(now, ct);
                    break;
                case MieleEventKind.Actions:
                    break;
                default:
                    _log.LogDebug("Miele: unknown SSE event {Name}", ev.Name);
                    break;
            }
            _state.Raise();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An odd payload or a database hiccup must not drop the stream.
            _state.SetError("eveniment " + ev.Kind + ": " + ex.Message, now);
            _log.LogWarning(ex, "Miele: {Kind} event could not be processed", ev.Kind);
        }
    }

    /// <summary>Calls with the access token; after a 401 one forced refresh and one retry.</summary>
    private async Task<T> WithTokenAsync<T>(Func<string, Task<T>> call, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        try
        {
            return await call(token);
        }
        catch (MieleAuthException)
        {
            _log.LogInformation("Miele: API answered 401, refreshing the token once");
            token = await _auth.ForceRefreshAsync(ct);
            return await call(token);
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is MieleException or HttpRequestException or TimeoutException or IOException
            or System.Text.Json.JsonException or OperationCanceledException;
}
