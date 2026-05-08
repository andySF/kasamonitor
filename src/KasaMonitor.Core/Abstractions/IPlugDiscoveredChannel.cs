namespace KasaMonitor.Core.Abstractions;

public interface IPlugDiscoveredChannel
{
    void Publish(Guid plugId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}
