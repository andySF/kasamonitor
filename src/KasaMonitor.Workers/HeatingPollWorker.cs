using System.Text.Json;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Heating;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>Outcome of one poll cycle; decides the wait before the next one (<see cref="HeatingPollWorker.NextDelay"/>).</summary>
public enum HeatingPollResult
{
    /// <summary>The boiler was read.</summary>
    Read,
    /// <summary>No HTTP call was made (disabled, no Client ID, not connected, local budget exhausted).</summary>
    NotCalled,
    /// <summary>A call was made and failed (5xx, network, gateway offline, repeated 401, no boiler, refresh 5xx/429, …).</summary>
    CallFailed,
    /// <summary>HTTP 429: paused until the reset time.</summary>
    RateLimited,
}

/// <summary>
/// Polls the Viessmann boiler: once connected, discovers the installation (one call), then every
/// <c>ViessmannPollIntervalSeconds</c> (≥ 60 s, default 120 s = 720 of the 1450 daily calls) makes ONE bulk features
/// call, keeps the raw answer (latest only), stores a reading, the daily energy rows and the period values, and
/// publishes the snapshot for the UI and /metrics. After a failed call the next one waits 1×, 2×, 4× … the interval
/// (at most 30 minutes, never less than the interval) until the first success; an HTTP 429 pauses until the reset
/// time. An API 401 triggers one token refresh and one retry. Read-only: no command is ever sent to the boiler.
/// </summary>
public sealed class HeatingPollWorker : BackgroundService
{
    /// <summary>Wait while nothing is called (disabled, not connected): only the settings are re-read.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHeatingSettingsService _settings;
    private readonly ViessmannAuthService _auth;
    private readonly IViessmannClient _client;
    private readonly HeatingState _state;
    private readonly ILogger<HeatingPollWorker> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeZoneInfo _tz;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private bool _loaded;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    /// <param name="wait">Test hook replacing the wait between cycles (default: the delay, cut short by <see cref="Wake"/>).</param>
    public HeatingPollWorker(IServiceScopeFactory scopeFactory, IHeatingSettingsService settings, ViessmannAuthService auth,
        IViessmannClient client, HeatingState state, ILogger<HeatingPollWorker> log, Func<DateTimeOffset>? clock = null, TimeZoneInfo? tz = null,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _wait = wait ?? (async (d, c) => await _wake.WaitAsync(d, c));
        _scopeFactory = scopeFactory;
        _settings = settings;
        _auth = auth;
        _client = client;
        _state = state;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _tz = tz ?? TimeZoneInfo.Local;
        _auth.Connected += Wake;
    }

    /// <summary>Polls now (after connecting or saving the settings) instead of waiting for the interval.</summary>
    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try { _wake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                var s = await _settings.GetAsync(ct);
                _state.PollIntervalSeconds = s.PollIntervalSeconds;
                await LoadStoredAsync(ct);
                var result = await PollOnceAsync(ct);
                failures = result switch { HeatingPollResult.CallFailed => failures + 1, HeatingPollResult.Read => 0, _ => failures };
                delay = NextDelay(result, failures, s.PollIntervalSeconds, _state.Budget.BlockedUntil, _clock());
                if (result == HeatingPollResult.CallFailed)
                    _log.LogInformation("Heating: failure {Count} in a row, next call in {Delay}", failures, delay);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Unexpected (e.g. database): treated as a failed call, never retried faster than the interval.
                failures++;
                delay = NextDelay(HeatingPollResult.CallFailed, failures, _state.PollIntervalSeconds, null, _clock());
                _log.LogWarning(ex, "Heating poll failed; next attempt in {Delay}", delay);
            }
            try { await _wait(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Wait before the next cycle: the interval after a read; after <paramref name="consecutiveFailures"/> failed calls
    /// interval × 2^(n−1), at most 30 minutes (or the interval, if longer); after a 429 until the reset (at least the
    /// interval); 30 s when nothing was called.
    /// </summary>
    public static TimeSpan NextDelay(HeatingPollResult result, int consecutiveFailures, int intervalSeconds, DateTimeOffset? blockedUntil, DateTimeOffset now)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(intervalSeconds, ViessmannApi.MinPollIntervalSeconds));
        switch (result)
        {
            case HeatingPollResult.Read:
                return interval;
            case HeatingPollResult.CallFailed:
                var cap = interval > MaxBackoff ? interval : MaxBackoff;
                var factor = Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 20));
                var backoff = TimeSpan.FromSeconds(interval.TotalSeconds * factor);
                return backoff > cap ? cap : backoff;
            case HeatingPollResult.RateLimited:
                var until = blockedUntil is { } b ? b - now : interval;
                return until < interval ? interval : until > TimeSpan.FromHours(24) ? TimeSpan.FromHours(24) : until;
            default:
                return IdleDelay;
        }
    }

    /// <summary>After a restart: the stored device, its last raw dump and totals, so the page has data before the first poll.</summary>
    public async Task LoadStoredAsync(CancellationToken ct)
    {
        if (_loaded) return;
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IHeatingRepository>();
        var device = (await repo.ListDevicesAsync(ct)).FirstOrDefault();
        if (device is null)
        {
            _loaded = true;
            return;
        }
        HeatingSnapshot? snapshot = null;
        if (await repo.GetRawAsync(device.Id, ct) is { } raw)
        {
            try { snapshot = HeatingFeatureParser.Parse(raw.Json, raw.TsUtc); }
            catch (JsonException) { }
        }
        _state.Publish(new HeatingLive(device, snapshot, await repo.GetTotalsAsync(device.Id, HeatingEnergy.DayOf(_clock(), _tz), ct),
            device.LastSeenUtc, device.LastError, LastPollOk: false, await repo.ListPeriodsAsync(device.Id, ct)));
        // Only now: a failure above is retried at the next cycle.
        _loaded = true;
    }

    /// <summary>One cycle (discovery if needed + one features call).</summary>
    public async Task<HeatingPollResult> PollOnceAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.HeatingEnabled) { _state.SetStatus("Centrala e dezactivată în Setări."); return HeatingPollResult.NotCalled; }
        if (!s.HasClientId) { _state.SetStatus("Lipsește Client ID-ul Viessmann (Setări → Centrală)."); return HeatingPollResult.NotCalled; }
        if (s.Token is null) { _state.SetStatus(s.AuthError ?? "Contul ViCare nu e conectat."); return HeatingPollResult.NotCalled; }

        HeatingDevice? device = _state.Current?.Device;
        try
        {
            if (device is null)
            {
                using (var scope = _scopeFactory.CreateScope())
                    device = (await scope.ServiceProvider.GetRequiredService<IHeatingRepository>().ListDevicesAsync(ct)).FirstOrDefault();
                if (device is null)
                {
                    var (found, discoveryCalled) = await DiscoverAsync(ct);
                    if (found is null) return discoveryCalled ? HeatingPollResult.CallFailed : HeatingPollResult.NotCalled;
                    device = found;
                }
            }
            if (!device.Enabled) { _state.SetStatus("Dispozitivul e dezactivat."); return HeatingPollResult.NotCalled; }

            var json = await CallAsync(token => _client.GetFeaturesJsonAsync(token, device.InstallationId, device.GatewaySerial, ct), ct);
            if (json is null) return HeatingPollResult.NotCalled;
            var now = _clock();
            var snapshot = HeatingFeatureParser.Parse(json, now);

            HeatingEnergyTotals totals;
            var periods = HeatingEnergyBuilder.Periods(device.Id, snapshot, s.GasKwhPerM3, s.GasUnit);
            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IHeatingRepository>();
                await repo.SaveRawAsync(device.Id, now, json, ct);
                await repo.SaveReadingAsync(HeatingReading.From(device.Id, snapshot, s.GasKwhPerM3, s.GasUnit), ct);
                var decreases = await repo.UpsertDailyAsync(HeatingEnergyBuilder.Days(device.Id, snapshot, _tz, s.GasKwhPerM3, s.GasUnit), ct);
                foreach (var d in decreases)
                    _log.LogInformation(
                        "Heating gas {Purpose} for {Day}: received {Received} m3 < stored highest {Max} m3 (last {Last} m3) at {Local} local; boiler counter reset or correction (feature timestamp {FeatureTs})",
                        d.Purpose, d.Day.ToString("yyyy-MM-dd"), d.ReceivedM3, d.StoredMaxM3, d.StoredLastM3,
                        TimeZoneInfo.ConvertTime(now, _tz).ToString("yyyy-MM-dd HH:mm:ss"),
                        d.Purpose == HeatingKinds.Heating ? snapshot.GasHeating?.FeatureTs : snapshot.GasDhw?.FeatureTs);
                await repo.UpsertPeriodsAsync(periods, ct);
                await repo.UpdateDeviceStatusAsync(device.Id, now, null, ct);
                totals = await repo.GetTotalsAsync(device.Id, HeatingEnergy.DayOf(now, _tz), ct);
                if (s.ReadingsRetentionDays > 0 && now - _lastRetention > TimeSpan.FromHours(24))
                {
                    _lastRetention = now;
                    var deleted = await repo.DeleteReadingsBeforeAsync(now.AddDays(-s.ReadingsRetentionDays), ct);
                    if (deleted > 0) _log.LogInformation("Heating readings retention: deleted {Count} readings older than {Days} days", deleted, s.ReadingsRetentionDays);
                }
            }
            device = device with { LastSeenUtc = now, LastError = null };
            _state.SetStatus(null);
            _state.Publish(new HeatingLive(device, snapshot, totals, now, null, LastPollOk: true, periods));
            _log.LogDebug("Heating read: {Features} features, {Unmapped} unmapped, burner {Burner}", snapshot.FeatureCount, snapshot.Unmapped.Count, snapshot.BurnerActive);
            return HeatingPollResult.Read;
        }
        catch (ViessmannRateLimitException ex)
        {
            var until = _state.Budget.Block(ex.ResetUtc);
            _log.LogWarning("Viessmann API answered 429 ({Limit}); no calls until {Until:u}", ex.LimitName ?? "rate limit", until);
            var message = $"Limita de apeluri Viessmann atinsă ({ex.LimitName ?? "429"}); pauză până la {until.ToLocalTime():yyyy-MM-dd HH:mm}.";
            _state.SetStatus(message);
            await FailAsync(device, message, ct);
            return HeatingPollResult.RateLimited;
        }
        catch (HeatingDisconnectedException ex)
        {
            // Tokens cleared (or none usable): nothing more is called until the account is reconnected.
            _state.SetStatus(ex.Message);
            await FailAsync(device, ex.Message, ct);
            return HeatingPollResult.NotCalled;
        }
        catch (Exception ex) when (ex is ViessmannException or HttpRequestException or JsonException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // A request left (API, or the token endpoint answering 5xx/429): back off.
            _log.LogWarning("Heating poll failed: {Reason}", ex.Message);
            await FailAsync(device, ex.Message, ct);
            return HeatingPollResult.CallFailed;
        }
    }

    /// <summary>The boiler of the account (one call); <c>Called</c> = false when the budget did not allow the call.</summary>
    private async Task<(HeatingDevice? Device, bool Called)> DiscoverAsync(CancellationToken ct)
    {
        var json = await CallAsync(token => _client.GetInstallationsJsonAsync(token, ct), ct);
        if (json is null) return (null, false);
        var boiler = ViessmannInstallations.PickBoiler(ViessmannInstallations.Parse(json));
        if (boiler is null)
        {
            _state.SetStatus("Contul ViCare nu are niciun dispozitiv de încălzire (sau clientul nu e încă activat, poate dura o oră).");
            return (null, true);
        }
        using var scope = _scopeFactory.CreateScope();
        var device = await scope.ServiceProvider.GetRequiredService<IHeatingRepository>().UpsertDeviceAsync(new HeatingDevice(Guid.NewGuid(),
            ViessmannApi.Provider, boiler.InstallationId, boiler.GatewaySerial, boiler.DeviceId, boiler.ModelId,
            ViessmannInstallations.FriendlyModel(boiler.ModelId), true, null, null), ct);
        _log.LogInformation("Heating device discovered: {Model} (device {DeviceId}, gateway status {Status})", boiler.ModelId, boiler.DeviceId, boiler.AggregatedStatus);
        _state.Publish(new HeatingLive(device, null, null, null, null, LastPollOk: false));
        return (device, true);
    }

    /// <summary>
    /// One API call within the budget, with the current access token (obtained first, so a missing or failed token
    /// never consumes a budget slot); on a 401 the token is refreshed and the call repeated once (when the budget
    /// allows). Every request that actually leaves is counted for today. Null = not called (budget exhausted or paused).
    /// </summary>
    private async Task<string?> CallAsync(Func<string, Task<string>> call, CancellationToken ct)
    {
        var token = await _auth.GetAccessTokenAsync(ct);
        if (!Acquire()) return null;
        try
        {
            _state.Calls.Record();
            return await call(token);
        }
        catch (ViessmannAuthException)
        {
            token = await _auth.ForceRefreshAsync(ct);
            if (!Acquire()) return null;
            _state.Calls.Record();
            return await call(token);
        }
    }

    private bool Acquire()
    {
        if (_state.Budget.TryAcquire(out var retryAt)) return true;
        _state.SetStatus($"Bugetul de apeluri Viessmann e consumat; următorul apel după {retryAt?.ToLocalTime():yyyy-MM-dd HH:mm}.");
        _log.LogDebug("Heating call skipped: budget exhausted until {RetryAt:u}", retryAt);
        return false;
    }

    private async Task FailAsync(HeatingDevice? device, string error, CancellationToken ct)
    {
        if (device is null) return;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IHeatingRepository>().UpdateDeviceStatusAsync(device.Id, null, error, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Could not store the heating error");
        }
        var current = _state.Current;
        _state.Publish(current is not null && current.Device.Id == device.Id
            ? current with { LastError = error, LastPollOk = false }
            : new HeatingLive(device, null, null, device.LastSeenUtc, error, LastPollOk: false));
    }

    public override void Dispose()
    {
        _auth.Connected -= Wake;
        base.Dispose();
    }
}
