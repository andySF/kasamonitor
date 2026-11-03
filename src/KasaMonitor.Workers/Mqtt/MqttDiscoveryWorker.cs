using System.Threading.Channels;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Mqtt;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers.Mqtt;

/// <summary>
/// The MQTT Discovery integration (0.11, read only): keeps one connection to the broker (MQTTnet 5 has no managed client:
/// the reconnection with backoff 5 → 60 s and the re-subscription after every connection are here), subscribes to
/// <c>&lt;prefix&gt;/#</c> plus the state / availability / attribute topics of what was discovered, and hands every message
/// to one consumer through a channel (the receive loop never waits for the database). Discovery messages update the
/// <see cref="MqttRegistry"/>; added devices are kept in step by <see cref="GenericRecorder"/>, which stores their values,
/// rolls them up and purges them (maintenance every 5 s / 10 min / daily). Publishes nothing except the birth message
/// asked for on the MQTT tab („Cere republicarea”); never a command (R8).
/// </summary>
public sealed class MqttDiscoveryWorker : BackgroundService
{
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RollupEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private readonly TimeSpan _minBackoff;

    private readonly IMqttSettingsService _settings;
    private readonly IMqttTransportFactory _factory;
    private readonly MqttState _state;
    private readonly GenericRecorder _recorder;
    private readonly ILogger<MqttDiscoveryWorker>? _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Channel<MqttMessage> _channel = Channel.CreateBounded<MqttMessage>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly string _clientId = "kasamonitor-" + Guid.NewGuid().ToString("N")[..8];
    private volatile IMqttTransport? _transport;
    private volatile MqttRuntimeSettings? _current;
    private TaskCompletionSource _settingsChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public MqttDiscoveryWorker(IMqttSettingsService settings, IMqttTransportFactory factory, MqttState state, GenericRecorder recorder,
        ILogger<MqttDiscoveryWorker>? log = null, Func<DateTimeOffset>? clock = null, TimeSpan? minBackoff = null)
    {
        _minBackoff = minBackoff ?? TimeSpan.FromSeconds(5);
        _settings = settings;
        _factory = factory;
        _state = state;
        _recorder = recorder;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _settings.Changed += () => _settingsChanged.TrySetResult();
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await _recorder.LoadAsync(stop); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "MQTT: generic devices could not be loaded"); }
        var consumer = Task.Run(() => ConsumeAsync(stop), stop);
        var maintenance = Task.Run(() => MaintenanceAsync(stop), stop);
        var backoff = _minBackoff;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var s = await _settings.GetAsync(stop);
                _current = s;
                var changed = SettingsChanged();
                if (!s.Enabled || !s.IsConfigured)
                {
                    _state.SetDisconnected(!s.Enabled ? "citirea MQTT e oprită" : "neconfigurat: lipsește brokerul");
                    await Task.WhenAny(changed, Task.Delay(TimeSpan.FromMinutes(5), stop));
                    continue;
                }
                var (result, wasConnected) = await RunConnectionAsync(s, changed, stop);
                if (result is null) { backoff = _minBackoff; continue; }
                // R11 (round 2): a connection that worked starts the waits again from the shortest.
                if (wasConnected) backoff = _minBackoff;
                // Refused or dropped: wait (or a new save), then again, doubling the wait up to a minute.
                await Task.WhenAny(changed, Task.Delay(backoff, stop));
                backoff = TimeSpan.FromSeconds(Math.Min(Math.Max(MaxBackoff.TotalSeconds, _minBackoff.TotalSeconds), backoff.TotalSeconds * 2));
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "MQTT: connection loop failed");
                _state.SetDisconnected("Eroare: " + ex.Message, ex.Message);
                try { await Task.Delay(backoff, stop); } catch (OperationCanceledException) { break; }
            }
        }
        await Task.WhenAll(consumer.ContinueWith(_ => { }, TaskScheduler.Default), maintenance.ContinueWith(_ => { }, TaskScheduler.Default));
    }

    private Task SettingsChanged()
    {
        if (_settingsChanged.Task.IsCompleted) _settingsChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _settingsChanged.Task;
    }

    /// <summary>One connection: null when it ended because the settings changed (reconnect at once), else the error; whether it connected.</summary>
    private async Task<(string? Error, bool WasConnected)> RunConnectionAsync(MqttRuntimeSettings s, Task settingsChanged, CancellationToken stop)
    {
        await using var t = _factory.Create();
        var dropped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        t.MessageReceived += m =>
        {
            _channel.Writer.TryWrite(m);
            return Task.CompletedTask;
        };
        t.Disconnected += reason =>
        {
            dropped.TrySetResult(reason);
            return Task.CompletedTask;
        };
        var result = await t.ConnectAsync(new MqttConnectOptions(s.Host, s.Port, s.User.Length == 0 ? null : s.User, _settings.Password, s.UseTls, _clientId), stop);
        if (!result.Connected)
        {
            _log?.LogWarning("MQTT: connection to {Broker} refused: {Error}", s.Broker, result.Error);
            _state.SetDisconnected("Eroare: " + result.Error, result.Error);
            return (result.Error ?? "refuzat", false);
        }
        _transport = t;
        _state.RequestRepublish = ct => RequestRepublishAsync(t, s.DiscoveryPrefix, ct);
        _state.SetConnected(s.Broker, _clock());
        _log?.LogInformation("MQTT: connected to {Broker}, discovery prefix {Prefix}", s.Broker, s.DiscoveryPrefix);
        try
        {
            // Re-subscribe after every connection: the plain client keeps nothing.
            await t.SubscribeAsync(new[] { s.DiscoveryPrefix + "/#" }.Concat(_state.Registry.Topics()).ToList(), stop);
            var done = await Task.WhenAny(dropped.Task, settingsChanged, Task.Delay(Timeout.Infinite, stop));
            if (done == dropped.Task)
            {
                var reason = dropped.Task.Result ?? "conexiune închisă";
                _log?.LogWarning("MQTT: connection to {Broker} lost: {Reason}", s.Broker, reason);
                _state.SetDisconnected("Eroare: conexiune pierdută (" + reason + ")", reason);
                return (reason, true);
            }
            return (null, true);
        }
        finally
        {
            _transport = null;
            _state.RequestRepublish = null;
        }
    }

    private static async Task<string> RequestRepublishAsync(IMqttTransport t, string prefix, CancellationToken ct)
    {
        if (!t.IsConnected) return "Nu e conectat la broker.";
        await t.PublishAsync(prefix + "/status", "online", retain: false, ct);
        return $"Publicat „online” pe {prefix}/status: dispozitivele își retrimit descrierea.";
    }

    private async Task ConsumeAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var m in _channel.Reader.ReadAllAsync(stop))
            {
                try { await ProcessAsync(m, stop); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "MQTT: message on {Topic} could not be processed", m.Topic); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task MaintenanceAsync(CancellationToken stop)
    {
        var nextRollup = _clock();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(FlushEvery, stop);
                var now = _clock();
                try
                {
                    await _recorder.FlushAsync(now, stop);
                    if (now >= nextRollup)
                    {
                        nextRollup = now + RollupEvery;
                        await _recorder.RollupAsync(now, stop);
                        await _recorder.PurgeAsync(now, (_current ?? await _settings.GetAsync(stop)).RawRetentionDays, stop);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { _log?.LogWarning(ex, "MQTT: maintenance of the generic readings failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>One message (the consumer; public for the tests with a fake transport).</summary>
    public async Task ProcessAsync(MqttMessage m, CancellationToken ct)
    {
        var now = _clock();
        var s = _current ?? await _settings.GetAsync(ct);
        _current = s;
        if (m.Oversized || m.Payload.Length > MqttNetTransport.MaxPayloadBytes)
        {
            _state.Registry.AddProblem(m.Topic, $"mesaj prea mare (peste {MqttNetTransport.MaxPayloadBytes / 1024} KB): ignorat", now);
            return;
        }
        if (m.Topic.StartsWith(s.DiscoveryPrefix + "/", StringComparison.Ordinal))
        {
            var r = MqttDiscoveryParser.Parse(m.Topic, m.Payload, s.DiscoveryPrefix);
            if (r.Kind != MqttDiscoveryKind.NotDiscovery)
            {
                _state.MarkMessage(now, discovery: true);
                await ApplyDiscoveryAsync(r, s, now, ct);
                return;
            }
        }
        _state.MarkMessage(now, discovery: false);
        // R3 (round 2): a retained state is not a new reading (no row, the time of the last reading stays).
        await _recorder.OnValuesAsync(_state.Registry.ApplyMessage(m.Topic, m.Payload, now, m.Retain), ct);
    }

    private async Task ApplyDiscoveryAsync(MqttDiscoveryResult r, MqttRuntimeSettings s, DateTimeOffset now, CancellationToken ct)
    {
        var change = _state.Registry.ApplyDiscovery(r, now);
        if (change.NewTopics.Count > 0 && _transport is { IsConnected: true } t) await t.SubscribeAsync(change.NewTopics, ct);
        var removedByDevice = change.Removed.GroupBy(x => x.Device).ToDictionary(g => g.Key, g => (IReadOnlyCollection<string>)g.Select(x => x.UniqueId).ToList());
        foreach (var (device, uids) in removedByDevice.Where(kv => !change.ChangedDevices.Contains(kv.Key) || _state.Registry.Get(kv.Key, now) is null))
            await _recorder.MarkRemovedAsync(device, uids, ct);
        foreach (var ext in change.ChangedDevices)
        {
            if (_state.Registry.Get(ext, now) is not { } snap) continue;
            if (_recorder.ByExternal(IntegrationKeys.Mqtt, ext) is not null)
                await _recorder.SyncAsync(snap, removedByDevice.GetValueOrDefault(ext) ?? Array.Empty<string>(), now, ct);
            else if (s.AutoAdd)
                await AdoptAsync(snap, now, ct);
        }
        _state.Raise();
    }

    /// <summary>„Adaugă” on a discovered device: stores it with its entities and the values already received (retained states).</summary>
    public async Task<KasaMonitor.Core.Abstractions.GenericDevice> AddAsync(string externalId, CancellationToken ct)
    {
        var now = _clock();
        var snap = _state.Registry.Get(externalId, now) ?? throw new InvalidOperationException("Dispozitivul nu mai e anunțat pe broker.");
        return await AdoptAsync(snap, now, ct);
    }

    private async Task<KasaMonitor.Core.Abstractions.GenericDevice> AdoptAsync(MqttDeviceSnapshot snap, DateTimeOffset now, CancellationToken ct)
    {
        var device = await _recorder.AdoptAsync(snap, now, ct);
        var current = snap.Entities.Where(e => e.HasValue && e.Ts is not null)
            .Select(e => new MqttValueUpdate(snap.ExternalId, e.Spec, new MqttValue(e.Number, e.Text), e.Ts!.Value)).ToList();
        await _recorder.OnValuesAsync(current, ct);
        await _recorder.FlushAsync(now, ct);
        _state.Raise();
        return device;
    }
}
