using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface IPlugStateBroadcaster
{
    void Publish(PlugLiveState state);
    IReadOnlyDictionary<Guid, PlugLiveState> Snapshot();
    event Action<PlugLiveState>? StateChanged;
}
