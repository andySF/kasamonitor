using KasaMonitor.Core.Mqtt;

namespace KasaMonitor.Workers.Mqtt;

/// <summary>
/// In-memory state of the MQTT integration (singleton, 0.11): the connection (connected, since when, the last error), the
/// messages seen, the registry of what the broker describes (<see cref="MqttRegistry"/>: the discovered devices, also the
/// ones not added, and their current values) and the /metrics series-limit warning. The UI and /metrics read it.
/// </summary>
public sealed class MqttState
{
    private readonly object _sync = new();

    public MqttRegistry Registry { get; } = new();

    public event Action? Changed;

    public bool Connected { get; private set; }
    /// <summary>Why nothing is read (not configured, switched off, the connection error), null while connected.</summary>
    public string? Status { get; private set; }
    public string? LastError { get; private set; }
    public string? Broker { get; private set; }
    public DateTimeOffset? ConnectedSinceUtc { get; private set; }
    public DateTimeOffset? LastMessageUtc { get; private set; }
    public DateTimeOffset? LastDiscoveryUtc { get; private set; }
    public long Messages { get; private set; }
    /// <summary>Set by <c>GenericMetrics</c> when the generic series exceed the limit; shown on the MQTT tab.</summary>
    public string? MetricsWarning { get; private set; }

    /// <summary>Publishes the birth message (<c>&lt;prefix&gt;/status</c> = <c>online</c>), set by the worker while connected.</summary>
    public Func<CancellationToken, Task<string>>? RequestRepublish { get; set; }

    public void SetConnected(string broker, DateTimeOffset now)
    {
        lock (_sync)
        {
            Connected = true;
            Broker = broker;
            ConnectedSinceUtc = now;
            // The last error stays visible on the MQTT tab after a reconnection.
            Status = null;
        }
        Raise();
    }

    /// <summary>Not connected: <paramref name="status"/> says why (shown as is), <paramref name="error"/> is kept as the last error.</summary>
    public void SetDisconnected(string status, string? error = null)
    {
        lock (_sync)
        {
            Connected = false;
            ConnectedSinceUtc = null;
            Status = status;
            if (error is not null) LastError = error;
        }
        Raise();
    }

    public void MarkMessage(DateTimeOffset now, bool discovery)
    {
        lock (_sync)
        {
            Messages++;
            LastMessageUtc = now;
            if (discovery) LastDiscoveryUtc = now;
        }
    }

    public void SetMetricsWarning(string? warning)
    {
        lock (_sync)
        {
            if (MetricsWarning == warning) return;
            MetricsWarning = warning;
        }
        Raise();
    }

    public void Raise()
    {
        try { Changed?.Invoke(); } catch { /* a UI handler must never break the worker */ }
    }
}

/// <summary>The devices the broker describes that were not added yet („Descoperite”, the MQTT „Adaugă” panel).</summary>
public static class MqttDiscovered
{
    public static IReadOnlyList<MqttDeviceSnapshot> NotAdded(MqttState state, GenericRecorder recorder, DateTimeOffset now) =>
        state.Registry.Devices(now).Where(d => recorder.ByExternal(KasaMonitor.Core.Devices.IntegrationKeys.Mqtt, d.ExternalId) is null).ToList();
}
