using System.Buffers;
using System.Text;
using MQTTnet;
using MQTTnet.Protocol;

namespace KasaMonitor.Workers.Mqtt;

/// <summary>
/// One message received from the broker. <see cref="Oversized"/> (R7, round 2): the payload was larger than
/// <see cref="MqttNetTransport.MaxPayloadBytes"/> and was not decoded (<see cref="Payload"/> is empty).
/// </summary>
public sealed record MqttMessage(string Topic, string Payload, bool Retain, bool Oversized = false);

/// <summary>How to reach the broker. The password is read once at start-up from a file or the environment, never stored.</summary>
public sealed record MqttConnectOptions(string Host, int Port, string? User, string? Password, bool UseTls, string ClientId)
{
    /// <summary>Hides the password (the options may end up in a log line).</summary>
    public override string ToString() => $"{Host}:{Port} user={User ?? "-"} tls={UseTls} client={ClientId}";
}

/// <summary>The result of a connection: v5 of MQTTnet does not throw on a refused CONNACK (wrong password), so it is checked.</summary>
public sealed record MqttConnectResult(bool Connected, string? Error);

/// <summary>
/// The thin facade over the MQTT client (0.11): connect, subscribe, publish, a stream of messages and the disconnection
/// event. The logic (discovery, values, storage) never sees the library, so it is tested with a fake transport and the
/// library can be replaced. Not reconnecting by itself: <see cref="MqttDiscoveryWorker"/> owns the reconnection and the
/// re-subscription.
/// </summary>
public interface IMqttTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<MqttConnectResult> ConnectAsync(MqttConnectOptions options, CancellationToken ct);
    Task SubscribeAsync(IReadOnlyCollection<string> topicFilters, CancellationToken ct);
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    /// <summary>Raised for every message; the handler must not block (the worker hands it to a channel).</summary>
    event Func<MqttMessage, Task>? MessageReceived;
    /// <summary>Raised when an established connection drops (reason text, may be null).</summary>
    event Func<string?, Task>? Disconnected;
}

public interface IMqttTransportFactory
{
    IMqttTransport Create();
}

public sealed class MqttNetTransportFactory : IMqttTransportFactory
{
    public IMqttTransport Create() => new MqttNetTransport();
}

/// <summary>
/// <see cref="IMqttTransport"/> on MQTTnet 5.2 (MIT): <c>MqttClientFactory.CreateMqttClient()</c>, MQTT 3.1.1 (what every
/// broker and Zigbee2MQTT / ESPHome speak), clean session, QoS 0 subscriptions, the result code of <c>ConnectAsync</c> checked.
/// </summary>
public sealed class MqttNetTransport : IMqttTransport
{
    /// <summary>R7 (round 2): the largest payload decoded (a discovery or state message is a few KB; 64 KB is generous).</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    private readonly IMqttClient _client;
    private bool _wasConnected;

    public MqttNetTransport()
    {
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            var m = e.ApplicationMessage;
            if (m.Payload.Length > MaxPayloadBytes)
                return MessageReceived?.Invoke(new MqttMessage(m.Topic, "", m.Retain, Oversized: true)) ?? Task.CompletedTask;
            var payload = m.Payload.IsEmpty ? "" : Encoding.UTF8.GetString(m.Payload.ToArray());
            return MessageReceived?.Invoke(new MqttMessage(m.Topic, payload, m.Retain)) ?? Task.CompletedTask;
        };
        _client.DisconnectedAsync += e =>
        {
            if (!_wasConnected) return Task.CompletedTask;
            _wasConnected = false;
            var reason = e.ReasonString ?? e.Exception?.Message ?? e.Reason.ToString();
            return Disconnected?.Invoke(reason) ?? Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    public event Func<MqttMessage, Task>? MessageReceived;
    public event Func<string?, Task>? Disconnected;

    public async Task<MqttConnectResult> ConnectAsync(MqttConnectOptions o, CancellationToken ct)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(o.Host, o.Port)
            .WithClientId(o.ClientId)
            .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
            .WithCleanSession(true)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(10));
        if (!string.IsNullOrEmpty(o.User)) builder = builder.WithCredentials(o.User, o.Password ?? "");
        if (o.UseTls) builder = builder.WithTlsOptions(t => t.UseTls());
        try
        {
            var result = await _client.ConnectAsync(builder.Build(), ct);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
                return new MqttConnectResult(false, result.ResultCode switch
                {
                    MqttClientConnectResultCode.BadUserNameOrPassword or MqttClientConnectResultCode.NotAuthorized => "autentificare refuzată",
                    _ => "conectare refuzată: " + result.ResultCode,
                });
            _wasConnected = true;
            return new MqttConnectResult(true, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MqttConnectResult(false, ex.InnerException?.Message ?? ex.Message);
        }
    }

    public async Task SubscribeAsync(IReadOnlyCollection<string> topicFilters, CancellationToken ct)
    {
        // A broker accepts a limited number of filters per packet; 50 at a time.
        foreach (var chunk in topicFilters.Distinct(StringComparer.Ordinal).Chunk(50))
        {
            var b = new MqttClientSubscribeOptionsBuilder();
            foreach (var t in chunk) b = b.WithTopicFilter(t, MqttQualityOfServiceLevel.AtMostOnce);
            await _client.SubscribeAsync(b.Build(), ct);
        }
    }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) =>
        _client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).WithRetainFlag(retain).Build(), ct);

    public async Task DisconnectAsync(CancellationToken ct)
    {
        _wasConnected = false;
        if (_client.IsConnected) await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), ct);
    }

    public async ValueTask DisposeAsync()
    {
        try { await DisconnectAsync(CancellationToken.None); } catch { /* closing anyway */ }
        _client.Dispose();
    }
}
