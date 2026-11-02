using System.Net;
using System.Text.Json;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Protocol.Shelly;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// Reads every climate source on its own schedule and hands the samples to <see cref="ClimateRecorder"/>:
/// the boiler's readings (every minute, from the database), Govee (every 10 minutes, only the devices ticked „acasă”;
/// the device list every 6 hours), Shelly (mDNS every 5 minutes, the devices with temperature components every
/// minute), Open-Meteo (every 15 minutes when latitude/longitude are set) and the 1-year retention (daily).
/// A failing source sets its status text and never stops the others. Read-only towards every device and API.
/// </summary>
public sealed class ClimateWorker : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ViessmannInterval = TimeSpan.FromMinutes(1);
    /// <summary>On the first run without any boiler sensor, the stored boiler readings of the last 30 days are imported.</summary>
    public static readonly TimeSpan ViessmannBackfill = TimeSpan.FromDays(30);
    /// <summary>The boiler readings are read up to this far behind „now” when none is newer (one being written is not skipped).</summary>
    public static readonly TimeSpan ViessmannSafetyLag = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ShellyDiscoveryInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ShellyPollInterval = ClimateFreshness.ShellyInterval;
    public static readonly TimeSpan ShellyMdnsTimeout = TimeSpan.FromSeconds(2.5);
    public static readonly TimeSpan ShellyProbeTimeout = TimeSpan.FromSeconds(1);
    /// <summary>A Shelly without climate components is asked again only after an hour (an Add-on may be fitted later).</summary>
    public static readonly TimeSpan ShellyRecheckInterval = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(24);
    /// <summary>Raw readings are kept one year (a few MB per sensor).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(365);
    /// <summary>After a Govee 429 nothing is requested for an hour.</summary>
    public static readonly TimeSpan GoveeRateLimitPause = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IClimateSettingsService _settings;
    private readonly IKasaSettingsService? _kasa;
    private readonly ClimateRecorder _recorder;
    private readonly ClimateState _state;
    private readonly IGoveeClient _govee;
    private readonly IOpenMeteoClient _meteo;
    private readonly IShellyClient? _shelly;
    private readonly ILogger<ClimateWorker> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private DateTimeOffset _viessmannDue, _goveeDue, _goveeListDue, _goveeBlockedUntil, _shellyDiscoveryDue, _shellyPollDue, _meteoDue, _retentionDue;
    private DateTimeOffset? _viessmannCursor;
    private readonly Dictionary<string, ShellyClimateDevice> _shellyDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _shellyNonClimate = new(StringComparer.Ordinal);

    private sealed record ShellyClimateDevice(IPAddress Ip, ShellyDeviceInfo Info, JsonElement? Config);

    public ClimateWorker(IServiceScopeFactory scopeFactory, IClimateSettingsService settings, ClimateRecorder recorder, ClimateState state,
        IGoveeClient govee, IOpenMeteoClient meteo, ILogger<ClimateWorker> log, IKasaSettingsService? kasa = null, IShellyClient? shelly = null,
        Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _scopeFactory = scopeFactory;
        _settings = settings;
        _recorder = recorder;
        _state = state;
        _govee = govee;
        _meteo = meteo;
        _log = log;
        _kasa = kasa;
        _shelly = shelly;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _wait = wait ?? (async (d, c) => await _wake.WaitAsync(d, c));
    }

    /// <summary>Reads Govee and Open-Meteo now (settings saved); <paramref name="refreshGoveeList"/> also re-reads the Govee device list.</summary>
    public void Wake(bool refreshGoveeList = false)
    {
        _goveeDue = DateTimeOffset.MinValue;
        _meteoDue = DateTimeOffset.MinValue;
        if (refreshGoveeList) _goveeListDue = DateTimeOffset.MinValue;
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try { await RunDueAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { _log.LogWarning(ex, "Climate cycle failed"); }
            try { await _wait(Tick, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Runs every source whose time has come.</summary>
    public async Task RunDueAsync(CancellationToken ct)
    {
        await _recorder.LoadAsync(ct);
        var now = _clock();
        if (now >= _viessmannDue)
        {
            _viessmannDue = now + ViessmannInterval;
            await GuardAsync(ClimateSources.Viessmann, () => ViessmannAsync(now, ct), ct);
        }
        if (now >= _goveeDue)
        {
            _goveeDue = now + GoveeApi.PollInterval;
            await GuardAsync(ClimateSources.Govee, () => GoveeAsync(now, ct), ct);
        }
        if (now >= _shellyDiscoveryDue)
        {
            _shellyDiscoveryDue = now + ShellyDiscoveryInterval;
            _shellyPollDue = now + ShellyPollInterval;
            await GuardAsync(ClimateSources.Shelly, () => ShellyDiscoverAsync(now, ct), ct);
        }
        else if (now >= _shellyPollDue)
        {
            _shellyPollDue = now + ShellyPollInterval;
            await GuardAsync(ClimateSources.Shelly, () => ShellyPollAsync(now, ct), ct);
        }
        if (now >= _meteoDue)
        {
            _meteoDue = now + OpenMeteoApi.PollInterval;
            await GuardAsync(ClimateSources.OpenMeteo, () => OpenMeteoAsync(now, ct), ct);
        }
        if (now >= _retentionDue)
        {
            _retentionDue = now + RetentionInterval;
            await GuardAsync("retention", () => RetentionAsync(now, ct), ct);
        }
    }

    private async Task GuardAsync(string source, Func<Task> run, CancellationToken ct)
    {
        try { await run(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning("Climate {Source} failed: {Reason}", source, ex.Message);
            if (source != "retention") _state.SetSourceStatus(source, ex.Message);
        }
    }

    /// <summary>The boiler readings stored since the last one used (on the first run: the last 30 days), as three virtual sensors.</summary>
    public async Task<int> ViessmannAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var heating = scope.ServiceProvider.GetRequiredService<IHeatingRepository>();
        var device = (await heating.ListDevicesAsync(ct)).FirstOrDefault(d => d.Enabled);
        if (device is null) return 0;
        _viessmannCursor ??= _recorder.SensorsOf(ClimateSources.Viessmann).Select(s => s.LastSeenUtc).Where(t => t is not null).Max()
                             ?? now - ViessmannBackfill;
        var count = 0;
        while (_viessmannCursor < now)
        {
            var from = _viessmannCursor.Value.AddSeconds(1);
            var to = from.AddDays(1) < now ? from.AddDays(1) : now;
            var readings = await heating.ListReadingsAsync(device.Id, from, to, ct);
            foreach (var r in readings)
            {
                foreach (var sample in ViessmannClimate.Samples(r))
                    await _recorder.RecordAsync(sample, ct);
                _viessmannCursor = r.TsUtc;
                count++;
            }
            if (to >= now)
            {
                // Nothing newer than the last reading: move up to „now − lag” at most, so a reading being written is not skipped.
                var safe = now - ViessmannSafetyLag;
                if (readings.Count == 0 && safe > _viessmannCursor) _viessmannCursor = safe;
                break;
            }
            if (readings.Count == 0) _viessmannCursor = to;
        }
        _state.SetSourceStatus(ClimateSources.Viessmann, null);
        return count;
    }

    /// <summary>Govee: the device list when due, then the state of every thermometer ticked „acasă”; unticked ones are archived.</summary>
    public async Task GoveeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        var key = _settings.GoveeApiKey;
        if (key is null)
        {
            _state.SetSourceStatus(ClimateSources.Govee, null);
            return;
        }
        if (now < _goveeBlockedUntil) return;
        try
        {
            if (_state.GoveeDevices is null || now >= _goveeListDue)
            {
                _goveeListDue = now + GoveeApi.DeviceListInterval;
                _state.GoveeCalls.Record();
                _state.SetGoveeDevices(await _govee.ListDevicesAsync(key, ct), now);
            }
            // Ticked again → shown and stored again; unticked → hidden (its history stays).
            foreach (var sensor in _recorder.SensorsOf(ClimateSources.Govee))
                await _recorder.SetArchivedAsync(ClimateSources.Govee, sensor.ExternalId, !s.GoveeHomeDevices.Contains(sensor.ExternalId), ct);
            var errors = new List<string>();
            foreach (var d in (_state.GoveeDevices ?? Array.Empty<GoveeDevice>()).Where(d => d.IsThermometer && s.GoveeHomeDevices.Contains(d.Device)))
            {
                try
                {
                    _state.GoveeCalls.Record();
                    var st = await _govee.GetStateAsync(key, d.Sku, d.Device, ct);
                    await _recorder.RecordAsync(new ClimateSample(ClimateSources.Govee, d.Device, d.Name, now, st.TemperatureC, st.HumidityPercent,
                        st.BatteryPercent, Online: st.Online != false), ct);
                }
                catch (Exception ex) when (ex is not GoveeRateLimitException && ex is GoveeException or HttpRequestException or JsonException
                                               || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    errors.Add(d.Name + ": " + ex.Message);
                }
            }
            _state.SetSourceStatus(ClimateSources.Govee, errors.Count == 0 ? null : string.Join("; ", errors));
            foreach (var e in errors) _log.LogWarning("Govee: {Error}", e);
        }
        catch (GoveeRateLimitException ex)
        {
            _goveeBlockedUntil = now + GoveeRateLimitPause;
            _state.SetSourceStatus(ClimateSources.Govee, ex.Message + "; pauză până la " + _goveeBlockedUntil.ToLocalTime().ToString("HH:mm"));
            _log.LogWarning("Govee rate limit: {Reason}; paused for {Pause}", ex.Message, GoveeRateLimitPause);
        }
    }

    /// <summary>mDNS (<c>_shelly._tcp</c>): every new Gen2+ address is asked once; those with temperature components are kept and read.</summary>
    public async Task ShellyDiscoverAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_shelly is null) return;
        var kasa = _kasa is null ? null : await _kasa.GetAsync(ct);
        if (kasa is { ShellyDiscoveryEnabled: true } || kasa is null)
        {
            var addresses = new List<IPAddress>();
            await foreach (var answer in _shelly.DiscoverAsync(ShellyMdnsTimeout, ct))
                if (answer.Gen is null or >= 2 && !addresses.Contains(answer.Address)) addresses.Add(answer.Address);
            var known = _shellyDevices.Values.Select(d => d.Ip).ToHashSet();
            foreach (var ip in addresses.Where(a => !known.Contains(a)))
            {
                if (_shellyNonClimate.TryGetValue(ip.ToString(), out var checkedAt) && now - checkedAt < ShellyRecheckInterval) continue;
                await ProbeShellyAsync(ip, kasa?.ShellyCredentials?.Password, now, ct);
            }
        }
        await ShellyPollAsync(now, ct);
    }

    private async Task ProbeShellyAsync(IPAddress ip, string? password, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var info = await _shelly!.GetDeviceInfoAsync(ip, ct, ShellyProbeTimeout);
            var status = await _shelly.GetStatusAsync(ip, password, ct);
            if (ShellyClimateParser.Parse(status).Channels.Count == 0)
            {
                _shellyNonClimate[ip.ToString()] = now;
                return;
            }
            JsonElement? config = null;
            try { config = await _shelly.GetConfigAsync(ip, password, ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* names are optional */ }
            // A device moved to another address replaces its old entry (same MAC).
            _shellyDevices[info.Mac] = new ShellyClimateDevice(ip, info, config);
            _log.LogInformation("Climate: Shelly {Model} at {Ip} has temperature sensors", info.App, ip);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Asleep (H&T on batteries), not a Gen2+ device, password missing: tried again at the next discovery.
            _log.LogDebug("Climate: Shelly probe {Ip} failed: {Reason}", ip, ex.Message);
        }
    }

    /// <summary>Reads every known Shelly climate device; an H&amp;T asleep does not answer (its last value stays, aging).</summary>
    public async Task ShellyPollAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_shelly is null || _shellyDevices.Count == 0) return;
        var password = _kasa is null ? null : (await _kasa.GetAsync(ct)).ShellyCredentials?.Password;
        foreach (var d in _shellyDevices.Values.ToList())
        {
            JsonElement status;
            try { status = await _shelly.GetStatusAsync(d.Ip, password, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogDebug("Climate: Shelly {Ip} did not answer: {Reason}", d.Ip, ex.Message);
                continue;
            }
            var parsed = ShellyClimateParser.Parse(status, d.Config);
            var many = parsed.Channels.Count > 1;
            foreach (var c in parsed.Channels)
                await _recorder.RecordAsync(new ClimateSample(ClimateSources.Shelly, ShellyClimateParser.ExternalId(d.Info.Mac, c.Id),
                    ShellyClimateParser.SensorName(d.Info, d.Config, c, many), now, c.TemperatureC, c.HumidityPercent, parsed.BatteryPercent,
                    BatteryPowered: parsed.OnBattery), ct);
        }
        _state.SetSourceStatus(ClimateSources.Shelly, null);
    }

    /// <summary>Open-Meteo's current temperature and humidity as the „Afară” sensor (nothing without a location).</summary>
    public async Task OpenMeteoAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.HasLocation)
        {
            _state.SetSourceStatus(ClimateSources.OpenMeteo, null);
            return;
        }
        _state.OpenMeteoCalls.Record();
        var current = await _meteo.GetCurrentAsync(s.Latitude!.Value, s.Longitude!.Value, ct);
        await _recorder.RecordAsync(new ClimateSample(ClimateSources.OpenMeteo, OpenMeteoApi.OutdoorExternalId, "Afară", now,
            current.TemperatureC, current.HumidityPercent, DefaultRoom: "Afară", DefaultOutdoor: true), ct);
        _state.SetSourceStatus(ClimateSources.OpenMeteo, null);
    }

    public async Task<int> RetentionAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var deleted = await scope.ServiceProvider.GetRequiredService<IClimateRepository>().DeleteReadingsBeforeAsync(now - Retention, ct);
        if (deleted > 0) _log.LogInformation("Climate retention: deleted {Count} readings older than {Days} days", deleted, (int)Retention.TotalDays);
        return deleted;
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
