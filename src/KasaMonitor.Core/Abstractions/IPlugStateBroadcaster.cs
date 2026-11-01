using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface IPlugStateBroadcaster
{
    void Publish(PlugLiveState state);

    /// <summary>Forget a plug (archived/withdrawn or unsupported): raises <see cref="StateRemoved"/> if it was known.</summary>
    void Remove(Guid plugId);

    IReadOnlyDictionary<Guid, PlugLiveState> Snapshot();
    event Action<PlugLiveState>? StateChanged;
    event Action<Guid>? StateRemoved;
}
