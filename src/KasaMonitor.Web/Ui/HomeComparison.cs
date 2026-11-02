using KasaMonitor.Core.Costing;
using KasaMonitor.Workers;

namespace KasaMonitor.Web.Ui;

/// <summary>
/// The „Energie” card of the home page in numbers: the period's electricity, gas and cost, and what each is compared with.
/// <see cref="CurrentKwh"/> is what the Δ uses (for today: the live sum, the same source as yesterday's); <see cref="Kwh"/>
/// is the shown total. A cost is null when a component that should exist is missing (<see cref="EnergyPrices.Total"/>), so
/// a Δ of cost exists only between two complete totals. <see cref="Partial"/>: today vs yesterday has a gap in the stream.
/// </summary>
public sealed record EnergyComparison(
    double? Kwh, double? M3, decimal? ElectricityRon, decimal? GasRon, decimal? CostRon,
    double? CurrentKwh, decimal? CurrentCostRon,
    double? PreviousKwh, double? PreviousM3, decimal? PreviousCostRon, double? WholePreviousKwh,
    bool SameTime, bool InProgress, bool Partial, string Versus, string PreviousName);

/// <summary>Builds <see cref="EnergyComparison"/> (pure: tested without the page).</summary>
public static class HomeComparison
{
    public static EnergyComparison Build(HomePeriod? d, bool isToday, EnergyPrices prices)
    {
        var kind = d?.Period.Kind ?? Core.Reports.ReportGranularity.Day;
        var names = PeriodNames.For(kind);
        var same = isToday ? d?.SameTime : null;
        var inProgress = !isToday && d?.InProgress == true;
        var partial = same?.Partial == true;
        var kwh = d?.House?.HouseTotalKwh;
        var m3 = d?.Gas?.TotalM3;
        var current = same is not null ? same.TodayKwh : kwh;
        var prevKwh = same is not null ? (partial ? null : same.YesterdaySameTimeKwh)
            : inProgress ? d?.PreviousSameFractionKwh : d?.House?.PreviousHouseTotalKwh;
        var prevM3 = same is not null ? same.Gas?.YesterdaySameTimeM3
            : inProgress ? d?.PreviousSameFractionGasM3 : d?.Gas?.PreviousTotalM3;
        var previousName = same is not null ? "ieri" : names.Previous;
        var versus = same is not null ? "față de ieri la aceeași oră"
            : inProgress ? "față de aceeași parte din " + names.Previous
            : names.Versus;
        return new EnergyComparison(
            kwh, m3, prices.Electricity(kwh), prices.Gas(m3), prices.Total(kwh, m3),
            current, prices.Total(current, m3),
            prevKwh, prevM3, prices.Total(prevKwh, prevM3), inProgress ? d?.House?.PreviousHouseTotalKwh : null,
            same is not null, inProgress, partial, versus, previousName);
    }
}
