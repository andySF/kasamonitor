using FluentAssertions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PollFailureTrackerTests
{
    [Fact]
    public void LogsFirstFailure_ThenEveryTenConsecutiveFailures()
    {
        var t = new PollFailureTracker();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");

        var logged = new List<int>();
        for (int i = 0; i < 25; i++)
        {
            var (log, count) = t.RecordFailure(id, now.AddSeconds(i * 10));
            if (log) logged.Add(count);
        }

        logged.Should().Equal(1, 11, 21);
    }

    [Fact]
    public void LogsAfterTenMinutes_EvenWithFewFailures()
    {
        var t = new PollFailureTracker();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-11-01T10:00:00Z");

        t.RecordFailure(id, now).ShouldLog.Should().BeTrue();
        t.RecordFailure(id, now.AddMinutes(5)).ShouldLog.Should().BeFalse();
        t.RecordFailure(id, now.AddMinutes(10)).ShouldLog.Should().BeTrue();
    }

    [Fact]
    public void Success_ReportsEndedStreak_AndResets()
    {
        var t = new PollFailureTracker();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        t.RecordFailure(id, now);
        t.RecordFailure(id, now);

        t.RecordSuccess(id).Should().Be(2);
        t.RecordSuccess(id).Should().Be(0);
        t.RecordFailure(id, now).ShouldLog.Should().BeTrue();
    }
}
