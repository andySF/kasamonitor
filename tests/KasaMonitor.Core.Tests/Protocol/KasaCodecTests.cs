using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Text;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaCodecTests
{
    [Fact]
    public void Encrypt_EmptyInput_ReturnsEmpty()
    {
        KasaCodec.Encrypt(ReadOnlySpan<byte>.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Decrypt_EmptyInput_ReturnsEmpty()
    {
        KasaCodec.Decrypt(ReadOnlySpan<byte>.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Encrypt_KnownVector_ProducesExpectedBytes()
    {
        // Plaintext "{}" -> bytes [123, 125].
        // key=171:
        //   123 XOR 171 = 208 (0xD0). new key = 208
        //   125 XOR 208 = 173 (0xAD). new key = 173
        var plaintext = Encoding.UTF8.GetBytes("{}");
        var encrypted = KasaCodec.Encrypt(plaintext);
        encrypted.Should().Equal(new byte[] { 0xD0, 0xAD });
    }

    [Fact]
    public void Decrypt_InverseOfEncrypt_RoundTrips()
    {
        var plaintext = Encoding.UTF8.GetBytes("{\"system\":{\"get_sysinfo\":{}}}");
        var encrypted = KasaCodec.Encrypt(plaintext);
        var decrypted = KasaCodec.Decrypt(encrypted);
        decrypted.Should().Equal(plaintext);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("hello")]
    [InlineData("{\"emeter\":{\"get_realtime\":{}}}")]
    public void RoundTrip_PreservesAnyAsciiInput(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var roundTripped = Encoding.UTF8.GetString(KasaCodec.Decrypt(KasaCodec.Encrypt(bytes)));
        roundTripped.Should().Be(input);
    }
}
