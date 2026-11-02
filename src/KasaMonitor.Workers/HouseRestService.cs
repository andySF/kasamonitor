using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers;

/// <summary>
/// "Restul casei" now („Prize” page „Casă” card, /contor): the main meter vs the plugs over the last whole minute, from
/// the live stream (<see cref="HouseBalance.LastWholeMinute"/>) or, without a live sample of the main meter in that
/// minute (stream off, channel not streamed, WebSocket down, first minute after start), from the means of the 10 s
/// polled samples of the same minute (<see cref="HouseBalance.FromPolledMinute"/>). Computed once per minute and shared
/// by every page and circuit.
/// </summary>
public sealed class HouseRestService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILiveStreamBroadcaster _stream;
    private readonly IPlugStateBroadcaster _states;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (DateTimeOffset Minute, RestBalance? Value)? _cache;

    public HouseRestService(IServiceScopeFactory scopeFactory, ILiveStreamBroadcaster stream, IPlugStateBroadcaster states,
        Func<DateTimeOffset>? clock = null)
    {
        _scopeFactory = scopeFactory;
        _stream = stream;
        _states = states;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The cached value of the current minute without waiting (null before the first computation).</summary>
    public RestBalance? Cached => _cache is { } c && c.Minute == HouseBalance.LastWholeMinuteStart(_clock()) ? c.Value : null;

    /// <summary>The balance of the last whole minute; null without a main meter or without any sample of it in that minute.</summary>
    public async Task<RestBalance?> GetAsync(CancellationToken ct)
    {
        var now = _clock();
        var minute = HouseBalance.LastWholeMinuteStart(now);
        if (_cache is { } c && c.Minute == minute) return c.Value;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } c2 && c2.Minute == minute) return c2.Value;
            var value = await ComputeAsync(now, minute, ct);
            _cache = (minute, value);
            return value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<RestBalance?> ComputeAsync(DateTimeOffset now, DateTimeOffset minute, CancellationToken ct)
    {
        var states = _states.Snapshot().Values.ToList();
        var main = states.FirstOrDefault(s => s.Role == PlugRole.MainMeter);
        if (main is null) return null;
        IEnumerable<LiveSample> Recent(Guid id) => _stream.Recent(id);
        if (HouseBalance.LastWholeMinute(now, main.PlugId, Recent, states) is { } live) return live;

        var ids = states.Where(s => s.Role == PlugRole.MainMeter || PlugRoles.CountsInTotals(s.Role)).Select(s => s.PlugId).ToList();
        using var scope = _scopeFactory.CreateScope();
        var averages = await scope.ServiceProvider.GetRequiredService<ISampleRepository>()
            .AveragePowerByPlugAsync(ids, minute, minute.AddMinutes(1), ct);
        return HouseBalance.FromPolledMinute(minute, main.PlugId, averages, Recent, states);
    }
}
