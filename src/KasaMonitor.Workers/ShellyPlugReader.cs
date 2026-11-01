using System.Net;
using System.Text.Json;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;

namespace KasaMonitor.Workers;

/// <summary>Reads a Shelly device (GetStatus + GetConfig) and maps it to plugs; shared by discovery and the hourly refresh of polling.</summary>
public static class ShellyPlugReader
{
    /// <param name="status">A GetStatus answer already in hand (polling); null to request it.</param>
    public static async Task<IReadOnlyList<DiscoveredPlug>> ReadAsync(IShellyClient shelly, IPAddress ip, ShellyDeviceInfo info,
        ShellyCredentials? creds, CancellationToken ct, JsonElement? status = null)
    {
        var password = creds?.Password;
        if (info.AuthEnabled && string.IsNullOrEmpty(password))
            return new[] { ShellySupport.Unsupported(ip, info, ShellySupport.MissingPasswordReason) };
        try
        {
            var st = status ?? await shelly.GetStatusAsync(ip, password, ct);
            JsonElement? config = null;
            try { config = await shelly.GetConfigAsync(ip, password, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* names are optional: aliases fall back to the device id */ }
            return ShellySupport.ToPlugs(ip, info, st, config);
        }
        catch (ShellyAuthenticationException ex)
        {
            return new[] { ShellySupport.Unsupported(ip, info, ex.PasswordMissing ? ShellySupport.MissingPasswordReason : ShellySupport.AuthFailedReason) };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new[] { ShellySupport.Unsupported(ip, info, $"eroare Shelly RPC: {ex.Message}") };
        }
    }
}
