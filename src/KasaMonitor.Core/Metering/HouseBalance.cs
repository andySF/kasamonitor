using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Metering;

/// <summary>"Restul casei": what the main meter measures beyond the plugs that are online right now.</summary>
public static class HouseBalance
{
    /// <summary>Σ power (W) of the online plugs whose role counts towards totals (<see cref="PlugRole.Plug"/>).</summary>
    public static double PlugsW(IEnumerable<PlugLiveState> states) => states
        .Where(s => PlugRoles.CountsInTotals(s.Role) && s.IsOnline && s.LastReading is not null)
        .Sum(s => s.LastReading!.PowerMw / 1000.0);

    /// <summary>Main meter power minus the online plugs; null without a main meter reading.</summary>
    public static double? RestW(double? mainW, IEnumerable<PlugLiveState> states) => mainW is { } m ? m - PlugsW(states) : null;

    /// <summary>The main meter's power: the 1-second stream when its sample is at most <paramref name="maxAge"/> old, else the 10 s poll.</summary>
    public static double? MainW(PlugLiveState? polled, LiveSample? streamed, DateTimeOffset nowUtc, TimeSpan maxAge)
    {
        if (streamed is not null && nowUtc - streamed.TsUtc <= maxAge) return streamed.PowerW;
        return polled is { IsOnline: true, LastReading: { } r } ? r.PowerMw / 1000.0 : null;
    }
}
