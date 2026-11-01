using FluentAssertions;
using KasaMonitor.Core.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class DigestAuthTests
{
    // RFC 7616 §3.9.1 example: user "Mufasa", password "Circle of Life", GET /dir/index.html.
    private const string Realm = "http-auth@example.org";
    private const string Nonce = "7ypf/xlj9XXwfDPEoM4URrv/xwf94BcCAzFZH4GiTo0v";
    private const string Cnonce = "f2/wE4q74E6zIJEtWaHKaf5wv/H5QzzpXusqGemxURZJ";

    [Theory]
    [InlineData("SHA-256", "753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1")]
    [InlineData("MD5", "8ca523f5e9506fed4657c9700eebdbec")]
    public void Rfc7616_KnownVector(string algorithm, string expected) =>
        DigestAuth.ComputeResponse(algorithm, "Mufasa", Realm, "Circle of Life", "GET", "/dir/index.html", Nonce, "auth", "00000001", Cnonce)
            .Should().Be(expected);

    [Fact]
    public void ParseChallenge_ShellyHeader()
    {
        var c = DigestAuth.ParseChallenge("Digest qop=\"auth\", realm=\"shellyplus1pm-a8032ab12345\", nonce=\"60dc59c6\", algorithm=SHA-256");

        c.Should().NotBeNull();
        c!.Realm.Should().Be("shellyplus1pm-a8032ab12345");
        c.Nonce.Should().Be("60dc59c6");
        c.Qop.Should().Be("auth");
        c.Algorithm.Should().Be("SHA-256");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Basic realm=\"x\"")]
    [InlineData("Digest realm=\"x\"")]                                     // no nonce
    [InlineData("Digest realm=\"x\", nonce=\"y\", algorithm=SHA-512-256")]  // unsupported algorithm
    public void ParseChallenge_RejectsUnusable(string? header) => DigestAuth.ParseChallenge(header).Should().BeNull();

    [Fact]
    public void BuildAuthorization_CarriesTheRfcResponse()
    {
        var c = new DigestAuth.Challenge(Realm, Nonce, "auth", "SHA-256", "FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS");

        var header = DigestAuth.BuildAuthorization(c, "Mufasa", "Circle of Life", "GET", "/dir/index.html", 1, Cnonce);

        header.Should().StartWith("Digest username=\"Mufasa\"");
        header.Should().Contain("response=\"753927fa0e85d155564e2e272a28d1802ca10daf4496794697cf8db5856cb6c1\"");
        header.Should().Contain("nc=00000001").And.Contain("qop=auth").And.Contain($"cnonce=\"{Cnonce}\"")
            .And.Contain("opaque=\"FQhe/qaU925kfnzjCev0ciny7QMkPqMAFRtzCUYo5tdS\"").And.Contain("algorithm=SHA-256");
    }
}
