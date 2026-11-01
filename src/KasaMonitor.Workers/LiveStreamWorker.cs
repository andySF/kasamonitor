using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Protocol.Tapo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// Live stream of every plug/channel with <see cref="Plug.StreamEnabled"/> (default: main meter and plugs on,
/// sub-meters off), for Shelly (one persistent WebSocket per device, <c>EM1.GetStatus</c>/<c>Switch.GetStatus</c>/...
/// per channel, answers matched by <c>id</c>), Kasa legacy (<c>emeter.get_realtime</c> through the shared client) and
/// Tapo KLAP/AES (<c>get_energy_usage</c> on the transport's cached session, re-authenticated by the transport).
/// <list type="bullet">
/// <item>Ticks are aligned to the start of each wall-clock second; a sample is stamped with the nearest second.</item>
/// <item>Interval: the main meter fixed at the base interval (<see cref="SettingKeys.LiveStreamIntervalSeconds"/>, ≥ 1 s);
/// every other channel adaptive, 1 → 10 s (<see cref="AdaptiveInterval"/>). A jump of the main meter larger than
/// <see cref="SettingKeys.LiveBurstTriggerW"/> (default 50 W) is published as a house event: every other channel is
/// read at once and stays at 1 s for 60 s.</item>
/// <item>Storage: every sample goes to the UI and to the minute aggregates; a raw row is written only when the power
/// changed (<see cref="ChangeRule"/>) or 60 s after the last written one (<see cref="ChangeFilter"/>), in batches
/// (one transaction every <see cref="FlushInterval"/>).</item>
/// <item>A device that stops answering (3 ticks with every due request failing, or a closed WebSocket) is re-opened
/// with backoff 1 → 30 s. Notifications on a Shelly socket are logged at Debug and update the relay state.</item>
/// </list>
/// The 10-second polling (Samples, /metrics) is not affected.
/// </summary>
public sealed class LiveStreamWorker : BackgroundService
{
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan SuperviseInterval = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HouseEventBoost = TimeSpan.FromSeconds(60);

    /// <summary>Consecutive ticks in which every due request failed before the device is re-opened (with backoff).</summary>
    public const int MaxFailedTicks = 3;

    /// <summary>Unsaved samples kept when the database is unavailable; older ones are dropped beyond this.</summary>
    public const int MaxBufferedSamples = 50_000;

    public enum StreamKind { Shelly, Legacy, Tapo }

    /// <summary>One streamed channel; <see cref="IsMain"/> = main meter (fixed 1 s, source of house events).</summary>
    public sealed record StreamChannel(Guid PlugId, int Channel, string Alias, bool IsMain);

    /// <summary>
    /// One device to stream (Shelly: all streamed channels of one MAC on one WebSocket; Kasa/Tapo: one plug).
    /// <see cref="AllChannels"/> are every polled channel of the device (relay notifications map to them).
    /// </summary>
    public sealed record StreamTarget(string Key, StreamKind Kind, IPAddress Ip, string Protocol, IReadOnlyList<StreamChannel> Channels,
        IReadOnlyList<StreamChannel> AllChannels, int BaseSeconds, string? ShellyPassword, TapoCredentials? Tapo)
    {
        public string Signature =>
            $"{Kind}|{Ip}|{Protocol}|{BaseSeconds}|{ShellyPassword?.GetHashCode()}|{Tapo?.GetHashCode()}|" +
            string.Join(",", Channels.Select(c => $"{c.Channel}:{c.PlugId}:{c.IsMain}"));
    }

    private sealed record RunningStream(StreamTarget Target, CancellationTokenSource Cts, Task Task);

    /// <summary>Set as soon as a connection delivers a sample, even if it fails later (resets the backoff).</summary>
    private sealed class Session { public bool Streamed; }

    /// <summary>An open device: reads one channel; <see cref="Completion"/> ends when the connection is lost.</summary>
    private interface ILiveSession : IAsyncDisposable
    {
        Task<EmeterReading?> ReadAsync(StreamChannel channel, CancellationToken ct);
        Task Completion { get; }
    }

    private sealed class DelegateSession(Func<StreamChannel, CancellationToken, Task<EmeterReading?>> read) : ILiveSession
    {
        public Task<EmeterReading?> ReadAsync(StreamChannel channel, CancellationToken ct) => read(channel, ct);
        public Task Completion { get; } = new TaskCompletionSource().Task;   // HTTP/TCP per request: nothing to lose
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ShellySession(IShellyRpcSocket socket, ShellyMeterKind kind, string method, Action<StreamChannel, bool> relay) : ILiveSession
    {
        public Task Completion => socket.Completion;

        public async Task<EmeterReading?> ReadAsync(StreamChannel channel, CancellationToken ct)
        {
            var result = await socket.CallAsync(method, new Dictionary<string, object> { ["id"] = channel.Channel }, RequestTimeout, ct);
            var reading = ShellyStatusParser.ParseComponent(kind, result);
            if (kind == ShellyMeterKind.Switch && reading?.RelayOn is { } on) relay(channel, on);
            return reading;
        }

        public ValueTask DisposeAsync() => socket.DisposeAsync();
    }

    /// <summary>Schedule of one channel inside a device loop.</summary>
    private sealed class ChannelState(StreamChannel channel, AdaptiveInterval interval)
    {
        public StreamChannel Channel { get; } = channel;
        public AdaptiveInterval Interval { get; } = interval;
        public DateTimeOffset NextDue = DateTimeOffset.MinValue;
        public DateTimeOffset? PreviousTs;
        public double? PreviousW;
        public int ScheduledSeconds = 1;
        public long BoostEpoch;
    }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKasaSettingsService _settings;
    private readonly ILiveStreamBroadcaster _broadcaster;
    private readonly IShellyRpcSocketFactory _sockets;
    private readonly IKasaClient _kasa;
    private readonly ITapoClient _tapo;
    private readonly ILogger<LiveStreamWorker> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan? _tickOverride;

    private readonly Dictionary<string, RunningStream> _streams = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<LiveSample> _samples = new();
    private readonly ConcurrentQueue<LiveMinuteAggregate> _minutes = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTs = new();
    private readonly ConcurrentDictionary<string, int> _failuresLogged = new(StringComparer.Ordinal);
    private readonly LiveMinuteAggregator _aggregator = new();
    private readonly ChangeFilter _filter = new();
    private readonly object _aggLock = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private long _samplesReceived, _samplesWritten, _boostEpoch, _boostUntilTicks;

    /// <summary>Samples accepted (published and aggregated) since start, all channels.</summary>
    public long SamplesReceived => Interlocked.Read(ref _samplesReceived);

    /// <summary>Samples selected for a raw row (changed, or the 60 s witness) since start.</summary>
    public long SamplesWritten => Interlocked.Read(ref _samplesWritten);

    /// <param name="tickOverride">Test hook: loop period instead of the wall-clock half-second alignment.</param>
    public LiveStreamWorker(IServiceScopeFactory scopeFactory, IKasaSettingsService settings, ILiveStreamBroadcaster broadcaster,
        IShellyRpcSocketFactory sockets, IKasaClient kasa, ITapoClient tapo, ILogger<LiveStreamWorker> log,
        Func<DateTimeOffset>? clock = null, TimeSpan? tickOverride = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _broadcaster = broadcaster;
        _sockets = sockets;
        _kasa = kasa;
        _tapo = tapo;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tickOverride = tickOverride;
        _broadcaster.HouseEvent += OnHouseEvent;
    }

    /// <summary>Keys of the devices currently streamed (Shelly: MAC; Kasa/Tapo: MAC-channel).</summary>
    public IReadOnlyCollection<string> StreamedDevices
    {
        get { lock (_streams) return _streams.Keys.ToList(); }
    }

    /// <summary>Main-meter jump (W, between two readings) that sends every plug back to 1 s (setting LiveBurstTriggerW).</summary>
    public double BurstTriggerW { get; private set; } = KasaSettingsDefaults.DefaultLiveBurstTriggerW;

    /// <summary>Until when the non-main channels stay at 1 s after the last house event.</summary>
    public DateTimeOffset BoostUntil => new(Interlocked.Read(ref _boostUntilTicks), TimeSpan.Zero);

    /// <summary>What should be streamed now; empty when the stream is disabled.</summary>
    public static IReadOnlyList<StreamTarget> Targets(IEnumerable<Plug> plugs, KasaRuntimeSettings settings, ITapoClient? tapo = null)
    {
        if (!settings.LiveStreamEnabled) return Array.Empty<StreamTarget>();
        var baseSeconds = Math.Max(KasaSettingsService.MinLiveIntervalSeconds, settings.LiveStreamIntervalSeconds);
        var polled = plugs.Where(p => p.IsPolled).ToList();
        StreamChannel Ch(Plug p) => new(p.Id, p.Channel, p.Alias, p.Role == PlugRole.MainMeter);
        var targets = new List<StreamTarget>();

        foreach (var g in polled.Where(p => p.Protocol == PlugProtocols.ShellyRpc).GroupBy(p => p.Mac, StringComparer.Ordinal))
        {
            var all = g.OrderBy(p => p.Channel).ToList();
            var streamed = all.Where(p => p.StreamEnabled).Select(Ch).ToList();
            if (streamed.Count == 0) continue;
            targets.Add(new StreamTarget(g.Key, StreamKind.Shelly, all[0].IpAddress, PlugProtocols.ShellyRpc, streamed,
                all.Select(Ch).ToList(), baseSeconds, settings.ShellyCredentials?.Password, null));
        }
        foreach (var p in polled.Where(p => p.StreamEnabled && p.Protocol == PlugProtocols.Legacy))
            targets.Add(new StreamTarget(p.MacLabel, StreamKind.Legacy, p.IpAddress, p.Protocol, new[] { Ch(p) }, new[] { Ch(p) },
                baseSeconds, null, null));
        if (settings.TapoCredentials is { } creds)
            foreach (var p in polled.Where(p => p.StreamEnabled && p.Protocol is PlugProtocols.Klap or PlugProtocols.Aes
                                                && (tapo?.SupportsProtocol(p.Protocol) ?? true)))
                targets.Add(new StreamTarget(p.MacLabel, StreamKind.Tapo, p.IpAddress, p.Protocol, new[] { Ch(p) }, new[] { Ch(p) },
                    baseSeconds, null, creds));
        return targets;
    }

    /// <summary>Starts, restarts (changed IP/channels/interval/credentials) or stops the per-device streams.</summary>
    public async Task SuperviseOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<Plug> plugs;
        using (var scope = _scopeFactory.CreateScope())
            plugs = await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        var settings = await _settings.GetAsync(ct);
        BurstTriggerW = settings.LiveBurstTriggerW;
        var targets = Targets(plugs, settings, _tapo).ToDictionary(t => t.Key, StringComparer.Ordinal);

        List<RunningStream> toStop;
        lock (_streams)
        {
            toStop = _streams.Values
                .Where(s => !targets.TryGetValue(s.Target.Key, out var t) || t.Signature != s.Target.Signature)
                .ToList();
            foreach (var s in toStop) _streams.Remove(s.Target.Key);
        }
        foreach (var s in toStop) await StopAsync(s);

        lock (_streams)
        {
            foreach (var t in targets.Values.Where(t => !_streams.ContainsKey(t.Key)))
            {
                var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _streams[t.Key] = new RunningStream(t, cts, Task.Run(() => RunDeviceAsync(t, cts.Token), CancellationToken.None));
            }
        }
    }

    private static async Task StopAsync(RunningStream s)
    {
        s.Cts.Cancel();
        try { await s.Task; } catch (Exception) { /* RunDeviceAsync handles its own errors */ }
        s.Cts.Dispose();
    }

    /// <summary>Connection loop of one device: open, stream until it fails, back off (1 → 30 s), repeat.</summary>
    private async Task RunDeviceAsync(StreamTarget target, CancellationToken ct)
    {
        var backoff = MinBackoff;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            var session = new Session();
            try
            {
                await using var live = await OpenAsync(target, ct);
                await StreamAsync(target, live, session, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (session.Streamed) failures = 0;   // a working connection broke: report it once more
                failures++;
                _failuresLogged[target.Key] = failures;
                if (failures == 1)
                    _log.LogWarning("Live stream {Key} ({Ip}, {Kind}): {Reason}; retrying with backoff", target.Key, target.Ip, target.Kind, ex.Message);
                else
                    _log.LogDebug("Live stream {Key} ({Ip}) failed again ({Failures}): {Reason}", target.Key, target.Ip, failures, ex.Message);
            }
            if (session.Streamed) backoff = MinBackoff;   // the connection worked: start the backoff again from 1 s
            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, backoff.Ticks * 2));
        }
    }

    private async Task<ILiveSession> OpenAsync(StreamTarget target, CancellationToken ct)
    {
        if (target.Kind != StreamKind.Shelly)
            _log.LogDebug("Live stream {Key} ({Ip}, {Kind}) started", target.Key, target.Ip, target.Kind);
        switch (target.Kind)
        {
            case StreamKind.Legacy:
                return new DelegateSession(async (_, c) => await _kasa.GetRealtimeAsync(target.Ip, c));
            case StreamKind.Tapo:
                return new DelegateSession(async (_, c) => await _tapo.GetLivePowerAsync(target.Ip, target.Protocol, target.Tapo!, c));
        }

        var socket = await _sockets.ConnectAsync(target.Ip, target.ShellyPassword, ct);
        try
        {
            socket.Notification += (method, parameters) => OnNotification(target, method, parameters);
            // First frame: a full status (valid src -> the device starts sending notifications on this connection).
            var status = await socket.CallAsync("Shelly.GetStatus", null, RequestTimeout, ct);
            var kind = ShellyStatusParser.Parse(status).Kind;
            var method = ShellyStatusParser.ComponentStatusMethod(kind)
                ?? throw new InvalidOperationException("no measuring component (em, em1, pm1, switch with apower)");
            PublishRelays(target, status);
            _log.LogInformation("Live stream {Key} ({Ip}) connected: {Method} for channels {Channels}",
                target.Key, target.Ip, method, string.Join(",", target.Channels.Select(m => m.Channel)));
            return new ShellySession(socket, kind, method, (ch, on) => _broadcaster.PublishRelay(new RelayState(ch.PlugId, on, _clock())));
        }
        catch
        {
            await socket.DisposeAsync();
            throw;
        }
    }

    private Task WaitTickAsync(CancellationToken ct) =>
        Task.Delay(_tickOverride ?? LiveClock.DelayToNextSecond(_clock()), ct);

    /// <summary>One open device: ticks at the start of every second and reads the channels that are due.</summary>
    private async Task StreamAsync(StreamTarget target, ILiveSession live, Session session, CancellationToken ct)
    {
        var states = target.Channels.Select(c => new ChannelState(c, new AdaptiveInterval(c.IsMain, target.BaseSeconds))
        {
            ScheduledSeconds = target.BaseSeconds,
            BoostEpoch = Interlocked.Read(ref _boostEpoch),
        }).ToList();
        var failedTicks = 0;
        while (true)
        {
            await WaitTickAsync(ct);
            if (live.Completion.IsCompleted) throw new WebSocketException("connection closed");
            var ts = LiveClock.RoundToSecond(_clock());
            var epoch = Interlocked.Read(ref _boostEpoch);
            var boostUntil = BoostUntil;
            foreach (var st in states.Where(s => s.BoostEpoch != epoch))
            {
                st.BoostEpoch = epoch;
                if (st.Channel.IsMain) continue;
                st.Interval.Boost();
                st.ScheduledSeconds = target.BaseSeconds;
                st.NextDue = DateTimeOffset.MinValue;   // read at once
            }

            var due = states.Where(s => s.NextDue <= ts).ToList();
            if (due.Count == 0) continue;
            var results = await Task.WhenAll(due.Select(st => ReadOneAsync(target, live, st, ts, boostUntil, ct)));
            if (results.Any(r => r))
            {
                if (!session.Streamed && _failuresLogged.TryRemove(target.Key, out var before))
                    _log.LogInformation("Live stream {Key} ({Ip}) receiving again after {Failures} failed attempts", target.Key, target.Ip, before);
                session.Streamed = true;
                failedTicks = 0;
            }
            else if (++failedTicks >= MaxFailedTicks)
            {
                throw new TimeoutException($"no answer in {MaxFailedTicks} consecutive ticks");
            }
        }
    }

    private async Task<bool> ReadOneAsync(StreamTarget target, ILiveSession live, ChannelState st, DateTimeOffset ts, DateTimeOffset boostUntil,
        CancellationToken ct)
    {
        try
        {
            if (await live.ReadAsync(st.Channel, ct) is not { } reading) return false;
            var w = reading.PowerMw / 1000.0;
            if (st.PreviousTs is { } prev && ts <= prev) return true;   // two ticks rounded to the same second
            // The sample stands for the interval it was scheduled with, never for more than the time since the last one.
            var seconds = st.PreviousTs is { } p ? Math.Min((ts - p).TotalSeconds, st.ScheduledSeconds) : st.ScheduledSeconds;
            Accept(new LiveSample(st.Channel.PlugId, ts, w, reading.ApparentPowerVa,
                reading.VoltageMv / 1000.0, reading.CurrentMa / 1000.0, reading.PowerFactor, reading.FrequencyHz, seconds));
            if (st.Channel.IsMain && st.PreviousW is { } pw && Math.Abs(w - pw) > BurstTriggerW)
                _broadcaster.PublishHouseEvent(ts);
            st.PreviousTs = ts;
            st.PreviousW = w;
            st.ScheduledSeconds = st.Interval.Next(ts, w, boostUntil);
            st.NextDue = ts.AddSeconds(st.ScheduledSeconds);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug("Live stream {Key} ch{Channel}: read failed: {Reason}", target.Key, st.Channel.Channel, ex.Message);
            return false;
        }
    }

    private void OnHouseEvent(DateTimeOffset at)
    {
        var until = (at + HouseEventBoost).UtcTicks;
        long current;
        do { current = Interlocked.Read(ref _boostUntilTicks); }
        while (until > current && Interlocked.CompareExchange(ref _boostUntilTicks, until, current) != current);
        Interlocked.Increment(ref _boostEpoch);
    }

    /// <summary>
    /// Aggregates and buffers one sample (at most one per plug and second), then publishes it to the UI: a failing
    /// subscriber can no longer cost the sample.
    /// </summary>
    public void Accept(LiveSample sample)
    {
        var duplicate = false;
        _lastTs.AddOrUpdate(sample.PlugId, sample.TsUtc, (_, last) =>
        {
            if (sample.TsUtc <= last) { duplicate = true; return last; }
            return sample.TsUtc;
        });
        if (duplicate) return;
        Interlocked.Increment(ref _samplesReceived);
        LiveMinuteAggregate? done;
        bool write;
        lock (_aggLock)
        {
            done = _aggregator.Add(sample);
            write = _filter.ShouldWrite(sample);
        }
        if (write)
        {
            _samples.Enqueue(sample);
            Interlocked.Increment(ref _samplesWritten);
        }
        if (done is not null) _minutes.Enqueue(done);
        _broadcaster.Publish(sample);
        if (write) _broadcaster.PublishStored(sample);   // remote write: the same samples as the raw rows
    }

    private void OnNotification(StreamTarget target, string method, JsonElement parameters)
    {
        if (_log.IsEnabled(LogLevel.Debug))
            _log.LogDebug("Shelly {Mac} {Method}: {Params}", target.Key, method,
                parameters.ValueKind == JsonValueKind.Undefined ? "" : parameters.GetRawText());
        if (method is "NotifyStatus" or "NotifyFullStatus" && parameters.ValueKind == JsonValueKind.Object)
            PublishRelays(target, parameters);
    }

    /// <summary><c>switch:N.output</c> → relay state of the device's channel N (EM Gen3: the relay is shown on channel 0).</summary>
    private void PublishRelays(StreamTarget target, JsonElement statusOrParams)
    {
        foreach (var (id, on) in ShellyStatusParser.Relays(statusOrParams))
            if (target.AllChannels.FirstOrDefault(c => c.Channel == id) is { } ch)
                _broadcaster.PublishRelay(new RelayState(ch.PlugId, on, _clock()));
    }

    /// <summary>Stores buffered raw samples and the minutes completed by now in one transaction.</summary>
    /// <param name="all">Shutdown: also stores the minutes still open (merged later with the rest of that minute).</param>
    public async Task FlushAsync(CancellationToken ct, bool all = false)
    {
        await _flushLock.WaitAsync(CancellationToken.None);
        try
        {
            lock (_aggLock)
                foreach (var m in all ? _aggregator.TakeAll() : _aggregator.TakeCompleted(_clock()))
                    _minutes.Enqueue(m);

            var samples = new List<LiveSample>();
            while (_samples.TryDequeue(out var s)) samples.Add(s);
            var minutes = new List<LiveMinuteAggregate>();
            while (_minutes.TryDequeue(out var m)) minutes.Add(m);
            if (samples.Count == 0 && minutes.Count == 0) return;
            try
            {
                // Never cancelled half-way: a batch taken out of the queue is either stored or put back.
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ILiveRepository>().SaveBatchAsync(samples, minutes, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Live stream: storing {Samples} samples / {Minutes} minutes failed, kept for the next attempt: {Reason}",
                    samples.Count, minutes.Count, ex.Message);
                foreach (var s in samples.TakeLast(MaxBufferedSamples)) _samples.Enqueue(s);
                foreach (var m in minutes) _minutes.Enqueue(m);
            }
        }
        finally
        {
            _flushLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flusher = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(FlushInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    try { await FlushAsync(stoppingToken); }
                    catch (Exception ex) { _log.LogError(ex, "Live stream flush failed"); }
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SuperviseOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Live stream supervision failed"); }
            try { await Task.Delay(SuperviseInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        List<RunningStream> running;
        lock (_streams)
        {
            running = _streams.Values.ToList();
            _streams.Clear();
        }
        foreach (var s in running) await StopAsync(s);
        await flusher;
        try { await FlushAsync(CancellationToken.None, all: true); }
        catch (Exception ex) { _log.LogWarning("Live stream: final flush failed: {Reason}", ex.Message); }
    }

    public override void Dispose()
    {
        _broadcaster.HouseEvent -= OnHouseEvent;
        base.Dispose();
    }
}
