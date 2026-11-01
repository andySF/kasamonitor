using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>
/// Byte-level encoding of the TP-Link Device Protocol (TDP) discovery used by Tapo and newer Kasa devices
/// on UDP 20002. Mirrors python-kasa <c>discover.py</c> (<c>_AesDiscoveryQuery.generate_query</c>):
/// header <c>&gt;BBHHBBII</c> = version 2, msg_type 0, op_code 1 (probe), payload size, flags, padding,
/// device serial, CRC; the CRC field is first filled with 0x5A6B7C8D, then replaced with CRC32 of the whole datagram.
/// </summary>
public static class TapoDiscoveryProtocol
{
    public const int Port = 20002;
    public const int HeaderLength = 16;
    private const uint InitialCrc = 0x5A6B7C8D;

    public static byte[] BuildProbe(ReadOnlySpan<byte> payload, byte flags, uint deviceSerial)
    {
        var buf = new byte[HeaderLength + payload.Length];
        buf[0] = 2;  // TDP version
        buf[1] = 0;  // msg type
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), 1);                      // op code: probe
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), (ushort)payload.Length); // payload size
        buf[6] = flags;
        buf[7] = 0;  // padding
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), deviceSerial);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), InitialCrc);
        payload.CopyTo(buf.AsSpan(HeaderLength));
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), Crc32.Compute(buf));
        return buf;
    }

    /// <summary>The 16-byte probe used by older python-kasa (hex 020000010000000000000000463cb5d3).</summary>
    public static byte[] BuildPlainProbe() => BuildProbe(ReadOnlySpan<byte>.Empty, flags: 0, deviceSerial: 0);

    /// <summary>The probe current python-kasa sends: JSON payload with an RSA public key, flags 17, random serial.</summary>
    public static byte[] BuildRsaProbe(string publicKeyPem)
    {
        var payload = JsonSerializer.Serialize(new { @params = new { rsa_key = publicKeyPem } });
        return BuildProbe(Encoding.UTF8.GetBytes(payload), flags: 17,
            deviceSerial: BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)));
    }

    /// <summary>Parses a discovery answer (16-byte header + JSON). Returns null for anything that is not a usable answer.</summary>
    public static TapoDiscoveryResponse? ParseResponse(ReadOnlySpan<byte> datagram, IPAddress remote)
    {
        if (datagram.Length <= HeaderLength) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(datagram[HeaderLength..].ToArray()); }
        catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("error_code", out var ec) && ec.ValueKind == JsonValueKind.Number && ec.GetInt32() != 0)
                return null;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) return null;

            var mac = GetString(result, "mac");
            if (string.IsNullOrEmpty(mac)) return null;

            var ip = IPAddress.TryParse(GetString(result, "ip"), out var parsedIp) ? parsedIp : remote;
            string encrypt = "";
            int port = 80;
            int? lv = null;
            bool https = false;
            if (result.TryGetProperty("mgt_encrypt_schm", out var schm) && schm.ValueKind == JsonValueKind.Object)
            {
                encrypt = GetString(schm, "encrypt_type") ?? "";
                if (schm.TryGetProperty("http_port", out var hp) && hp.ValueKind == JsonValueKind.Number) port = hp.GetInt32();
                if (schm.TryGetProperty("lv", out var lvEl) && lvEl.ValueKind == JsonValueKind.Number) lv = lvEl.GetInt32();
                if (schm.TryGetProperty("is_support_https", out var hs) && hs.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    https = hs.GetBoolean();
            }

            return new TapoDiscoveryResponse(
                ip,
                MacFormat.Normalize(mac),
                GetString(result, "device_type") ?? "",
                GetString(result, "device_model") ?? "",
                encrypt,
                port,
                lv,
                https);
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}

/// <summary>CRC-32 (IEEE 802.3, same as zlib.crc32).</summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }
}
