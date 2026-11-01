using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.History;

/// <summary>One plug's daily energy aligned to <see cref="AllPlugsDailyEnergy.Days"/> (0 where the plug has no row).</summary>
public sealed record PlugDailySeries(Guid PlugId, string Alias, string Color, IReadOnlyList<long> DailyWh, long TotalWh);

/// <summary>
/// Daily energy of all polled plugs, ready for a stacked bar chart ("Toate" on /history), plus the main meter
/// ("Casă") as its own series that is never added to the plug totals.
/// </summary>
public sealed record AllPlugsDailyEnergy(
    IReadOnlyList<DateOnly> Days,
    IReadOnlyList<PlugDailySeries> Series,
    IReadOnlyList<long> DayTotalsWh,
    long TotalWh,
    PlugDailySeries? House = null)
{
    /// <summary>Color of the "Casă" series (not in <see cref="PlugColors.Palette"/>, so it never matches a plug).</summary>
    public const string HouseColor = "#6C757D";

    /// <summary>
    /// Sums the rows per plug and day, keeps only polled plugs (<see cref="Plug.IsPolled"/>: withdrawn and unsupported
    /// plugs are excluded) whose role counts towards totals (<see cref="PlugRole.Plug"/>; meters already contain the
    /// plugs behind them), and aligns every series on the days that have data for at least one included plug or for the
    /// main meter. The polled <see cref="PlugRole.MainMeter"/> becomes <see cref="House"/> (aligned on the same days,
    /// not part of <see cref="DayTotalsWh"/>/<see cref="TotalWh"/>); sub-meters are left out.
    /// Plugs without any row in the range get no series. Colors come from <see cref="PlugColors"/>.
    /// </summary>
    public static AllPlugsDailyEnergy Build(IEnumerable<Plug> plugs, IEnumerable<PlugDailyKwh> rows)
    {
        var plugList = plugs.ToList();
        var colors = PlugColors.Assign(plugList);
        var polled = plugList.Where(p => p.IsPolled && p.CountsInTotals).ToDictionary(p => p.Id);
        var main = plugList.FirstOrDefault(p => p.IsPolled && p.Role == PlugRole.MainMeter);

        var sums = new Dictionary<(Guid PlugId, DateOnly Day), long>();
        var houseSums = new Dictionary<DateOnly, long>();
        foreach (var r in rows)
        {
            var day = new DateOnly(r.Day.Year, r.Day.Month, r.Day.Day);
            if (main is not null && r.PlugId == main.Id)
            {
                houseSums[day] = houseSums.GetValueOrDefault(day) + r.Day.EnergyWh;
                continue;
            }
            if (!polled.ContainsKey(r.PlugId)) continue;
            var key = (r.PlugId, day);
            sums[key] = sums.GetValueOrDefault(key) + r.Day.EnergyWh;
        }

        var days = sums.Keys.Select(k => k.Day).Concat(houseSums.Keys).Distinct().Order().ToList();
        var series = polled.Values
            .Where(p => sums.Keys.Any(k => k.PlugId == p.Id))
            .OrderBy(p => p.Alias, StringComparer.CurrentCultureIgnoreCase)
            .Select(p =>
            {
                var daily = days.Select(d => sums.GetValueOrDefault((p.Id, d))).ToList();
                return new PlugDailySeries(p.Id, p.Alias, colors[p.Id], daily, daily.Sum());
            })
            .ToList();
        var dayTotals = days.Select((_, i) => series.Sum(s => s.DailyWh[i])).ToList();
        PlugDailySeries? house = null;
        if (main is not null && houseSums.Count > 0)
        {
            var daily = days.Select(d => houseSums.GetValueOrDefault(d)).ToList();
            house = new PlugDailySeries(main.Id, "Casă", HouseColor, daily, daily.Sum());
        }
        return new AllPlugsDailyEnergy(days, series, dayTotals, dayTotals.Sum(), house);
    }
}

/// <summary>
/// Fixed categorical palette for plugs: Okabe–Ito (the most colour-vision-deficiency-robust off-the-shelf set),
/// reordered so neighbours differ in hue and lightness. Colors are assigned in alias order over the polled plugs,
/// so a plug keeps its color between loads and ranges (it changes only when plugs are added, renamed or withdrawn).
/// More than 8 plugs reuse the palette.
/// </summary>
public static class PlugColors
{
    public static readonly IReadOnlyList<string> Palette = new[]
    {
        "#0072B2", // blue
        "#E69F00", // orange
        "#009E73", // bluish green
        "#CC79A7", // reddish purple
        "#56B4E9", // sky blue
        "#D55E00", // vermillion
        "#F0E442", // yellow
        "#000000", // black
    };

    public static IReadOnlyDictionary<Guid, string> Assign(IEnumerable<Plug> plugs) =>
        plugs.Where(p => p.IsPolled)
            .OrderBy(p => p.Alias, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(p => p.Mac, StringComparer.Ordinal)
            .Select((p, i) => (p.Id, Color: Palette[i % Palette.Count]))
            .ToDictionary(x => x.Id, x => x.Color);
}
