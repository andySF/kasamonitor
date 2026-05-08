using System.Buffers.Binary;
using System.Text;

namespace KasaMonitor.Core.Protocol;

/// <summary>
/// Byte-level framing for Kasa protocol. TCP uses 4-byte big-endian length prefix + XOR JSON.
/// UDP uses raw XOR JSON. No socket I/O here — see <see cref="KasaClient"/>.
/// </summary>
public static class KasaWireProtocol
{
    private const int MaxBodyBytes = 1 << 20; // 1 MiB safety cap; real payloads are < 8 KiB.

    public static byte[] EncodeTcp(string json)
    {
        var body = KasaCodec.Encrypt(Encoding.UTF8.GetBytes(json));
        var output = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(0, 4), body.Length);
        body.CopyTo(output, 4);
        return output;
    }

    public static string DecodeTcp(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < 4)
            throw new InvalidDataException("Frame too short to contain length header.");
        var declared = BinaryPrimitives.ReadInt32BigEndian(framed[..4]);
        if (declared < 0 || declared > MaxBodyBytes)
            throw new InvalidDataException($"Frame length {declared} too large.");
        if (framed.Length - 4 < declared)
            throw new InvalidDataException(
                $"Frame body truncated: declared {declared} bytes, got {framed.Length - 4}.");
        return Encoding.UTF8.GetString(KasaCodec.Decrypt(framed.Slice(4, declared)));
    }

    public static byte[] EncodeUdp(string json) =>
        KasaCodec.Encrypt(Encoding.UTF8.GetBytes(json));

    public static string DecodeUdp(ReadOnlySpan<byte> ciphertext) =>
        Encoding.UTF8.GetString(KasaCodec.Decrypt(ciphertext));
}
