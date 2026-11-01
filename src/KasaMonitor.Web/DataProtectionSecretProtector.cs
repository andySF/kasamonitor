using KasaMonitor.Core.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;

namespace KasaMonitor.Web;

public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;
    private readonly ILogger<DataProtectionSecretProtector> _log;

    public DataProtectionSecretProtector(IDataProtectionProvider provider, ILogger<DataProtectionSecretProtector> log)
    {
        _protector = provider.CreateProtector("KasaMonitor.Settings.Secrets.v1");
        _log = log;
    }

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string protectedPayload)
    {
        try
        {
            return _protector.Unprotect(protectedPayload);
        }
        catch (CryptographicException ex)
        {
            _log.LogWarning("Stored secret could not be decrypted (DataProtection key ring changed?): {Reason}. Re-enter it in /settings.", ex.Message);
            return null;
        }
    }
}
