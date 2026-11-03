using System.Collections.Concurrent;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Protocol.Tapo;

namespace KasaMonitor.Workers;

/// <summary>
/// Reads a plug's settings for the plug row of /dispozitive on demand (never from the 10 s polling), with a 60 s cache
/// per plug. Failures come back as a <see cref="DeviceDetails"/> with only an error, so the UI can show them gently.
/// </summary>
public sealed class DeviceDetailsService
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IKasaClient _kasa;
    private readonly ITapoClient _tapo;
    private readonly IKasaSettingsService _settings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IShellyClient? _shelly;
    private readonly ConcurrentDictionary<Guid, DeviceDetails> _cache = new();

    public DeviceDetailsService(IKasaClient kasa, ITapoClient tapo, IKasaSettingsService settings, Func<DateTimeOffset>? clock = null,
        IShellyClient? shelly = null)
    {
        _shelly = shelly;
        _kasa = kasa;
        _tapo = tapo;
        _settings = settings;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<DeviceDetails> GetAsync(Plug plug, bool forceRefresh, CancellationToken ct)
    {
        var now = _clock();
        if (!forceRefresh && _cache.TryGetValue(plug.Id, out var cached) && now - cached.ReadAtUtc < CacheDuration)
            return cached;

        DeviceDetails details;
        try
        {
            if (!plug.IsSupported)
                details = Failed(now, "The plug is not supported, its settings cannot be read.");
            else if (plug.Protocol == PlugProtocols.Legacy)
                details = await _kasa.GetDeviceDetailsAsync(plug.IpAddress, ct);
            else if (plug.Protocol == PlugProtocols.ShellyRpc)
                details = _shelly is null
                    ? Failed(now, "Shelly support is not configured.")
                    : await ShellyDetailsAsync(plug, (await _settings.GetAsync(ct)).ShellyCredentials?.Password, now, ct);
            else if ((await _settings.GetAsync(ct)).TapoCredentials is { } creds && _tapo.SupportsProtocol(plug.Protocol))
                details = await _tapo.GetDeviceDetailsAsync(plug.IpAddress, plug.Protocol, creds, ct);
            else
                details = Failed(now, "TP-Link credentials are missing (Settings).");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            details = Failed(now, $"The plug did not answer: {ex.Message}");
        }

        details = details with { ReadAtUtc = now };
        _cache[plug.Id] = details;
        return details;
    }

    /// <summary>Shelly.GetDeviceInfo, Shelly.GetStatus and Shelly.GetConfig, each on its own (a failure only drops its items).</summary>
    private async Task<DeviceDetails> ShellyDetailsAsync(Plug plug, string? password, DateTimeOffset now, CancellationToken ct)
    {
        var errors = new List<string>();
        ShellyDeviceInfo? info = null;
        System.Text.Json.JsonElement? status = null, config = null;
        try { info = await _shelly!.GetDeviceInfoAsync(plug.IpAddress, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"Shelly.GetDeviceInfo: {ex.Message}"); }
        try { status = await _shelly!.GetStatusAsync(plug.IpAddress, password, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"Shelly.GetStatus: {ex.Message}"); }
        try { config = await _shelly!.GetConfigAsync(plug.IpAddress, password, ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { errors.Add($"Shelly.GetConfig: {ex.Message}"); }
        return ShellyDetailsParser.Parse(info, status, config, plug.Channel, errors, now);
    }

    private static DeviceDetails Failed(DateTimeOffset now, string error) =>
        new(now, Array.Empty<DeviceDetailItem>(), new[] { error });
}
