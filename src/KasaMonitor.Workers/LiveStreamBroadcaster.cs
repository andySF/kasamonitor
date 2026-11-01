using System.Collections.Concurrent;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KasaMonitor.Workers;

/// <summary>In-memory fan-out of the live stream; keeps the last <see cref="Window"/> per plug (at most 600 points).</summary>
public sealed class LiveStreamBroadcaster : ILiveStreamBroadcaster
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MaxPoints = 600;

    private readonly ConcurrentDictionary<Guid, SlidingWindow<LiveSample>> _recent = new();
    private readonly ConcurrentDictionary<Guid, LiveSample> _latest = new();
    private readonly ConcurrentDictionary<Guid, RelayState> _relays = new();
    private readonly ILogger _log;

    public LiveStreamBroadcaster(ILogger<LiveStreamBroadcaster>? log = null) => _log = (ILogger?)log ?? NullLogger.Instance;

    public event Action<LiveSample>? SampleReceived;
    public event Action<RelayState>? RelayChanged;
    public event Action<DateTimeOffset>? HouseEvent;
    public event Action<LiveSample>? SampleStored;

    public void Publish(LiveSample sample)
    {
        _recent.GetOrAdd(sample.PlugId, _ => new SlidingWindow<LiveSample>(Window, MaxPoints)).Add(sample.TsUtc, sample);
        _latest[sample.PlugId] = sample;
        Raise(SampleReceived, sample);
    }

    public void PublishRelay(RelayState state)
    {
        // Keep the newest report: a 10 s poll answered before a notification must not overwrite it.
        var stored = _relays.AddOrUpdate(state.PlugId, state, (_, old) => state.AtUtc >= old.AtUtc ? state : old);
        if (ReferenceEquals(stored, state)) Raise(RelayChanged, state);
    }

    public void PublishHouseEvent(DateTimeOffset atUtc) => Raise(HouseEvent, atUtc);

    public void PublishStored(LiveSample sample) => Raise(SampleStored, sample);

    public LiveSample? Latest(Guid plugId) => _latest.TryGetValue(plugId, out var s) ? s : null;

    public IReadOnlyList<LiveSample> Recent(Guid plugId) =>
        _recent.TryGetValue(plugId, out var w) ? w.Snapshot().Select(x => x.Value).ToList() : Array.Empty<LiveSample>();

    public RelayState? Relay(Guid plugId) => _relays.TryGetValue(plugId, out var r) ? r : null;

    /// <summary>Each subscriber on its own: an exception in one UI circuit must not stop the others or the worker.</summary>
    private void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try { h(value); }
            catch (Exception ex) { _log.LogDebug("Live stream subscriber failed: {Reason}", ex.Message); }
        }
    }
}
