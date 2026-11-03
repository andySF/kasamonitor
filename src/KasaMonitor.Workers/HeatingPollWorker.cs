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
/// Gas, electricity and heat are attributed to the boiler's day (setting <c>BoilerTimeZone</c>, <see cref="BoilerClock"/>);
/// a lower gas value is the daily reset only by <see cref="BoilerClock.Classify"/>, and a day still holding the previous
/// day's total is repaired at startup and once a day (<see cref="BoilerClock.RepairCurrentDay"/>).
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
    // Daily gas guard (BoilerClock): the decreases already logged (once per
    // purpose and boiler day), the last boiler day the repair ran on and the total drops already logged.
    private readonly HashSet<(string Purpose, DateOnly Day)> _loggedDecreases = new();
    private DateOnly? _lastRepairDay;
    private readonly HashSet<(string Field, DateOnly Day)> _loggedTotalDrops = new();
    private readonly HashSet<DateOnly> _loggedStale = new();

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
                _state.BoilerTimeZone = s.BoilerTimeZone;
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

    /// <summary>How far back the purpose of the current burner run is looked for (the run so far and the reading before it).</summary>
    internal static readonly TimeSpan BurnerPurposeLookback = TimeSpan.FromHours(12);

    /// <summary>
    /// The purpose of the burner at the latest stored reading (<c>heating_burner_purpose_info</c>): the rule of
    /// <see cref="BurnerTimeline"/> on the recent readings, so the current run is classified as it goes.
    /// </summary>
    private static async Task<BurnerPurpose?> BurnerPurposeAsync(IHeatingRepository repo, Guid deviceId, DateTimeOffset now, CancellationToken ct) =>
        BurnerTimeline.Current(await repo.ListReadingsAsync(deviceId, now - BurnerPurposeLookback, now, ct));

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
        var boilerTz = (await _settings.GetAsync(ct)).BoilerTimeZone;
        _state.BoilerTimeZone = boilerTz;
        await RepairOnceADayAsync(repo, device.Id, _clock(), boilerTz, ct);
        HeatingSnapshot? snapshot = null;
        if (await repo.GetRawAsync(device.Id, ct) is { } raw)
        {
            try { snapshot = HeatingFeatureParser.Parse(raw.Json, raw.TsUtc); }
            catch (JsonException) { }
        }
        _state.Publish(new HeatingLive(device, snapshot, await repo.GetTotalsAsync(device.Id, BoilerClock.DayOf(_clock(), boilerTz), ct),
            device.LastSeenUtc, device.LastError, LastPollOk: false, await repo.ListPeriodsAsync(device.Id, ct),
            await BurnerPurposeAsync(repo, device.Id, _clock(), ct)));
        // Only now: a failure above is retried at the next cycle.
        _loaded = true;
    }

    /// <summary>One cycle (discovery if needed + one features call).</summary>
    public async Task<HeatingPollResult> PollOnceAsync(CancellationToken ct)
    {
        var s = await _settings.GetAsync(ct);
        if (!s.HeatingEnabled) { _state.SetStatus("Citirea centralei e oprită în Setări → Viessmann."); return HeatingPollResult.NotCalled; }
        if (!s.HasClientId) { _state.SetStatus("Lipsește Client ID-ul Viessmann (Setări → Viessmann)."); return HeatingPollResult.NotCalled; }
        if (s.Token is null) { _state.SetStatus(s.AuthError ?? "Contul ViCare nu e conectat."); return HeatingPollResult.NotCalled; }
        var boilerTz = s.BoilerTimeZone;
        _state.BoilerTimeZone = boilerTz;

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
            if (!device.Enabled) { _state.SetStatus("Centrala e retrasă (Dispozitive)."); return HeatingPollResult.NotCalled; }

            var json = await CallAsync(token => _client.GetFeaturesJsonAsync(token, device.InstallationId, device.GatewaySerial, ct), ct);
            if (json is null) return HeatingPollResult.NotCalled;
            var now = _clock();
            var snapshot = HeatingFeatureParser.Parse(json, now);

            HeatingEnergyTotals totals;
            BurnerPurpose? purpose;
            var periods = HeatingEnergyBuilder.Periods(device.Id, snapshot, s.GasKwhPerM3, s.GasUnit);
            using (var scope = _scopeFactory.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<IHeatingRepository>();
                await repo.SaveRawAsync(device.Id, now, json, ct);
                await repo.SaveReadingAsync(HeatingReading.From(device.Id, snapshot, s.GasKwhPerM3, s.GasUnit), ct);
                purpose = await BurnerPurposeAsync(repo, device.Id, now, ct);
                await WriteDaysAsync(repo, device.Id, snapshot, HeatingEnergyBuilder.Days(device.Id, snapshot, boilerTz, s.GasKwhPerM3, s.GasUnit),
                    now, boilerTz, ct);
                await RepairOnceADayAsync(repo, device.Id, now, boilerTz, ct);
                await repo.UpsertPeriodsAsync(periods, ct);
                await repo.UpdateDeviceStatusAsync(device.Id, now, null, ct);
                var boilerToday = BoilerClock.DayOf(now, boilerTz);
                var published = _state.Current is { } cur && cur.Device.Id == device.Id ? cur.Totals : null;
                totals = await repo.GetTotalsAsync(device.Id, boilerToday, ct);
                WarnIfLower(published, totals, boilerToday);
                if (s.ReadingsRetentionDays > 0 && now - _lastRetention > TimeSpan.FromHours(24))
                {
                    _lastRetention = now;
                    var deleted = await repo.DeleteReadingsBeforeAsync(now.AddDays(-s.ReadingsRetentionDays), ct);
                    if (deleted > 0) _log.LogInformation("Heating readings retention: deleted {Count} readings older than {Days} days", deleted, s.ReadingsRetentionDays);
                }
            }
            device = device with { LastSeenUtc = now, LastError = null };
            _state.SetStatus(null);
            _state.Publish(new HeatingLive(device, snapshot, totals, now, null, LastPollOk: true, periods, purpose));
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

    /// <summary>
    /// Writes the daily rows of one reading. The "currentDay" values of the reading's boiler day that are still the
    /// previous day's total (<see cref="BoilerClock.IsStale"/>, first 30 minutes after the boiler's midnight) go to the
    /// previous day instead (they may raise it) and the new day gets nothing from them. The remaining gas values go
    /// through <see cref="BoilerClock.Classify"/>: a reset overwrites the target day with the new value (and, just after
    /// midnight, carries the stored highest to the previous day); any other drop leaves the day as it is (highest and
    /// last, so the total never goes down) and is logged at Warning once per purpose and boiler day.
    /// </summary>
    private async Task WriteDaysAsync(IHeatingRepository repo, Guid deviceId, HeatingSnapshot snapshot, IReadOnlyList<HeatingEnergyDay> days,
        DateTimeOffset now, TimeZoneInfo boilerTz, CancellationToken ct)
    {
        var readDay = BoilerClock.DayOf(now, boilerTz);
        var prevDay = readDay.AddDays(-1);
        var rows = new List<HeatingEnergyDay>(days.Count);
        foreach (var d in days)
        {
            var row = d;
            if (d.Day == readDay && d.Source == HeatingKinds.SourceSummary)
            {
                var stored = await repo.ListDailyAsync(deviceId, prevDay, readDay, ct);
                var today = stored.FirstOrDefault(x => x.Day == readDay);
                var previous = stored.FirstOrDefault(x => x.Day == prevDay);

                // Yesterday's total still read after the boiler's midnight: it belongs to yesterday.
                if (previous is not null)
                {
                    var stale = new List<string>();
                    bool Stale(double? value, double? prevMax, double? prevLast, double minPrevious = BoilerClock.StaleMinPreviousM3) =>
                        value is { } v && BoilerClock.IsStale(v, prevMax, prevLast, now, boilerTz, minPrevious);
                    foreach (var (purpose, value, prevMax, prevLast) in new[]
                             {
                                 (HeatingKinds.Heating, d.GasHeatingMaxM3, previous.GasHeatingMaxM3, previous.GasHeatingLastM3),
                                 (HeatingKinds.Dhw, d.GasDhwMaxM3, previous.GasDhwMaxM3, previous.GasDhwLastM3),
                             })
                    {
                        if (!Stale(value, prevMax, prevLast)) continue;
                        var v = value!.Value;
                        await repo.SetDailyGasAsync(deviceId, prevDay, purpose, Math.Max(prevMax ?? v, v), Math.Max(prevLast ?? prevMax ?? v, v), ct);
                        row = purpose == HeatingKinds.Heating ? row with { GasHeatingMaxM3 = null, GasHeatingLastM3 = null } : row with { GasDhwMaxM3 = null, GasDhwLastM3 = null };
                        stale.Add($"gas {purpose} {v}");
                    }
                    // Heat and electricity keep the day's highest: a stale value would stay on the new day for good.
                    var toPrevious = new HeatingEnergyDay(deviceId, prevDay, null, null,
                        Stale(d.HeatHeatingKwh, previous.HeatHeatingKwh, null, BoilerClock.StaleMinPreviousHeatKwh) ? d.HeatHeatingKwh : null,
                        Stale(d.HeatDhwKwh, previous.HeatDhwKwh, null, BoilerClock.StaleMinPreviousHeatKwh) ? d.HeatDhwKwh : null,
                        Stale(d.PowerHeatingKwh, previous.PowerHeatingKwh, null, BoilerClock.StaleMinPreviousPowerKwh) ? d.PowerHeatingKwh : null,
                        Stale(d.PowerDhwKwh, previous.PowerDhwKwh, null, BoilerClock.StaleMinPreviousPowerKwh) ? d.PowerDhwKwh : null,
                        previous.Source);
                    if (toPrevious.HeatHeatingKwh is not null || toPrevious.HeatDhwKwh is not null || toPrevious.PowerHeatingKwh is not null || toPrevious.PowerDhwKwh is not null)
                    {
                        rows.Add(toPrevious);
                        row = row with
                        {
                            HeatHeatingKwh = toPrevious.HeatHeatingKwh is null ? row.HeatHeatingKwh : null,
                            HeatDhwKwh = toPrevious.HeatDhwKwh is null ? row.HeatDhwKwh : null,
                            PowerHeatingKwh = toPrevious.PowerHeatingKwh is null ? row.PowerHeatingKwh : null,
                            PowerDhwKwh = toPrevious.PowerDhwKwh is null ? row.PowerDhwKwh : null,
                        };
                        stale.Add("heat/electricity");
                    }
                    if (stale.Count > 0 && _loggedStale.Add(readDay))
                        _log.LogInformation(
                            "Heating: {Values} read at {Boiler} boiler time ({Local} local) is still {Previous}'s total: kept on {KeptOn}, {Day} starts with the first lower value (logged once a day)",
                            string.Join(", ", stale), Local(now, boilerTz), Local(now, _tz), prevDay.ToString("yyyy-MM-dd"), prevDay.ToString("yyyy-MM-dd"),
                            readDay.ToString("yyyy-MM-dd"));
                }

                foreach (var purpose in new[] { HeatingKinds.Heating, HeatingKinds.Dhw })
                {
                    var heating = purpose == HeatingKinds.Heating;
                    if ((heating ? row.GasHeatingMaxM3 : row.GasDhwMaxM3) is not { } received) continue;
                    var storedMax = heating ? today?.GasHeatingMaxM3 : today?.GasDhwMaxM3;
                    var storedLast = heating ? today?.GasHeatingLastM3 : today?.GasDhwLastM3;
                    var decision = BoilerClock.Classify(received, storedMax, readDay, now, boilerTz);
                    var featureTs = heating ? snapshot.GasHeating?.FeatureTs : snapshot.GasDhw?.FeatureTs;
                    if (decision.Action == GasDayAction.IgnoredDecrease)
                    {
                        // The day keeps its value (highest and last): the total never goes down because of a drop.
                        row = heating ? row with { GasHeatingMaxM3 = null, GasHeatingLastM3 = null } : row with { GasDhwMaxM3 = null, GasDhwLastM3 = null };
                        if (_loggedDecreases.Add((purpose, readDay)))
                            _log.LogWarning(
                                "Heating gas {Purpose} for {Day}: received {Received} m3 < stored highest {Max} m3 (last {Last} m3) at {Boiler} boiler time ({Local} local); not the daily reset (only 23:45-00:30 boiler time): the day is kept until the boiler's value passes it, logged once a day (feature timestamp {FeatureTs})",
                                purpose, readDay.ToString("yyyy-MM-dd"), received, storedMax, storedLast, Local(now, boilerTz), Local(now, _tz), featureTs);
                    }
                    else if (decision.Action == GasDayAction.Reset)
                    {
                        row = heating ? row with { GasHeatingMaxM3 = null, GasHeatingLastM3 = null } : row with { GasDhwMaxM3 = null, GasDhwLastM3 = null };
                        await repo.SetDailyGasAsync(deviceId, decision.TargetDay, purpose, received, received, ct);
                        var carried = "";
                        if (decision.CarryToPreviousDay && storedMax is { } leaked)
                        {
                            var (prevMax, prevLast) = BoilerClock.CarryToPrevious(heating ? previous?.GasHeatingMaxM3 : previous?.GasDhwMaxM3,
                                heating ? previous?.GasHeatingLastM3 : previous?.GasDhwLastM3, leaked);
                            await repo.SetDailyGasAsync(deviceId, readDay.AddDays(-1), purpose, prevMax, prevLast, ct);
                            carried = $"; {readDay.AddDays(-1):yyyy-MM-dd} = {prevMax} m3";
                        }
                        _log.LogInformation(
                            "Heating gas {Purpose}: boiler reset accepted at {Boiler} boiler time ({Local} local): {Day} = {Received} m3 (stored highest {Max} m3){Carried} (feature timestamp {FeatureTs})",
                            purpose, Local(now, boilerTz), Local(now, _tz), decision.TargetDay.ToString("yyyy-MM-dd"), received, storedMax, carried, featureTs);
                    }
                }
            }
            // A row whose every value went to the previous day (stale) or to a reset target is not written.
            if (row.GasHeatingMaxM3 is not null || row.GasDhwMaxM3 is not null || row.HeatHeatingKwh is not null || row.HeatDhwKwh is not null
                || row.PowerHeatingKwh is not null || row.PowerDhwKwh is not null)
                rows.Add(row);
        }
        await repo.UpsertDailyAsync(rows, ct);
    }

    /// <summary>The repair of the current boiler day (<see cref="BoilerClock.RepairCurrentDay"/>), once per boiler day, 30 minutes after its midnight.</summary>
    private async Task RepairOnceADayAsync(IHeatingRepository repo, Guid deviceId, DateTimeOffset now, TimeZoneInfo boilerTz, CancellationToken ct)
    {
        var today = BoilerClock.DayOf(now, boilerTz);
        if (_lastRepairDay == today || now - BoilerClock.MidnightUtc(today, boilerTz) <= BoilerClock.RepairAfterMidnight) return;
        var rows = await repo.ListDailyAsync(deviceId, today.AddDays(-1), today, ct);
        var fixes = BoilerClock.RepairCurrentDay(rows.FirstOrDefault(r => r.Day == today.AddDays(-1)), rows.FirstOrDefault(r => r.Day == today), now, boilerTz);
        foreach (var f in fixes)
        {
            await repo.SetDailyGasAsync(deviceId, f.Day.AddDays(-1), f.Purpose, f.NewPreviousMaxM3, f.NewPreviousLastM3, ct);
            await repo.SetDailyGasAsync(deviceId, f.Day, f.Purpose, f.CurrentLastM3, f.CurrentLastM3, ct);
            _log.LogInformation(
                "Heating gas {Purpose}: repaired {Day} (highest {Max} m3 > last {Last} m3 held the previous day's total): {Previous} = highest {PrevMax} m3 / last {PrevLast} m3 (was {OldPrevMax} / {OldPrevLast}), the day keeps {Kept} m3",
                f.Purpose, f.Day.ToString("yyyy-MM-dd"), f.CurrentMaxM3, f.CurrentLastM3, f.Day.AddDays(-1).ToString("yyyy-MM-dd"),
                f.NewPreviousMaxM3, f.NewPreviousLastM3, f.PreviousMaxM3, f.PreviousLastM3, f.CurrentLastM3);
        }
        // The same day also holds yesterday's heat produced and electricity (kept as the day's highest).
        if (fixes.Count > 0 && rows.FirstOrDefault(r => r.Day == today) is { } current)
        {
            foreach (var k in BoilerClock.RepairCurrentDayKwh(rows.FirstOrDefault(r => r.Day == today.AddDays(-1)), current))
            {
                await repo.SetDailyKwhAsync(deviceId, k.Day.AddDays(-1), k.Kind, k.Purpose, k.NewPreviousKwh, ct);
                await repo.SetDailyKwhAsync(deviceId, k.Day, k.Kind, k.Purpose, null, ct);
                _log.LogInformation(
                    "Heating {Kind} {Purpose}: repaired {Day} ({Current} kWh = the previous day's {PreviousValue} kWh): {Previous} = {NewPrevious} kWh, the day is filled again by the next reading",
                    k.Kind, k.Purpose, k.Day.ToString("yyyy-MM-dd"), k.CurrentKwh, k.PreviousKwh, k.Day.AddDays(-1).ToString("yyyy-MM-dd"), k.NewPreviousKwh);
            }
        }
        _lastRepairDay = today;
    }

    /// <summary>
    /// The totals are always the ones computed from the database (Σ highest of the closed boiler days + last value of
    /// today); a field lower than the previous one (a correction, a repair) is logged at Warning once per field and
    /// boiler day. The <c>_total</c> counters themselves never go down (<c>IncTo</c>).
    /// </summary>
    private void WarnIfLower(HeatingEnergyTotals? previous, HeatingEnergyTotals current, DateOnly boilerDay)
    {
        if (previous is null) return;
        void Check(string field, double old, double now)
        {
            if (now < old - 1e-9 && _loggedTotalDrops.Add((field, boilerDay)))
                _log.LogWarning("Heating total {Field} dropped from {Previous} to {Current} on {Day} (stored days changed); the /metrics counter waits until the sum passes {Previous} again (logged once a day)",
                    field, old, now, boilerDay.ToString("yyyy-MM-dd"), old);
        }
        Check("gas heating m3", previous.GasHeatingM3, current.GasHeatingM3);
        Check("gas dhw m3", previous.GasDhwM3, current.GasDhwM3);
        Check("heat heating kWh", previous.HeatHeatingKwh, current.HeatHeatingKwh);
        Check("heat dhw kWh", previous.HeatDhwKwh, current.HeatDhwKwh);
        Check("power heating kWh", previous.PowerHeatingKwh, current.PowerHeatingKwh);
        Check("power dhw kWh", previous.PowerDhwKwh, current.PowerDhwKwh);
    }

    private static string Local(DateTimeOffset utc, TimeZoneInfo tz) => TimeZoneInfo.ConvertTime(utc, tz).ToString("yyyy-MM-dd HH:mm:ss");

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
