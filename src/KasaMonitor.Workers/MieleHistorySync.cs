using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Miele;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// Reads the program history of every appliance not archived (<c>GET /v1/devices/{id}/lastUsedPrograms</c>, at most the
/// last five runs; one call per appliance) and unites it with the observed cycles (<see cref="MieleRecorder.MergeHistoryAsync"/>).
/// When: <see cref="MieleHistorySchedule"/> — after the first read of the appliances after start or after the account
/// was connected, then every 6 hours, and 2 minutes after an observed cycle ended. The energy profile is asked for
/// (<c>calculateEnergyMonitoring=true</c>); an appliance that answers 400/404 to it is logged once and read without it
/// from then on. 429/503: logged, nothing is called before <c>Retry-After</c> (else 1 hour). 403 = the appliance
/// statistics consent is missing in the Miele app (logged once per appliance, shown on /aparate). No other call.
/// </summary>
public sealed class MieleHistorySync : BackgroundService
{
    /// <summary>The loop re-checks the schedule at least this often.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    private readonly MieleAuthService _auth;
    private readonly IMieleClient _client;
    private readonly MieleRecorder _recorder;
    private readonly MieleState _state;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<MieleHistorySync> _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly HashSet<string> _noEnergyMonitoring = new(StringComparer.Ordinal);
    private readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);

    /// <param name="wait">Test hook replacing the wait of the loop (default: the delay, cut short by a read of the appliances or a cycle end).</param>
    public MieleHistorySync(MieleAuthService auth, IMieleClient client, MieleRecorder recorder, MieleState state, IServiceScopeFactory scopes,
        ILogger<MieleHistorySync> log, Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _auth = auth;
        _client = client;
        _recorder = recorder;
        _state = state;
        _scopes = scopes;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _wait = wait ?? (async (d, c) => await _wake.WaitAsync(d, c));
        _recorder.DevicesRead += now =>
        {
            Schedule.DevicesRead(now);
            Wake();
        };
        _recorder.CycleClosed += (_, now) =>
        {
            Schedule.CycleEnded(now);
            Wake();
        };
        _auth.Connected += Schedule.Connected;
    }

    public MieleHistorySchedule Schedule { get; } = new();

    /// <summary>Appliances that refused <c>calculateEnergyMonitoring=true</c> (read without it).</summary>
    public IReadOnlyCollection<string> WithoutEnergyMonitoring => _noEnergyMonitoring;

    private void Wake()
    {
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
            await RunDueAsync(ct);
            var next = Schedule.NextDue;
            var delay = next is { } d ? d - _clock() : MaxWait;
            if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);
            if (delay > MaxWait) delay = MaxWait;
            try { await _wait(delay, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Reads the history when it is due. Returns whether a read was attempted.</summary>
    public async Task<bool> RunDueAsync(CancellationToken ct)
    {
        var now = _clock();
        if (!Schedule.IsDue(now)) return false;
        try
        {
            await SyncAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Unexpected (e.g. database): never retried before the backoff.
            Schedule.Throttled(now, null);
            _log.LogWarning(ex, "Miele: program history read failed; next attempt in {Delay}", MieleApi.HistoryBackoff);
            Publish("Istoricul Miele nu a putut fi citit: " + ex.Message);
        }
        return true;
    }

    /// <summary>One read of the history of every appliance not archived, merged into the cycles.</summary>
    public async Task SyncAsync(CancellationToken ct)
    {
        var now = _clock();
        IReadOnlyList<MieleDevice> devices;
        using (var scope = _scopes.CreateScope())
            devices = await scope.ServiceProvider.GetRequiredService<IMieleRepository>().ListDevicesAsync(includeArchived: false, ct);

        string? message = null;
        foreach (var d in devices)
        {
            string json;
            try
            {
                json = await ReadAsync(d, ct);
            }
            catch (MieleDisconnectedException ex)
            {
                Schedule.WaitForConnection();
                Publish(ex.Message);
                return;
            }
            catch (MieleApiException ex) when (ex.StatusCode is 429 or 503)
            {
                Schedule.Throttled(now, ex.RetryAfter);
                var wait = ex.RetryAfter is { } r && r > TimeSpan.Zero ? r : MieleApi.HistoryBackoff;
                _log.LogWarning("Miele: program history limited by the API ({Reason}); next call in {Wait}", ex.Message, wait);
                Publish("Miele a limitat cererile pentru istoric (HTTP " + ex.StatusCode + "); următoarea încercare peste " + Minutes(wait) + ".");
                return;
            }
            catch (MieleApiException ex) when (ex.StatusCode == 403)
            {
                LogOnce(d.Id + ":403", "Miele {Alias}: program history refused ({Reason}); the appliance statistics consent must be granted in the Miele app", d.Alias, ex.Message);
                message = "Istoricul pentru " + d.Alias + " cere acordul „statistici aparat” în aplicația Miele (HTTP 403).";
                continue;
            }
            catch (MieleApiException ex) when (ex.StatusCode is 400 or 404)
            {
                LogOnce(d.Id + ":" + ex.StatusCode, "Miele {Alias}: no program history for this appliance ({Reason})", d.Alias, ex.Message);
                continue;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is MieleException or HttpRequestException or TimeoutException or IOException
                                           or TaskCanceledException)
            {
                Schedule.Throttled(now, null);
                _log.LogWarning("Miele {Alias}: program history read failed ({Reason}); next attempt in {Delay}", d.Alias, ex.Message, MieleApi.HistoryBackoff);
                Publish("Istoricul Miele nu a putut fi citit (" + ex.Message + "); următoarea încercare peste " + Minutes(MieleApi.HistoryBackoff) + ".");
                return;
            }

            IReadOnlyList<MieleProgramRun> runs;
            try
            {
                runs = MieleLastUsedProgramsParser.Parse(json);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _log.LogWarning("Miele {Alias}: program history answer is not JSON ({Reason})", d.Alias, ex.Message);
                continue;
            }
            var plan = await _recorder.MergeHistoryAsync(d.Id, runs, ct);
            _log.LogDebug("Miele {Alias}: {Runs} runs in the API history ({Updated} updated, {Added} added, {Deferred} deferred)",
                d.Alias, runs.Count, plan.Updates.Count, plan.Inserts.Count, plan.Deferred);
        }
        Schedule.Synced(now);
        Publish(message);
    }

    /// <summary>With the energy profile unless the appliance refused it (400/404 → logged once, then without it).</summary>
    private async Task<string> ReadAsync(MieleDevice d, CancellationToken ct)
    {
        if (!_noEnergyMonitoring.Contains(d.Id))
        {
            try
            {
                return await _auth.WithTokenAsync(t => _client.GetLastUsedProgramsJsonAsync(t, d.Id, true, ct), ct);
            }
            catch (MieleApiException ex) when (ex.StatusCode is 400 or 404)
            {
                _noEnergyMonitoring.Add(d.Id);
                _log.LogWarning("Miele {Alias}: calculateEnergyMonitoring not accepted ({Reason}); the history is read without the energy profile",
                    d.Alias, ex.Message);
            }
        }
        return await _auth.WithTokenAsync(t => _client.GetLastUsedProgramsJsonAsync(t, d.Id, false, ct), ct);
    }

    private void LogOnce(string key, string message, params object?[] args)
    {
        if (!_loggedOnce.Add(key)) return;
#pragma warning disable CA2254 // the templates are constants of this class
        _log.LogWarning(message, args);
#pragma warning restore CA2254
    }

    private void Publish(string? message) => _state.SetHistory(Schedule.LastSyncUtc, message);

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 90 ? Math.Round(t.TotalHours) + " h" : Math.Max(1, Math.Round(t.TotalMinutes)) + " min";
}
