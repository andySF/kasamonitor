using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>One Shelly instance announced over mDNS (<c>_shelly._tcp.local</c>), with its TXT keys (gen, app, ver, id).</summary>
public sealed record ShellyMdnsAnswer(string Instance, IPAddress Address, int Port, IReadOnlyDictionary<string, string> Txt)
{
    public int? Gen => Txt.TryGetValue("gen", out var g) && int.TryParse(g, out var n) ? n : null;
    public string? App => Txt.GetValueOrDefault("app");
    public string? Id => Txt.GetValueOrDefault("id");
}

/// <summary>
/// Minimal mDNS (RFC 6762) for Shelly Gen2+ discovery: a PTR question for <c>_shelly._tcp.local</c> sent to
/// 224.0.0.251:5353 from an ephemeral port ("legacy unicast": responders answer by unicast to that port, so we do not
/// need to bind 5353, which an OS responder may hold), and a parser for the PTR/SRV/TXT/A records of the answers.
/// </summary>
public static class ShellyMdns
{
    public const string ServiceName = "_shelly._tcp.local";
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    public const int Port = 5353;

    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

    /// <summary>Standard query, one question: PTR <paramref name="service"/>, class IN with the unicast-response bit.</summary>
    public static byte[] BuildQuery(string service = ServiceName)
    {
        var name = EncodeName(service);
        var buf = new byte[12 + name.Length + 4];
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(4), 1);   // QDCOUNT
        name.CopyTo(buf.AsSpan(12));
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(12 + name.Length), TypePtr);
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(12 + name.Length + 2), 0x8001);   // QU + IN
        return buf;
    }

    /// <summary>
    /// Instances of <paramref name="service"/> in one response packet. The address is the A record of the SRV target
    /// when present, otherwise the packet's source (the responder itself). Malformed packets yield nothing.
    /// </summary>
    public static IReadOnlyList<ShellyMdnsAnswer> ParseResponse(byte[] packet, IPAddress source, string service = ServiceName)
    {
        try { return ParseCore(packet, source, service); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or FormatException)
        {
            return Array.Empty<ShellyMdnsAnswer>();
        }
    }

    private static IReadOnlyList<ShellyMdnsAnswer> ParseCore(byte[] p, IPAddress source, string service)
    {
        if (p.Length < 12) return Array.Empty<ShellyMdnsAnswer>();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
        if ((flags & 0x8000) == 0) return Array.Empty<ShellyMdnsAnswer>();   // a query, not a response
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
        var rrCount = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8))
                      + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            ReadName(p, ref pos);
            pos += 4;
        }

        var instances = new List<string>();
        var srv = new Dictionary<string, (string Target, int Port)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var a = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < rrCount && pos < p.Length; i++)
        {
            var name = ReadName(p, ref pos);
            var type = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 8));
            var rd = pos + 10;
            pos = rd + rdLength;
            if (pos > p.Length) break;
            switch (type)
            {
                case TypePtr when name.Equals(service, StringComparison.OrdinalIgnoreCase):
                {
                    var at = rd;
                    var instance = ReadName(p, ref at);
                    if (!instances.Contains(instance, StringComparer.OrdinalIgnoreCase)) instances.Add(instance);
                    break;
                }
                case TypeSrv when rdLength >= 7:
                {
                    var port = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(rd + 4));
                    var at = rd + 6;
                    srv[name] = (ReadName(p, ref at), port);
                    break;
                }
                case TypeTxt:
                    txt[name] = ParseTxt(p.AsSpan(rd, rdLength));
                    break;
                case TypeA when rdLength == 4:
                    a[name] = new IPAddress(p.AsSpan(rd, 4));
                    break;
            }
        }

        // Some responders send only SRV/TXT for the instance (no PTR) in the same packet: accept those too.
        foreach (var name in srv.Keys.Concat(txt.Keys))
            if (name.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) && !instances.Contains(name, StringComparer.OrdinalIgnoreCase))
                instances.Add(name);

        return instances.Select(inst =>
        {
            var hasSrv = srv.TryGetValue(inst, out var s);
            var address = hasSrv && a.TryGetValue(s.Target, out var ip) ? ip : source;
            return new ShellyMdnsAnswer(
                inst.EndsWith("." + service, StringComparison.OrdinalIgnoreCase) ? inst[..^(service.Length + 1)] : inst,
                address,
                hasSrv ? s.Port : 80,
                txt.TryGetValue(inst, out var t) ? t : new Dictionary<string, string>());
        }).ToList();
    }

    /// <summary>TXT rdata: length-prefixed "key=value" strings.</summary>
    public static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> rdata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < rdata.Length)
        {
            var len = rdata[i++];
            if (i + len > rdata.Length) break;
            var entry = Encoding.UTF8.GetString(rdata.Slice(i, len));
            i += len;
            if (entry.Length == 0) continue;
            var eq = entry.IndexOf('=');
            if (eq < 0) result[entry] = "";
            else result[entry[..eq]] = entry[(eq + 1)..];
        }
        return result;
    }

    public static byte[] EncodeName(string name)
    {
        var ms = new List<byte>();
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new FormatException($"Invalid DNS label in '{name}'.");
            ms.Add((byte)bytes.Length);
            ms.AddRange(bytes);
        }
        ms.Add(0);
        return ms.ToArray();
    }

    /// <summary>Reads a (possibly compressed) domain name at <paramref name="pos"/> and advances past it.</summary>
    private static string ReadName(byte[] p, ref int pos)
    {
        var labels = new List<string>();
        var at = pos;
        var jumped = false;
        var hops = 0;
        while (true)
        {
            var len = p[at];
            if (len == 0)
            {
                at++;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                var target = ((len & 0x3F) << 8) | p[at + 1];
                if (!jumped) pos = at + 2;
                jumped = true;
                if (++hops > 32 || target >= p.Length) throw new FormatException("DNS name compression loop.");
                at = target;
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, at + 1, len));
            at += 1 + len;
        }
        if (!jumped) pos = at;
        return string.Join('.', labels);
    }
}
