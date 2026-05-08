using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Text;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaWireProtocolTests
{
    [Fact]
    public void EncodeTcp_PrependsBigEndianLength()
    {
        var bytes = KasaWireProtocol.EncodeTcp("{}");
        bytes.Length.Should().Be(4 + 2);
        bytes[0..4].Should().Equal(new byte[] { 0, 0, 0, 2 }); // length = 2
    }

    [Fact]
    public void EncodeTcp_BodyIsXorEncrypted()
    {
        var bytes = KasaWireProtocol.EncodeTcp("{}");
        bytes[4..].Should().Equal(KasaCodec.Encrypt(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void DecodeTcp_StripsLengthAndDecrypts()
    {
        var encoded = KasaWireProtocol.EncodeTcp("hello");
        var decoded = KasaWireProtocol.DecodeTcp(encoded);
        decoded.Should().Be("hello");
    }

    [Fact]
    public void DecodeTcp_ThrowsWhenLengthMismatch()
    {
        var truncated = new byte[] { 0, 0, 0, 100, 1, 2, 3 };
        var act = () => KasaWireProtocol.DecodeTcp(truncated);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void EncodeUdp_HasNoLengthPrefix()
    {
        var bytes = KasaWireProtocol.EncodeUdp("{}");
        bytes.Should().Equal(KasaCodec.Encrypt(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void DecodeUdp_DecryptsRaw()
    {
        var encoded = KasaWireProtocol.EncodeUdp("hello");
        KasaWireProtocol.DecodeUdp(encoded).Should().Be("hello");
    }

    [Fact]
    public void DecodeTcp_RejectsHeaderTooShort()
    {
        var act = () => KasaWireProtocol.DecodeTcp(new byte[] { 0, 0, 1 });
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DecodeTcp_RejectsAbsurdLength()
    {
        // Length declared 16 MiB; we cap at 1 MiB to avoid OOM amplification.
        var bytes = new byte[] { 0x01, 0x00, 0x00, 0x00 };
        var act = () => KasaWireProtocol.DecodeTcp(bytes);
        act.Should().Throw<InvalidDataException>().WithMessage("*too large*");
    }
}
