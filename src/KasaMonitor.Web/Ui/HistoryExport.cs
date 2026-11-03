using KasaMonitor.Core.History;
using KasaMonitor.Core.Reports;
using KasaMonitor.Workers;

namespace KasaMonitor.Web.Ui;

/// <summary>
/// <c>GET /history/export.csv?section=…&amp;p=…&amp;d=…</c>: the table of one section of /history for the period of the
/// address (<see cref="PeriodQuery"/>; default the current month), from the same cached data and the same table builders
/// (<see cref="HistoryTables"/>) as the page.
/// </summary>
public static class HistoryExport
{
    /// <summary>The table of a section, or null for an unknown section.</summary>
    public static async Task<HistoryTable?> TableAsync(string section, ReportPeriod period, bool compare, Guid? plug, HomeService home,
        HistoryService history, IKasaSettingsService kasa, IHeatingSettingsService heating, DateTimeOffset nowUtc, CancellationToken ct,
        KasaMonitor.Workers.Mqtt.GenericHistoryService? generic = null)
    {
        var tz = history.TimeZone;
        var gas = section is HistorySections.Gas or HistorySections.Costs or HistorySections.DegreeDays || section == HistorySections.Electricity
            ? await history.GetGasAsync(period, ct) : null;
        var prices = await HistoryPrices.LoadAsync(kasa, heating, gas is not null, ct);
        switch (section)
        {
            case HistorySections.Electricity:
                return (await home.GetAsync(period, ct)).House is { } house
                    ? HistoryTables.Electricity(house, prices, nowUtc, compare, plug) : HistoryTable.Empty;
            case HistorySections.Gas:
                return gas is null ? HistoryTable.Empty : HistoryTables.Gas(gas, prices, nowUtc, compare);
            case HistorySections.MieleCycles:
                return HistoryTables.MieleCycles(await history.GetMieleAsync(period, ct), prices, tz);
            case HistorySections.MielePrograms:
                return HistoryTables.MielePrograms(await history.GetMieleAsync(period, ct), prices);
            case HistorySections.Consumers:
                return HistoryTables.Consumers((await history.GetDetectedAsync(period, ct)).Breakdown, prices);
            case HistorySections.Detected:
                return HistoryTables.Detected(await history.GetDetectedAsync(period, ct), prices);
            case HistorySections.Generic:
                return generic is null ? HistoryTable.Empty : HistoryTables.Generic(await generic.GetAsync(period, tz, ct), period.Kind, nowUtc, compare);
            case HistorySections.Climate:
                return HistoryTables.Climate(await history.GetClimateAsync(period, ct), period.Kind, nowUtc, compare);
            case HistorySections.DegreeDays:
                return HistoryTables.DegreeDays(await history.GetClimateAsync(period, ct), prices);
            case HistorySections.Costs:
                var h = (await home.GetAsync(period, ct)).House;
                return HistoryTables.Costs(h, gas, prices, period.Buckets(tz), period.Kind, nowUtc, compare);
            case HistorySections.Events:
                return HistoryTables.Events((await history.GetEventsAsync(period, ct)).Events, tz);
            default:
                return null;
        }
    }

    /// <summary>„istoric-curent-luna-2026-11.csv”.</summary>
    public static string FileName(string section, ReportPeriod period) =>
        "istoric-" + section + "-" + PeriodQuery.Format(period).Replace("p=", "").Replace("&d=", "-").Replace("&de=", "-").Replace("&pana=", "_") + ".csv";
}
