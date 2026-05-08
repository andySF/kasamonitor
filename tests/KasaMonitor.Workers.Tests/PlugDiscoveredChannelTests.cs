using FluentAssertions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PlugDiscoveredChannelTests
{
    [Fact]
    public async Task Publish_ReadAll_ReceivesInOrder()
    {
        var ch = new PlugDiscoveredChannel();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) ch.Publish(id);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var seen = new List<Guid>();
        try
        {
            await foreach (var id in ch.ReadAllAsync(cts.Token))
            {
                seen.Add(id);
                if (seen.Count == ids.Length) break;
            }
        }
        catch (OperationCanceledException) { }

        seen.Should().Equal(ids);
    }
}
