using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>
/// KLAP auth-hash variants, as in python-kasa <c>transports/klaptransport.py</c>:
/// <list type="bullet">
/// <item>V1 (<c>KlapTransport</c>, Kasa IOT devices): auth = md5(md5(user) + md5(pass)); hs1 = sha256(local + auth); hs2 = sha256(remote + auth).</item>
/// <item>V2 (<c>KlapTransportV2</c>, Tapo SMART devices, "SMART.KLAP"): auth = sha256(sha1(user) + sha1(pass));
/// hs1 = sha256(local + remote + auth); hs2 = sha256(remote + local + auth).</item>
/// </list>
/// All hashes are raw digests (bytes), not hex strings.
/// </summary>
public enum KlapAuthVersion
{
    V1 = 1,
    V2 = 2,
}

public static class KlapCrypto
{
    public static byte[] AuthHash(KlapAuthVersion version, TapoCredentials creds)
    {
        var user = Encoding.UTF8.GetBytes(creds.Username);
        var pass = Encoding.UTF8.GetBytes(creds.Password);
        return version switch
        {
            KlapAuthVersion.V1 => MD5.HashData(Concat(MD5.HashData(user), MD5.HashData(pass))),
            _ => SHA256.HashData(Concat(SHA1.HashData(user), SHA1.HashData(pass))),
        };
    }

    /// <summary>The hash the device returns after its remote seed in the handshake1 answer.</summary>
    public static byte[] Handshake1ServerHash(KlapAuthVersion version, byte[] localSeed, byte[] remoteSeed, byte[] authHash) =>
        version == KlapAuthVersion.V1
            ? SHA256.HashData(Concat(localSeed, authHash))
            : SHA256.HashData(Concat(localSeed, remoteSeed, authHash));

    /// <summary>The payload the client posts to /app/handshake2.</summary>
    public static byte[] Handshake2Payload(KlapAuthVersion version, byte[] localSeed, byte[] remoteSeed, byte[] authHash) =>
        version == KlapAuthVersion.V1
            ? SHA256.HashData(Concat(remoteSeed, authHash))
            : SHA256.HashData(Concat(remoteSeed, localSeed, authHash));

    internal static byte[] Concat(params byte[][] parts)
    {
        var len = parts.Sum(p => p.Length);
        var buf = new byte[len];
        var off = 0;
        foreach (var p in parts) { p.CopyTo(buf, off); off += p.Length; }
        return buf;
    }
}

/// <summary>
/// Symmetric session after a successful KLAP handshake (python-kasa <c>KlapEncryptionSession</c>):
/// key = sha256("lsk" + local + remote + auth)[:16];
/// full = sha256("iv" + local + remote + auth), iv = full[:12], seq = signed big-endian int32 of full[28:32];
/// sig = sha256("ldk" + local + remote + auth)[:28].
/// Each request increments seq; AES-128-CBC with IV = iv + seq(be32), PKCS7;
/// body = sha256(sig + seq(be32) + ciphertext) + ciphertext; the answer is decrypted with the same seq (first 32 bytes skipped).
/// </summary>
public sealed class KlapSession
{
    private readonly byte[] _key;
    private readonly byte[] _ivPrefix;
    private readonly byte[] _sig;

    public int Sequence { get; private set; }

    public KlapSession(byte[] localSeed, byte[] remoteSeed, byte[] authHash)
    {
        _key = SHA256.HashData(KlapCrypto.Concat("lsk"u8.ToArray(), localSeed, remoteSeed, authHash))[..16];
        var fullIv = SHA256.HashData(KlapCrypto.Concat("iv"u8.ToArray(), localSeed, remoteSeed, authHash));
        _ivPrefix = fullIv[..12];
        Sequence = BinaryPrimitives.ReadInt32BigEndian(fullIv.AsSpan(fullIv.Length - 4));
        _sig = SHA256.HashData(KlapCrypto.Concat("ldk"u8.ToArray(), localSeed, remoteSeed, authHash))[..28];
    }

    public ReadOnlySpan<byte> Key => _key;
    public ReadOnlySpan<byte> IvPrefix => _ivPrefix;
    public ReadOnlySpan<byte> Signature => _sig;

    /// <summary>Increments the sequence and returns (request body, sequence used).</summary>
    public (byte[] Body, int Sequence) Encrypt(string plaintext)
    {
        Sequence = unchecked(Sequence + 1);
        var cipher = Crypt(Encoding.UTF8.GetBytes(plaintext), Sequence, encrypt: true);
        var seqBytes = SeqBytes(Sequence);
        var signature = SHA256.HashData(KlapCrypto.Concat(_sig, seqBytes, cipher));
        return (KlapCrypto.Concat(signature, cipher), Sequence);
    }

    /// <summary>Decrypts a device answer produced for <paramref name="sequence"/>.</summary>
    public string Decrypt(ReadOnlySpan<byte> body, int sequence)
    {
        if (body.Length < 32 + 16) throw new KasaProtocolException("KLAP response too short.");
        var plain = Crypt(body[32..].ToArray(), sequence, encrypt: false);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Device side of <see cref="Encrypt"/>: used by tests that simulate a plug.</summary>
    public byte[] EncryptResponse(string plaintext, int sequence)
    {
        var cipher = Crypt(Encoding.UTF8.GetBytes(plaintext), sequence, encrypt: true);
        var signature = SHA256.HashData(KlapCrypto.Concat(_sig, SeqBytes(sequence), cipher));
        return KlapCrypto.Concat(signature, cipher);
    }

    /// <summary>Device side: verify signature and decrypt a client request.</summary>
    public string DecryptRequest(ReadOnlySpan<byte> body, int sequence)
    {
        var cipher = body[32..].ToArray();
        var expected = SHA256.HashData(KlapCrypto.Concat(_sig, SeqBytes(sequence), cipher));
        if (!expected.AsSpan().SequenceEqual(body[..32])) throw new KasaProtocolException("KLAP request signature mismatch.");
        return Encoding.UTF8.GetString(Crypt(cipher, sequence, encrypt: false));
    }

    private byte[] Crypt(byte[] data, int sequence, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        var iv = KlapCrypto.Concat(_ivPrefix, SeqBytes(sequence));
        return encrypt
            ? aes.EncryptCbc(data, iv, PaddingMode.PKCS7)
            : aes.DecryptCbc(data, iv, PaddingMode.PKCS7);
    }

    private static byte[] SeqBytes(int seq)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, seq);
        return b;
    }
}
