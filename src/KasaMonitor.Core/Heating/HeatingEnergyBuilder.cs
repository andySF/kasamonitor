namespace KasaMonitor.Core.Heating;

/// <summary>
/// Turns the energy features of one snapshot into daily rows and period values. Daily history is built by
/// KasaMonitor: each poll writes the summary's <c>currentDay</c> on the boiler day of the reading (<c>tz</c> of
/// <see cref="Days"/> = the boiler's zone, <see cref="BoilerClock"/>; the repository keeps
/// the day's highest and last value), so when the day is over its highest value stays. Boilers with a <c>day[]</c> series (day[0] = today) fill every day of
/// the series instead. Gas is stored in m³ (a kWh answer is converted with the calorific factor).
/// </summary>
public static class HeatingEnergyBuilder
{
    private enum Column { GasHeating, GasDhw, HeatHeating, HeatDhw, PowerHeating, PowerDhw }

    public static IReadOnlyList<HeatingEnergyDay> Days(Guid deviceId, HeatingSnapshot s, TimeZoneInfo tz, double kwhPerM3, EnergyUnit assumedGasUnit)
    {
        var rows = new SortedDictionary<DateOnly, double?[]>();
        var sources = new Dictionary<DateOnly, string>();

        void Add(Column column, EnergySummary? summary, bool gas)
        {
            if (summary is null) return;
            double Convert(double v) => gas ? HeatingEnergy.ToM3(v, summary.Unit, kwhPerM3, assumedGasUnit) : v;
            if (summary.Days is { Count: > 0 } series)
            {
                foreach (var (day, value) in HeatingEnergy.SeriesDays(series, s.ReadUtc, tz))
                    Put(day, column, Convert(value), HeatingKinds.SourceSeries);
            }
            else if (summary.CurrentDay is { } today)
            {
                Put(HeatingEnergy.DayOf(s.ReadUtc, tz), column, Convert(today), HeatingKinds.SourceSummary);
            }
        }

        void Put(DateOnly day, Column column, double value, string source)
        {
            if (!rows.TryGetValue(day, out var values)) rows[day] = values = new double?[6];
            values[(int)column] = value;
            // A series wins over a summary for the same day.
            if (!sources.TryGetValue(day, out var existing) || existing != HeatingKinds.SourceSeries) sources[day] = source;
        }

        Add(Column.GasHeating, s.GasHeating, gas: true);
        Add(Column.GasDhw, s.GasDhw, gas: true);
        Add(Column.HeatHeating, s.HeatHeating, gas: false);
        Add(Column.HeatDhw, s.HeatDhw, gas: false);
        Add(Column.PowerHeating, s.PowerHeating, gas: false);
        Add(Column.PowerDhw, s.PowerDhw, gas: false);

        return rows.Select(kv => new HeatingEnergyDay(deviceId, kv.Key, kv.Value[0], kv.Value[1], kv.Value[2], kv.Value[3], kv.Value[4],
            kv.Value[5], sources[kv.Key], GasHeatingLastM3: kv.Value[0], GasDhwLastM3: kv.Value[1])).ToList();
    }

    /// <summary>The boiler's own period values (day, last 7 days, month, last month, year, last year); gas in m³.</summary>
    public static IReadOnlyList<HeatingEnergyPeriod> Periods(Guid deviceId, HeatingSnapshot s, double kwhPerM3, EnergyUnit assumedGasUnit)
    {
        var list = new List<HeatingEnergyPeriod>();
        void Add(string kind, string purpose, EnergySummary? summary)
        {
            if (summary is null) return;
            var gas = kind == HeatingKinds.Gas;
            foreach (var (period, value) in summary.Periods())
                list.Add(new HeatingEnergyPeriod(deviceId, kind, purpose, period,
                    gas ? HeatingEnergy.ToM3(value, summary.Unit, kwhPerM3, assumedGasUnit) : value,
                    gas ? HeatingKinds.UnitM3 : HeatingKinds.UnitKwh, s.ReadUtc));
        }
        Add(HeatingKinds.Gas, HeatingKinds.Heating, s.GasHeating);
        Add(HeatingKinds.Gas, HeatingKinds.Dhw, s.GasDhw);
        Add(HeatingKinds.Heat, HeatingKinds.Heating, s.HeatHeating);
        Add(HeatingKinds.Heat, HeatingKinds.Dhw, s.HeatDhw);
        Add(HeatingKinds.Power, HeatingKinds.Heating, s.PowerHeating);
        Add(HeatingKinds.Power, HeatingKinds.Dhw, s.PowerDhw);
        return list;
    }
}
