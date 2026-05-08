using FluentAssertions;
using KasaMonitor.Core.Models;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PlugStateBroadcasterTests
{
    private static PlugLiveState State(Guid id, string mac = "aa", bool online = true) =>
        new(PlugId: id, Mac: mac, Alias: "x", IsOnline: online,
            LastReading: null, LastSampleUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void Snapshot_AfterPublish_ReflectsLatest()
    {
        var b = new PlugStateBroadcaster();
        var id = Guid.NewGuid();
        b.Publish(State(id, online: false));
        b.Publish(State(id, online: true));

        b.Snapshot()[id].IsOnline.Should().BeTrue();
    }

    [Fact]
    public void StateChanged_FiresOnPublish()
    {
        var b = new PlugStateBroadcaster();
        var fired = new List<PlugLiveState>();
        b.StateChanged += s => fired.Add(s);

        var id = Guid.NewGuid();
        b.Publish(State(id));

        fired.Should().HaveCount(1).And.ContainSingle(s => s.PlugId == id);
    }

    [Fact]
    public void Publish_FromMultipleThreads_DoesNotCorrupt()
    {
        var b = new PlugStateBroadcaster();
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();

        Parallel.ForEach(ids, id => b.Publish(State(id)));

        b.Snapshot().Should().HaveCount(50);
    }
}
