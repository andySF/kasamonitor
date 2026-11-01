using FluentAssertions;
using KasaMonitor.Workers;
using Prometheus;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class RemoteWriteMetricsTests
{
    [Fact]
    public async Task OwnMetrics_FollowTheWriterStatus()
    {
        RemoteWriteMetrics.Update(new RemoteWriteStatus(true, "http://p:9090/api/v1/write", DateTimeOffset.FromUnixTimeSeconds(1793551260), 1234, 5, 2, 0, null));

        using var ms = new MemoryStream();
        await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(ms);
        var text = System.Text.Encoding.UTF8.GetString(ms.ToArray());

        text.Should().Contain("kasa_remote_write_samples_total 1234");
        text.Should().Contain("kasa_remote_write_failures_total 2");
        text.Should().Contain("kasa_remote_write_dropped_samples_total 5");
        text.Should().Contain("kasa_remote_write_last_success_timestamp_seconds 1793551260");
    }
}
