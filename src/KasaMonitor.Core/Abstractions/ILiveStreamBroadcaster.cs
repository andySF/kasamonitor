using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

/// <summary>Relay state of a plug as last reported by the device (Shelly <c>switch:N.output</c>).</summary>
public sealed record RelayState(Guid PlugId, bool On, DateTimeOffset AtUtc);

/// <summary>
/// Live fan-out of the live stream (main meter at 1 s, plugs 1–10 s) to the UI (page /contor, the "Casă" card): every
/// sample, relay changes seen on a Shelly WebSocket (<c>NotifyStatus</c>) or after a command, and house events (the
/// main meter jumped by more than the <c>LiveBurstTriggerW</c> setting: every plug goes back to 1 s for 60 s), and the
/// samples selected for storage (<see cref="SampleStored"/>: changed, or the 60 s witness; also sent by Prometheus
/// remote write). Keeps the
/// last 10 minutes per plug so a page opened later starts with a full chart. A failing subscriber never stops the others.
/// </summary>
public interface ILiveStreamBroadcaster
{
    void Publish(LiveSample sample);
    void PublishRelay(RelayState state);

    /// <summary>The main meter's power jumped (signalled by the live stream worker).</summary>
    void PublishHouseEvent(DateTimeOffset atUtc);

    /// <summary>A sample that passed the change filter (written as a raw row): the remote write sends exactly these.</summary>
    void PublishStored(LiveSample sample);

    LiveSample? Latest(Guid plugId);
    IReadOnlyList<LiveSample> Recent(Guid plugId);
    RelayState? Relay(Guid plugId);

    event Action<LiveSample>? SampleReceived;
    event Action<RelayState>? RelayChanged;
    event Action<DateTimeOffset>? HouseEvent;
    event Action<LiveSample>? SampleStored;
}
