using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Miele;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

/// <summary>
/// Turns the appliance states read from Miele (SSE devices events and GET /v1/devices) into the stored data: the
/// device row (ident, last seen), a state snapshot on change (and at most every 5 minutes otherwise), the cycles
/// (<see cref="MieleCycleTracker"/>; at the end the linked plug's energy over the cycle), the timeline of the NILM
/// ground truth, and what the UI and /metrics show (<see cref="MieleState"/>). One caller at a time (the worker);
/// the UI only calls <see cref="ReloadDeviceAsync"/>, which is serialized with the rest.
/// </summary>
public sealed class MieleRecorder
{
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly MieleState _state;
    private readonly ILogger<MieleRecorder> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, MieleCycleTracker> _trackers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MieleStateSnapshot> _lastSnapshot = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (MieleIdent? Ident, DateTimeOffset At)> _lastTouch = new(StringComparer.Ordinal);
    private bool _loaded;

    public MieleRecorder(IServiceScopeFactory scopes, MieleState state, ILogger<MieleRecorder> log)
    {
        _scopes = scopes;
        _state = state;
        _log = log;
    }

    /// <summary>After a restart: the stored appliances, their open cycles, counts, last cycle and last state.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            using var scope = _scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMieleRepository>();
            var open = (await repo.ListOpenCyclesAsync(ct)).GroupBy(c => c.DeviceId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.StartUtc).Last());
            foreach (var d in await repo.ListDevicesAsync(includeArchived: true, ct))
            {
                if (open.TryGetValue(d.Id, out var o)) _trackers[d.Id] = new MieleCycleTracker(d.Id, o);
                var last = await repo.LatestSnapshotAsync(d.Id, ct);
                if (last is not null)
                {
                    _lastSnapshot[d.Id] = last;
                    _state.RecordStatus(d.Id, last.TsUtc, last.Values.StatusRaw, last.Values.PhaseText);
                }
                await PublishAsync(repo, d, last?.Values, last?.TsUtc, ct);
            }
            _loaded = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Processes a devices payload (SSE data or GET /v1/devices). Returns how many appliances it held.</summary>
    public async Task<int> ProcessAsync(string json, DateTimeOffset nowUtc, CancellationToken ct)
    {
        var states = MieleStateParser.Parse(json);
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var s in states)
                await ProcessDeviceAsync(s, nowUtc, ct);
        }
        finally
        {
            _lock.Release();
        }
        _state.MarkRead(nowUtc);
        return states.Count;
    }

    /// <summary>Closes the cycles of appliances not Running for 30 minutes (also when they went silent).</summary>
    public async Task TickAsync(DateTimeOffset nowUtc, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            foreach (var (deviceId, tracker) in _trackers.ToList())
            {
                var change = tracker.Tick(nowUtc);
                if (change.Closed is null) continue;
                using var scope = _scopes.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IMieleRepository>();
                var device = (await repo.ListDevicesAsync(includeArchived: true, ct)).FirstOrDefault(d => d.Id == deviceId);
                if (device is null) continue;
                await ApplyAsync(scope.ServiceProvider, repo, device, tracker, change, ct);
                var current = _state.Get(deviceId);
                await PublishAsync(repo, device, current?.Values, current?.StateUtc, ct);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>After the alias, plug or archive flag changed in the UI: reload the device and publish it.</summary>
    public async Task ReloadDeviceAsync(string deviceId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var scope = _scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMieleRepository>();
            var device = (await repo.ListDevicesAsync(includeArchived: true, ct)).FirstOrDefault(d => d.Id == deviceId);
            if (device is null) return;
            var current = _state.Get(deviceId);
            await PublishAsync(repo, device, current?.Values, current?.StateUtc, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task ProcessDeviceAsync(MieleDeviceState s, DateTimeOffset now, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMieleRepository>();
        var device = _state.Get(s.DeviceId)?.Device;
        // The device row is written when new, when its ident changed, and at most every 5 minutes for LastSeenUtc.
        if (device is null || s.Ident is not null && !Equals(_lastTouch.GetValueOrDefault(s.DeviceId).Ident, s.Ident)
            || !_lastTouch.TryGetValue(s.DeviceId, out var touched) || now - touched.At >= TouchEvery)
        {
            device = await repo.UpsertDeviceAsync(s.DeviceId, s.Ident, now, ct);
            _lastTouch[s.DeviceId] = (s.Ident ?? _lastTouch.GetValueOrDefault(s.DeviceId).Ident, now);
        }
        if (device.IsArchived || !s.HasState)
        {
            var known = _state.Get(s.DeviceId);
            await PublishAsync(repo, device, known?.Values, known?.StateUtc, ct);
            return;
        }

        var values = MieleStateValues.From(s, device.TypeRaw);
        _lastSnapshot.TryGetValue(device.Id, out var previous);
        if (MieleSnapshotRule.ShouldWrite(previous, values, now))
        {
            var snap = new MieleStateSnapshot(now, device.Id, values);
            await repo.AddSnapshotAsync(snap, ct);
            _lastSnapshot[device.Id] = snap;
        }
        _state.RecordStatus(device.Id, now, values.StatusRaw, values.PhaseText);

        if (!_trackers.TryGetValue(device.Id, out var tracker)) _trackers[device.Id] = tracker = new MieleCycleTracker(device.Id);
        var change = tracker.Observe(now, values);
        await ApplyAsync(scope.ServiceProvider, repo, device, tracker, change, ct);
        await PublishAsync(repo, device, values, now, ct);
    }

    private async Task ApplyAsync(IServiceProvider sp, IMieleRepository repo, MieleDevice device, MieleCycleTracker tracker, MieleCycleChange change, CancellationToken ct)
    {
        if (change.Opened is { } opened)
        {
            var stored = await repo.AddCycleAsync(opened with { DeviceId = device.Id }, ct);
            tracker.SetId(stored.Id);
            _log.LogInformation("Miele {Alias}: cycle started ({Program})", device.Alias, opened.Program ?? "?");
        }
        if (change.Updated is { } updated)
        {
            if (updated.Id == 0) tracker.SetId((await repo.AddCycleAsync(updated with { DeviceId = device.Id }, ct)).Id);
            else await repo.UpdateCycleAsync(updated with { DeviceId = device.Id }, ct);
        }
        if (change.Closed is { } closed)
        {
            closed = closed with { DeviceId = device.Id };
            if (device.PlugId is { } plug && closed.EndUtc is { } end)
                closed = closed with { PlugEnergyWh = await PlugEnergyAsync(sp, plug, closed.StartUtc, end, ct) };
            if (closed.Id == 0) await repo.AddCycleAsync(closed, ct);
            else await repo.UpdateCycleAsync(closed, ct);
            _log.LogInformation("Miele {Alias}: cycle ended ({Reason}, {Minutes:0} min, API {Kwh} kWh, plug {Wh} Wh)", device.Alias,
                closed.EndReason, closed.Duration?.TotalMinutes ?? 0, closed.EnergyKwh?.ToString("0.00") ?? "—", closed.PlugEnergyWh?.ToString("0") ?? "—");
        }
    }

    /// <summary>The plug's energy over the cycle: live minute aggregates, else the 10 s polls.</summary>
    private static async Task<double?> PlugEnergyAsync(IServiceProvider sp, Guid plugId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var minutes = await sp.GetRequiredService<ILiveRepository>().ListMinutesAsync(plugId, from.AddMinutes(-1), to, ct);
        if (MielePlugEnergy.FromMinutes(minutes, from, to) is { } wh) return wh;
        var samples = await sp.GetRequiredService<ISampleRepository>().ListByPlugAsync(plugId, from - MielePlugEnergy.MaxHold, to, ct);
        return MielePlugEnergy.FromSamples(samples, from, to);
    }

    private async Task PublishAsync(IMieleRepository repo, MieleDevice device, MieleStateValues? values, DateTimeOffset? stateUtc, CancellationToken ct)
    {
        var count = await repo.CountClosedCyclesAsync(device.Id, ct);
        var recent = await repo.ListCyclesAsync(device.Id, 2, ct);
        var open = _trackers.TryGetValue(device.Id, out var t) ? t.Open : null;
        var last = recent.FirstOrDefault(c => !c.IsOpen);
        _state.Publish(new MieleApplianceLive(device, values, stateUtc, open, last, count));
    }
}
