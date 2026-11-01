using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using System.Collections.Concurrent;

namespace KasaMonitor.Workers;

public sealed class PlugStateBroadcaster : IPlugStateBroadcaster
{
    private readonly ConcurrentDictionary<Guid, PlugLiveState> _state = new();

    public event Action<PlugLiveState>? StateChanged;
    public event Action<Guid>? StateRemoved;

    public void Publish(PlugLiveState state)
    {
        _state[state.PlugId] = state;
        StateChanged?.Invoke(state);
    }

    public void Remove(Guid plugId)
    {
        if (_state.TryRemove(plugId, out _))
            StateRemoved?.Invoke(plugId);
    }

    public IReadOnlyDictionary<Guid, PlugLiveState> Snapshot() =>
        new Dictionary<Guid, PlugLiveState>(_state);
}
