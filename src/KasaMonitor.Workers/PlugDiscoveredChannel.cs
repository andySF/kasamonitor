using KasaMonitor.Core.Abstractions;
using System.Threading.Channels;

namespace KasaMonitor.Workers;

public sealed class PlugDiscoveredChannel : IPlugDiscoveredChannel
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Publish(Guid plugId) => _channel.Writer.TryWrite(plugId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
