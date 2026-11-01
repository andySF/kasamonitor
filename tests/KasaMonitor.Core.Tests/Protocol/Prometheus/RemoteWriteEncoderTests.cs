using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Prometheus;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Prometheus;

public class RemoteWriteEncoderTests
{
    [Fact]
    public void KnownWriteRequest_EncodesToTheExpectedBytes()
    {
        // WriteRequest { timeseries { labels {__name__="m"} labels {a="b"} samples {value=1.0 timestamp=1000} } }
        var series = new[]
        {
            new RemoteSeries(new KeyValuePair<string, string>[] { new("a", "b"), new("__name__", "m") },   // sorted by the encoder
                new[] { new RemoteSample(1.0, 1000) }),
        };

        var hex = Convert.ToHexString(RemoteWriteEncoder.Encode(series));

        hex.Should().Be(
            "0A25" +                                     // field 1 (timeseries), 37 bytes
            "0A0D" + "0A08" + "5F5F6E616D655F5F" + "1201" + "6D" +   // label __name__ = m
            "0A06" + "0A01" + "61" + "1201" + "62" +                 // label a = b
            "120C" + "09" + "000000000000F03F" + "10" + "E807");     // sample: double 1.0, varint 1000
    }

    [Fact]
    public void LabelsAreSorted_AndUtf8_AndSamplesKeepTheirOrder()
    {
        var series = new[]
        {
            new RemoteSeries(new KeyValuePair<string, string>[] { new("role", "main"), new("alias", "Casă"), new("__name__", "kasa_live_power_w") },
                new[] { new RemoteSample(450.5, 1793551260000), new RemoteSample(470, 1793551261000) }),
        };
        var bytes = RemoteWriteEncoder.Encode(series);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        text.IndexOf("__name__", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("alias", StringComparison.Ordinal));
        text.IndexOf("alias", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("role", StringComparison.Ordinal));
        text.Should().Contain("Casă");
        PlugMetricLabels.Names.Should().Equal("alias", "mac", "model", "protocol", "role");
    }
}
