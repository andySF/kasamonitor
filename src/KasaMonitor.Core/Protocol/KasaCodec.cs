namespace KasaMonitor.Core.Protocol;

/// <summary>Stateless XOR cipher used by TP-Link Kasa devices on the wire (port 9999).</summary>
public static class KasaCodec
{
    private const byte InitialKey = 171;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty) return Array.Empty<byte>();
        var output = new byte[plaintext.Length];
        byte key = InitialKey;
        for (int i = 0; i < plaintext.Length; i++)
        {
            key = (byte)(key ^ plaintext[i]);
            output[i] = key;
        }
        return output;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.IsEmpty) return Array.Empty<byte>();
        var output = new byte[ciphertext.Length];
        byte key = InitialKey;
        for (int i = 0; i < ciphertext.Length; i++)
        {
            output[i] = (byte)(key ^ ciphertext[i]);
            key = ciphertext[i];
        }
        return output;
    }
}
