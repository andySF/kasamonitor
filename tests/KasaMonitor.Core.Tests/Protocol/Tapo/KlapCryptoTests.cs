using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Tapo;

/// <summary>
/// Vectors produced with a Python re-implementation of python-kasa klaptransport.py (hashlib + cryptography):
/// user "user@example.com", password "secret-pass", local_seed = bytes(0..15), remote_seed = bytes(16..31),
/// request '{"method":"get_device_info"}'.
/// </summary>
public class KlapCryptoTests
{
    private static readonly TapoCredentials Creds = new("user@example.com", "secret-pass");
    private static readonly byte[] LocalSeed = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] RemoteSeed = Enumerable.Range(16, 16).Select(i => (byte)i).ToArray();

    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void AuthHash_V2_IsSha256OfSha1s()
    {
        Hex(KlapCrypto.AuthHash(KlapAuthVersion.V2, Creds))
            .Should().Be("5d157bec795816a01542797ddcd01102fa166ae69f53e6d9b2598c6cd66afdec");
    }

    [Fact]
    public void AuthHash_V1_IsMd5OfMd5s()
    {
        Hex(KlapCrypto.AuthHash(KlapAuthVersion.V1, Creds)).Should().Be("7f040d329bb745d0e2c229ca191a308f");
    }

    [Fact]
    public void HandshakeHashes_V2()
    {
        var auth = KlapCrypto.AuthHash(KlapAuthVersion.V2, Creds);
        Hex(KlapCrypto.Handshake1ServerHash(KlapAuthVersion.V2, LocalSeed, RemoteSeed, auth))
            .Should().Be("a25b94a28e6c2b58dbafe077df6f4d21d540e786238ffa5314f4be74ecd16ced");
        Hex(KlapCrypto.Handshake2Payload(KlapAuthVersion.V2, LocalSeed, RemoteSeed, auth))
            .Should().Be("794050e694d4adff33a0b45ea2b9d843181443d2a4e5adc19963ac2150e6ae23");
    }

    [Fact]
    public void HandshakeHashes_V1()
    {
        var auth = KlapCrypto.AuthHash(KlapAuthVersion.V1, Creds);
        Hex(KlapCrypto.Handshake1ServerHash(KlapAuthVersion.V1, LocalSeed, RemoteSeed, auth))
            .Should().Be("1fad6bf9280376448a9448fea68d9da1b0481589c2aa36e9234478ea35aee076");
        Hex(KlapCrypto.Handshake2Payload(KlapAuthVersion.V1, LocalSeed, RemoteSeed, auth))
            .Should().Be("0c360add36aead044dc14e1adc99458a3b2d375101d7ee291797f02ac934c0fd");
    }

    [Fact]
    public void Session_DerivesKeyIvSeqSig()
    {
        var s = new KlapSession(LocalSeed, RemoteSeed, KlapCrypto.AuthHash(KlapAuthVersion.V2, Creds));

        Hex(s.Key).Should().Be("c3e7d64c983321c6cbf7163beb9fee44");
        Hex(s.IvPrefix).Should().Be("25150be6b6ee5cc2e1f2c4b1");
        s.Sequence.Should().Be(-1979115993);
        Hex(s.Signature).Should().Be("beefdefbaa78b6384c7de44988a7e31cb5ce983be83a0bc7de41b9c1");
    }

    [Fact]
    public void Session_EncryptsRequestExactlyLikeReference()
    {
        var s = new KlapSession(LocalSeed, RemoteSeed, KlapCrypto.AuthHash(KlapAuthVersion.V2, Creds));

        var (body, seq) = s.Encrypt("{\"method\":\"get_device_info\"}");

        seq.Should().Be(-1979115992);
        Hex(body).Should().Be(
            "2563d64e461bc1940a133f336dbdac33c81e04ac25a014980e1f15a707397c0e" +
            "1e082a455bd3bf1da35ce8dd387da507e3fff5aa146cfd34a1154fb59fecf375");
    }

    [Fact]
    public void Session_RoundTripsDeviceAnswer()
    {
        var auth = KlapCrypto.AuthHash(KlapAuthVersion.V2, Creds);
        var client = new KlapSession(LocalSeed, RemoteSeed, auth);
        var device = new KlapSession(LocalSeed, RemoteSeed, auth);

        var (body, seq) = client.Encrypt("{\"method\":\"get_energy_usage\"}");
        device.DecryptRequest(body, seq).Should().Be("{\"method\":\"get_energy_usage\"}");

        var answer = device.EncryptResponse("{\"error_code\":0,\"result\":{}}", seq);
        client.Decrypt(answer, seq).Should().Be("{\"error_code\":0,\"result\":{}}");
    }
}
