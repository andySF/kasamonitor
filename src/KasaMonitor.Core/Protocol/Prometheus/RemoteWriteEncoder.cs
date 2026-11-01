using System.Buffers.Binary;
using System.Text;

namespace KasaMonitor.Core.Protocol.Prometheus;

/// <summary>One series of a remote write request: labels (including <c>__name__</c>) and samples in time order.</summary>
public sealed record RemoteSeries(IReadOnlyList<KeyValuePair<string, string>> Labels, IReadOnlyList<RemoteSample> Samples);

public readonly record struct RemoteSample(double Value, long TimestampMs);

/// <summary>
/// Prometheus remote write 1.0 payload, written by hand (no protobuf runtime): <c>prometheus.WriteRequest</c>
/// { repeated TimeSeries timeseries = 1 }, TimeSeries { repeated Label labels = 1; repeated Sample samples = 2 },
/// Label { string name = 1; string value = 2 }, Sample { double value = 1; int64 timestamp = 2 } (ms). Labels are
/// sorted by name (the receiver requires it; <c>__name__</c> sorts first). The body is then snappy block-compressed.
/// </summary>
public static class RemoteWriteEncoder
{
    private const int WireVarint = 0, WireFixed64 = 1, WireLengthDelimited = 2;

    public static byte[] Encode(IReadOnlyList<RemoteSeries> series)
    {
        using var ms = new MemoryStream();
        foreach (var s in series) WriteMessage(ms, 1, TimeSeries(s));
        return ms.ToArray();
    }

    private static byte[] TimeSeries(RemoteSeries s)
    {
        using var ms = new MemoryStream();
        foreach (var (name, value) in s.Labels.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            using var label = new MemoryStream();
            WriteString(label, 1, name);
            WriteString(label, 2, value);
            WriteMessage(ms, 1, label.ToArray());
        }
        Span<byte> d = stackalloc byte[8];
        foreach (var sample in s.Samples)
        {
            using var sm = new MemoryStream();
            WriteTag(sm, 1, WireFixed64);
            BinaryPrimitives.WriteDoubleLittleEndian(d, sample.Value);
            sm.Write(d);
            WriteTag(sm, 2, WireVarint);
            WriteVarint(sm, unchecked((ulong)sample.TimestampMs));
            WriteMessage(ms, 2, sm.ToArray());
        }
        return ms.ToArray();
    }

    private static void WriteString(Stream s, int field, string value) => WriteMessage(s, field, Encoding.UTF8.GetBytes(value));

    private static void WriteMessage(Stream s, int field, byte[] payload)
    {
        WriteTag(s, field, WireLengthDelimited);
        WriteVarint(s, (ulong)payload.Length);
        s.Write(payload);
    }

    private static void WriteTag(Stream s, int field, int wire) => WriteVarint(s, (ulong)((field << 3) | wire));

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }
        s.WriteByte((byte)v);
    }
}
