namespace KasaMonitor.Core.Heating;

/// <summary>An interval with the energy saving (Eco) of the active program on; <see cref="Reason"/> as the boiler sent it.</summary>
public sealed record EcoInterval(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Reason, bool Ongoing);

/// <summary>The Eco intervals of a list of boiler readings (the „Eco” row of the burner timeline on /centrala).</summary>
public static class EcoTimeline
{
    /// <summary>
    /// From the first reading with <c>EcoActive = true</c> to the next reading without it, as a burner run is drawn. A missing
    /// value (null: readings before 0.15.0, or the boiler did not send it) ends an interval too. One still on at the last
    /// reading runs one <paramref name="cadence"/> further, never past <paramref name="nowUtc"/>. The reason is the one of
    /// the first reading of the interval. <paramref name="readings"/> are in time order.
    /// </summary>
    public static IReadOnlyList<EcoInterval> Build(IReadOnlyList<HeatingReading> readings, TimeSpan cadence, DateTimeOffset nowUtc)
    {
        var list = new List<EcoInterval>();
        DateTimeOffset? start = null;
        string? reason = null;
        foreach (var r in readings)
        {
            if (r.EcoActive == true)
            {
                if (start is null) { start = r.TsUtc; reason = r.EcoReason; }
                continue;
            }
            if (start is { } s) list.Add(new EcoInterval(s, r.TsUtc, reason, false));
            start = null;
        }
        if (start is { } open)
        {
            var end = readings[^1].TsUtc + cadence;
            list.Add(new EcoInterval(open, end < nowUtc ? end : nowUtc, reason, true));
        }
        return list;
    }
}
