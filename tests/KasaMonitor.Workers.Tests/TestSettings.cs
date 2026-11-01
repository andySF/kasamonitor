using KasaMonitor.Core.Abstractions;

namespace KasaMonitor.Workers.Tests;

/// <summary>Reversible fake: never stores the plaintext as-is.</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) => "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext));

    public string? Unprotect(string protectedPayload) =>
        protectedPayload.StartsWith("enc:", StringComparison.Ordinal)
            ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedPayload[4..]))
            : null;
}

internal static class TestSettings
{
    public static KasaSettingsService Create(TestScopeFactory scopes, KasaSettingsDefaults? defaults = null) =>
        new(scopes, defaults ?? new KasaSettingsDefaults(BroadcastNetworks: "192.168.254.255"), new FakeSecretProtector());
}
