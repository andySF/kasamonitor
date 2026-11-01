namespace KasaMonitor.Core.Abstractions;

/// <summary>Encrypts secrets before they are stored in the Settings table (implemented with ASP.NET DataProtection).</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>Returns null when the payload cannot be decrypted (e.g. lost key ring).</summary>
    string? Unprotect(string protectedPayload);
}
