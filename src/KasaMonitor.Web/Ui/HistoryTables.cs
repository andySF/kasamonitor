using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Costing;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Metering;
using KasaMonitor.Core.Reports;
using KasaMonitor.Workers;

namespace KasaMonitor.Web.Ui;

/// <summary>The CSV sections of /history (the <c>section=</c> of <c>/history/export.csv</c>).</summary>
public static class HistorySections
{
    public const string Electricity = "curent";
    public const string Gas = "gaz";
    public const string MieleCycles = "aparate";
    public const string MielePrograms = "aparate-programe";
    public const string Consumers = "consumatori";
    public const string Detected = "consumatori-detectati";
    public const string Climate = "clima";
    public const string DegreeDays = "clima-grade-zile";
    public const string Costs = "costuri";
    public const string Events = "evenimente";
    /// <summary>0.11: the generic devices („Alte dispozitive”).</summary>
    public const string Generic = "alte-dispozitive";
    /// <summary>0.14.0: the power over time („Putere”).</summary>
    public const string Power = "putere";

    public static readonly IReadOnlyList<string> All = new[] { Electricity, Power, Gas, MieleCycles, MielePrograms, Consumers, Detected, Climate, DegreeDays, Costs, Events, Generic };
}

/// <summary>
/// The tables of /history (single owner: the page shows them, <c>/history/export.csv</c> writes them). Numbers in the
/// Romanian format with the decimals of the pages (kWh 2 below 10 and 1 above, m³ and lei 2, °C 1), units in the headers,
/// an empty cell = no data. Bars after now are left out (the chart keeps the whole axis). Each bar of a week, month, range
/// or year links to its day (its month) on /history.
/// </summary>
public static class HistoryTables
{
    public static string BucketHeader(ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => "Ora",
        ReportGranularity.Year => "Luna",
        _ => "Ziua",
    };

    /// <summary>The address a bar drills down to: a day of a week / month / range, a month of a year; none for the hours of a day.</summary>
    public static string? DrillLink(ReportBucket b, ReportGranularity kind, bool compare, Guid? plug) => kind switch
    {
        ReportGranularity.Day => null,
        ReportGranularity.Year => PeriodQuery.History(ReportPeriod.Containing(ReportGranularity.Month, b.Day), compare, plug),
        _ => PeriodQuery.History(ReportPeriod.Containing(ReportGranularity.Day, b.Day), compare, plug),
    };

    /// <summary>
    /// The time of a bar as the CSV writes it (ISO): <c>yyyy-MM-dd HH:00</c> for an hour (the repeated hour of the autumn DST
    /// day with „ (2)”), <c>yyyy-MM</c> for a month of a year, <c>yyyy-MM-dd</c> for a day.
    /// </summary>
    public static string IsoKey(ReportBucket b, ReportGranularity kind) => kind switch
    {
        ReportGranularity.Day => b.LocalStart.ToString("yyyy-MM-dd HH:00", System.Globalization.CultureInfo.InvariantCulture) + (b.RepeatedHour ? " (2)" : ""),
        ReportGranularity.Year => b.Day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        _ => b.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>„în curs (parțial)” for the bar that holds now, otherwise empty.</summary>
    public static string InProgress(ReportBucket b, DateTimeOffset nowUtc) => b.StartUtc <= nowUtc && nowUtc < b.EndUtc ? "în curs (parțial)" : "";

    private static string Join(params string[] notes) => string.Join("; ", notes.Where(n => n.Length > 0));

    /// <summary>The marker of the rest of the house, apart from its number (a CSV cell stays numeric).</summary>
    public static string RestNote(RestKind? kind) => kind switch
    {
        RestKind.WithinMargin => "rest în marja de eroare",
        RestKind.Negative => "prizele depășesc contorul",
        RestKind.PartialData => "date parțiale",
        _ => "",
    };

    /// <summary>The bars up to now (index list): a bar that starts after now is not a row.</summary>
    public static IEnumerable<int> Elapsed(IReadOnlyList<ReportBucket> buckets, DateTimeOffset nowUtc) =>
        Enumerable.Range(0, buckets.Count).Where(i => buckets[i].StartUtc <= nowUtc);

    private static string Kwh(double? v) => v is null ? "" : ReportFormat.Kwh(v);
    private static string M3(double? v) => v is null ? "" : HomeFormat.M3(v);
    private static string Lei(decimal? v) => v is { } x ? HeatingFormat.Number((double)x, 2) : "";
    private static string Num(double? v, int decimals) => v is null ? "" : HeatingFormat.Number(v, decimals);

    // ------------------------------------------------------------------ electricity

    /// <summary>Per bar: house (meter), plugs, rest, lei of the house; with a plug filter also that plug's kWh.</summary>
    public static HistoryTable Electricity(HouseReport house, EnergyPrices prices, DateTimeOffset nowUtc, bool compare, Guid? plug)
    {
        var kind = house.Period.Kind;
        var plugSeries = plug is { } id ? house.PlugBuckets?.FirstOrDefault(p => p.PlugId == id) : null;
        var headers = new List<string> { BucketHeader(kind), "Casă (contor) kWh", "Prize kWh", "Restul casei kWh", "Casă lei" };
        if (plugSeries is not null) headers.Add(plugSeries.Alias + " kWh");
        headers.Add("Notă");
        var rows = new List<IReadOnlyList<string>>();
        var links = new List<string?>();
        var keys = new List<string>();
        foreach (var i in Elapsed(house.Buckets, nowUtc))
        {
            var b = house.Buckets[i];
            var row = new List<string>
            {
                ReportFormat.BucketRow(b, kind), Kwh(house.HouseKwh[i]), Kwh(house.PlugsKwh[i]),
                house.HouseKwh[i] is null ? "" : Kwh(house.RestKwh[i]), Lei(prices.Electricity(house.HouseKwh[i])),
            };
            if (plugSeries is not null) row.Add(Kwh(plugSeries.Kwh[i]));
            row.Add(Join(house.HouseKwh[i] is null ? "" : RestNote(house.RestKinds[i]), InProgress(b, nowUtc)));
            rows.Add(row);
            links.Add(DrillLink(b, kind, compare, plug));
            keys.Add(IsoKey(b, kind));
        }
        return new HistoryTable(headers, rows, links, keys);
    }

    // ------------------------------------------------------------------ gas

    public static HistoryTable Gas(HistoryGas g, EnergyPrices prices, DateTimeOffset nowUtc, bool compare)
    {
        var r = g.Report;
        var kind = r.Period.Kind;
        var heating = Series(r.Gas, HeatingReportBuilder.SeriesHeating);
        var dhw = Series(r.Gas, HeatingReportBuilder.SeriesDhw);
        var headers = new[] { BucketHeader(kind), "Încălzire m³", "Apă caldă m³", "Total m³", "Gaz ≈ kWh", "Gaz lei", "Căldură produsă kWh",
            "Curent centrală kWh", "Randament aparent %", "Ore de ardere", "Porniri", "Notă" };
        var rows = new List<IReadOnlyList<string>>();
        var links = new List<string?>();
        var keys = new List<string>();
        foreach (var i in Elapsed(r.Buckets, nowUtc))
        {
            var b = r.Buckets[i];
            var total = ReportMath.SumOrNull(new[] { At(heating, i), At(dhw, i) });
            var heat = ReportMath.SumOrNull(r.Heat.Series.Select(s => At(s.Values, i)));
            var power = ReportMath.SumOrNull(r.Power.Series.Select(s => At(s.Values, i)));
            var eff = HeatingEnergy.Efficiency(heat, total is { } t ? GasCost.Kwh(t, prices.GasKwhPerM3) : null);
            rows.Add(new[]
            {
                ReportFormat.BucketRow(b, kind), M3(At(heating, i)), M3(At(dhw, i)), M3(total), Kwh(total is { } t2 ? GasCost.Kwh(t2, prices.GasKwhPerM3) : null),
                Lei(prices.Gas(total)), Kwh(heat), Kwh(power), Num(eff * 100, 0), Num(g.Burner[i].Hours, 1), g.Burner[i].Starts?.ToString() ?? "",
                InProgress(b, nowUtc),
            });
            links.Add(DrillLink(b, kind, compare, null));
            keys.Add(IsoKey(b, kind));
        }
        return new HistoryTable(headers, rows, links, keys);
    }

    public static IReadOnlyList<double?> Series(HeatingKindReport k, string key) =>
        k.Series.FirstOrDefault(s => s.Key == key)?.Values ?? Array.Empty<double?>();

    private static double? At(IReadOnlyList<double?> values, int i) => i < values.Count ? values[i] : null;

    // ------------------------------------------------------------------ Miele

    public static HistoryTable MieleCycles(HistoryMiele m, EnergyPrices prices, TimeZoneInfo tz) => new(
        new[] { "Început", "Aparat", "Program", "Durată min", "Energie Miele kWh", "Energie priză kWh", "Apă L", "Lei (priză, altfel Miele)" },
        m.Cycles.Select(c => (IReadOnlyList<string>)new[]
        {
            Local(c.Cycle.StartUtc, tz), c.Appliance, c.Program, Num(c.DurationMinutes, 0), Kwh(c.MieleKwh), Kwh(c.PlugKwh), Num(c.WaterL, 0),
            Lei(prices.Electricity(c.BestKwh)),
        }).ToList());

    public static HistoryTable MielePrograms(HistoryMiele m, EnergyPrices prices) => new(
        new[] { "Aparat", "Program", "Cicluri", "Durată medie min", "Energie medie kWh", "Apă medie L", "Energie totală kWh", "Lei total" },
        m.Programs.Select(p => (IReadOnlyList<string>)new[]
        {
            p.Appliance, p.Program, p.Cycles.ToString(), Num(p.MeanMinutes, 0), Kwh(p.MeanKwh), Num(p.MeanWaterL, 0), Kwh(p.TotalKwh), Lei(prices.Electricity(p.TotalKwh)),
        }).ToList());

    // ------------------------------------------------------------------ detected appliances

    public static HistoryTable Detected(HistoryDetected d, EnergyPrices prices) => new(
        new[] { "Aparat", "Energie estimată kWh", "Lei", "Rulări", "Durată medie min", "Ora cu cele mai multe porniri" },
        d.Rows.Select(r => (IReadOnlyList<string>)new[]
        {
            r.Name, Kwh(r.Kwh), Lei(prices.Electricity(r.Kwh)), r.Runs.ToString(), Num(r.MeanMinutes, 0),
            r.Runs == 0 ? "" : $"{Array.IndexOf(r.StartsByHour.ToArray(), r.StartsByHour.Max()):00}:00",
        }).ToList());

    public static string ConsumerKindName(ConsumerKind k) => k switch
    {
        ConsumerKind.IdentifiedDetected => "identificat (detectat)",
        ConsumerKind.IdentifiedPlug => "identificat (priză)",
        ConsumerKind.OtherPlugs => "prize, aparat neidentificat",
        ConsumerKind.Unidentified => "neidentificat",
        ConsumerKind.AboveMeter => "peste contor (nu intră în total)",
        _ => "nedetectat",
    };

    /// <summary>
    /// The consumers' breakdown: one row per identified consumer, the other plugs and the anonymous signatures each also
    /// listed member by member (under their group row), „Nedetectat”; kWh, lei and the share of the total.
    /// </summary>
    public static HistoryTable Consumers(ConsumerBreakdown b, EnergyPrices prices)
    {
        string Pct(double kwh) => b.TotalKwh > 1e-9 ? Num(kwh / b.TotalKwh * 100, 1) : "";
        // „Peste contor” is not part of the meter: no share of it.
        var rows = new List<IReadOnlyList<string>>();
        foreach (var r in b.Rows)
        {
            rows.Add(new[] { r.Name, ConsumerKindName(r.Kind), Kwh(r.Kwh), Lei(prices.Electricity(r.Kwh)), r.Kind == ConsumerKind.AboveMeter ? "" : Pct(r.Kwh) });
            foreach (var m in r.Members ?? Array.Empty<ConsumerMember>())
                rows.Add(new[] { "  " + m.Name, ConsumerKindName(r.Kind) + " · membru", Kwh(m.Kwh), Lei(prices.Electricity(m.Kwh)), Pct(m.Kwh) });
        }
        rows.Add(new[] { "Total (contor)", "Σ contor pe bare; fără „peste contor”", Kwh(b.TotalKwh), Lei(prices.Electricity(b.TotalKwh)), b.TotalKwh > 1e-9 ? "100" : "" });
        return new HistoryTable(new[] { "Consumator", "Tip", "kWh", "Lei", "% din total" }, rows);
    }

    // ------------------------------------------------------------------ climate

    /// <summary>Long format: one row per bar and sensor (min / mean / max temperature and humidity).</summary>
    public static HistoryTable Climate(HistoryClimate c, ReportGranularity kind, DateTimeOffset nowUtc, bool compare)
    {
        var rows = new List<IReadOnlyList<string>>();
        var links = new List<string?>();
        var keys = new List<string>();
        foreach (var i in Elapsed(c.Buckets, nowUtc))
        {
            foreach (var s in c.Sensors)
            {
                var st = s.Buckets[i];
                if (st.AvgC is null) continue;
                rows.Add(new[]
                {
                    ReportFormat.BucketRow(c.Buckets[i], kind), s.Sensor.Name, s.Sensor.IsOutdoor ? "Afară" : s.Sensor.Room,
                    Num(st.MinC, 1), Num(st.AvgC, 1), Num(st.MaxC, 1), Num(st.MinHumidity, 0), Num(st.AvgHumidity, 0), Num(st.MaxHumidity, 0),
                    InProgress(c.Buckets[i], nowUtc),
                });
                links.Add(DrillLink(c.Buckets[i], kind, compare, null));
                keys.Add(IsoKey(c.Buckets[i], kind));
            }
        }
        return new HistoryTable(new[] { BucketHeader(kind), "Senzor", "Cameră", "T min °C", "T medie °C", "T max °C", "RH min %", "RH medie %", "RH max %", "Notă" },
            rows, links, keys);
    }

    /// <summary>
    /// „Alte dispozitive” (0.11): one row per bar and entity — min / mean / max of a measurement, Σ of a counter, the time on
    /// of an on/off — in the entity's unit and decimals.
    /// </summary>
    public static HistoryTable Generic(KasaMonitor.Workers.Mqtt.HistoryGeneric g, ReportGranularity kind, DateTimeOffset nowUtc, bool compare)
    {
        var rows = new List<IReadOnlyList<string>>();
        var links = new List<string?>();
        var keys = new List<string>();
        foreach (var i in Elapsed(g.Buckets, nowUtc))
        {
            foreach (var e in g.Entities)
            {
                var st = e.Buckets[i];
                if (st.Hours == 0) continue;
                var unit = e.Unit ?? "";
                rows.Add(e.Kind switch
                {
                    KasaMonitor.Workers.Mqtt.GenericHistoryKind.Counter => new[] { ReportFormat.BucketRow(g.Buckets[i], kind), e.Device, e.Entity, unit, "", "", "", Num(st.Delta, e.Decimals), "", InProgress(g.Buckets[i], nowUtc) },
                    KasaMonitor.Workers.Mqtt.GenericHistoryKind.Binary => new[] { ReportFormat.BucketRow(g.Buckets[i], kind), e.Device, e.Entity, "", "", "", "", "", Num(st.OnSeconds / 60, 0), InProgress(g.Buckets[i], nowUtc) },
                    _ => new[] { ReportFormat.BucketRow(g.Buckets[i], kind), e.Device, e.Entity, unit, Num(st.Min, e.Decimals), Num(st.Avg, e.Decimals), Num(st.Max, e.Decimals), "", "", InProgress(g.Buckets[i], nowUtc) },
                });
                links.Add(DrillLink(g.Buckets[i], kind, compare, null));
                keys.Add(IsoKey(g.Buckets[i], kind));
            }
        }
        return new HistoryTable(new[] { BucketHeader(kind), "Dispozitiv", "Valoare", "Unitate", "Min", "Medie", "Max", "Σ (contor)", "Pornit (min)", "Notă" },
            rows, links, keys);
    }

    /// <summary>The days of the energy signature: mean outdoor temperature, Eurostat degree days, the x of the line, heating gas.</summary>
    public static HistoryTable DegreeDays(HistoryClimate c, EnergyPrices prices)
    {
        var pts = c.Signature?.Points ?? Array.Empty<SignaturePoint>();
        return new HistoryTable(
            new[] { "Ziua", "T medie afară °C", $"Grade-zile Eurostat (bază {HeatingFormat.Number(c.Rule.BaseC, 1)} °C, prag {HeatingFormat.Number(c.Rule.ThresholdC, 1)} °C)",
                "Bază − T medie °C (x regresie)", "Gaz încălzire kWh", "Gaz încălzire m³" },
            pts.Select(p => (IReadOnlyList<string>)new[]
            {
                ReportFormat.Date(p.Day), Num(p.OutdoorMeanC, 1), Num(c.Rule.Of(p.OutdoorMeanC), 1), Num(p.DegreeDays, 1), Kwh(p.GasHeatingKwh),
                M3(prices.GasKwhPerM3 > 0 ? p.GasHeatingKwh / prices.GasKwhPerM3 : null),
            }).ToList(), CsvKeys: pts.Select(p => p.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)).ToList());
    }

    // ------------------------------------------------------------------ costs

    /// <summary>Per bar: electricity kWh and lei, gas m³ and lei, total (only with both parts: <see cref="EnergyPrices.Total"/>).</summary>
    public static HistoryTable Costs(HouseReport? house, HistoryGas? gas, EnergyPrices prices, IReadOnlyList<ReportBucket> buckets, ReportGranularity kind,
        DateTimeOffset nowUtc, bool compare)
    {
        var gasBars = gas is null ? null : GasPerBucket(gas, buckets);
        var rows = new List<IReadOnlyList<string>>();
        var links = new List<string?>();
        var keys = new List<string>();
        foreach (var i in Elapsed(buckets, nowUtc))
        {
            var kwh = house is not null && i < house.HouseKwh.Count ? house.HouseKwh[i] : null;
            var m3 = gasBars is not null && i < gasBars.Count ? gasBars[i] : null;
            rows.Add(new[] { ReportFormat.BucketRow(buckets[i], kind), Kwh(kwh), Lei(prices.Electricity(kwh)), M3(m3), Lei(prices.Gas(m3)), Lei(prices.Total(kwh, m3)),
                InProgress(buckets[i], nowUtc) });
            links.Add(DrillLink(buckets[i], kind, compare, null));
            keys.Add(IsoKey(buckets[i], kind));
        }
        return new HistoryTable(new[] { BucketHeader(kind), "Curent kWh", "Curent lei", "Gaz m³", "Gaz lei", "Total lei", "Notă" }, rows, links, keys);
    }

    /// <summary>
    /// Gas (both purposes) of the boiler report moved onto the given (local) bars by time (<see cref="BucketAlign.ByStart"/>):
    /// on a day, the boiler's hour that starts at 10:00 local lands on „10:00–11:00”; days and months land on their own.
    /// </summary>
    public static IReadOnlyList<double?> GasPerBucket(HistoryGas g, IReadOnlyList<ReportBucket> target) =>
        BucketAlign.ByStart(g.Report.Buckets,
            Enumerable.Range(0, g.Report.Buckets.Count).Select(i => ReportMath.SumOrNull(g.Report.Gas.Series.Select(s => At(s.Values, i)))).ToList(),
            target);

    // ------------------------------------------------------------------ events

    public static string SourceName(HistoryEventSource s) => s switch
    {
        HistoryEventSource.Relay => "Releu",
        HistoryEventSource.Boiler => "Centrală",
        _ => "Miele",
    };

    public static HistoryTable Events(IEnumerable<HistoryEvent> events, TimeZoneInfo tz) => new(
        new[] { "Început", "Sfârșit", "Sursă", "Ce", "Eveniment" },
        events.Select(e => (IReadOnlyList<string>)new[]
        {
            Local(e.TsUtc, tz), e.EndUtc is { } end ? Local(end, tz) : "", SourceName(e.Source), e.Subject, e.Text,
        }).ToList());

    /// <summary>
    /// „Putere” (0.14.0): one row per bucket and selected series that has data — the average and the maximum (W), when the
    /// maximum was, the minutes with data; with a comparison also the compared series' aligned values on its rows. The bucket
    /// as <c>yyyy-MM-dd HH:mm</c> (minute), <c>yyyy-MM-dd HH:00</c> (hour; the repeated hour of the autumn DST day with „ (2)”)
    /// or <c>yyyy-MM-dd</c> (day), local time.
    /// </summary>
    public static HistoryTable Power(HistoryPower p, TimeZoneInfo tz)
    {
        var compare = p.ComparisonAligned is not null;
        var headers = new List<string>
        {
            p.Resolution switch { PowerResolution.Minute => "Minutul", PowerResolution.Hour => "Ora", _ => "Ziua" },
            "Serie", "Medie W", "Max W", "Maxim la", "Minute cu date",
        };
        if (compare) headers.AddRange(new[] { "Comparație medie W", "Comparație max W" });
        var format = p.Resolution switch { PowerResolution.Minute => "yyyy-MM-dd HH:mm", PowerResolution.Hour => "yyyy-MM-dd HH:00", _ => "yyyy-MM-dd" };
        var seen = new HashSet<string>();
        var rows = new List<IReadOnlyList<string>>();
        for (var i = 0; i < p.Grid.Count; i++)
        {
            var key = TimeZoneInfo.ConvertTime(p.Grid[i], tz).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            if (!seen.Add(key)) key += " (2)";
            foreach (var s in p.Series)
            {
                if (s.Points[i] is not { } pt) continue;
                var row = new List<string> { key, PowerChart.Name(p, s.PlugId), Num(pt.AvgW, 0), Num(pt.MaxW, 0), Local(pt.MaxAtUtc, tz), pt.Minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) };
                if (compare)
                {
                    var c = s.PlugId == p.ComparedPlugId ? p.ComparisonAligned![i] : null;
                    row.Add(Num(c?.AvgW, 0));
                    row.Add(Num(c?.MaxW, 0));
                }
                rows.Add(row);
            }
        }
        return new HistoryTable(headers, rows);
    }

    /// <summary>"2026-11-02 14:05" in the application's zone.</summary>
    public static string Local(DateTimeOffset utc, TimeZoneInfo tz) =>
        TimeZoneInfo.ConvertTime(utc, tz).ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The prices of the house read from the settings (single place for /history and its CSV export).</summary>
public static class HistoryPrices
{
    public static async Task<EnergyPrices> LoadAsync(IKasaSettingsService kasa, IHeatingSettingsService heating, bool hasGas, CancellationToken ct)
    {
        var k = await kasa.GetAsync(ct);
        var h = await heating.GetAsync(ct);
        return new EnergyPrices(k.TariffRonPerKwh, h.GasKwhPerM3, h.GasPricePerKwh, hasGas);
    }
}
