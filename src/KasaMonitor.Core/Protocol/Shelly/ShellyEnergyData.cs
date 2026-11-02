using System.Text.Json;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>One record of <c>EM1Data.GetData</c>: Wh consumed / returned during the period starting at <see cref="Ts"/> (unix seconds).</summary>
public sealed record ShellyEnergyRow(long Ts, double ActiveWh, double ReturnedWh);

/// <summary>One page of <c>EM1Data.GetData</c>; <see cref="NextRecordTs"/> is set when the device has more records to fetch.</summary>
public sealed record ShellyEnergyPage(IReadOnlyList<ShellyEnergyRow> Rows, long? NextRecordTs);

/// <summary>One block of <c>EM1Data.GetRecords</c>: <see cref="Records"/> consecutive records of <see cref="Period"/> seconds from <see cref="Ts"/>.</summary>
public sealed record ShellyDataBlock(long Ts, int Period, int Records);

/// <summary>
/// Energy history of Shelly meters. <c>EM1Data.GetData</c> answers <c>keys</c> plus <c>data[{ts, period, values[[...]]}]</c>
/// with one row per period (60 s on EM Gen3); <c>total_act_energy</c> in a row is the energy of that period, not a
/// cumulative value. Daily values are the sum of the rows whose start falls on the local day.
/// </summary>
public static class ShellyEnergyData
{
    public const string ActiveKey = "total_act_energy";
    public const string ReturnedKey = "total_act_ret_energy";

    public static ShellyEnergyPage ParseGetData(JsonElement result)
    {
        var rows = new List<ShellyEnergyRow>();
        var activeIdx = -1;
        var returnedIdx = -1;
        if (result.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var k in keys.EnumerateArray())
            {
                if (k.ValueKind == JsonValueKind.String && k.GetString() == ActiveKey) activeIdx = i;
                if (k.ValueKind == JsonValueKind.String && k.GetString() == ReturnedKey) returnedIdx = i;
                i++;
            }
        }
        if (activeIdx < 0)
            throw new KasaProtocolException($"EM1Data.GetData: no '{ActiveKey}' key", operation: "EM1Data.GetData");

        if (result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in data.EnumerateArray())
            {
                var ts = (long)(ShellyJson.Num(block, "ts") ?? 0);
                var period = (long)(ShellyJson.Num(block, "period") ?? 60);
                if (!block.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) continue;
                var n = 0;
                foreach (var row in values.EnumerateArray())
                {
                    if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > activeIdx)
                        rows.Add(new ShellyEnergyRow(ts + n * period, Value(row, activeIdx), returnedIdx >= 0 && row.GetArrayLength() > returnedIdx ? Value(row, returnedIdx) : 0));
                    n++;
                }
            }
        }
        long? next = ShellyJson.Num(result, "next_record_ts") is { } nr ? (long)nr : null;
        return new ShellyEnergyPage(rows, next);
    }

    public static IReadOnlyList<ShellyDataBlock> ParseRecords(JsonElement result)
    {
        if (!result.TryGetProperty("data_blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            return Array.Empty<ShellyDataBlock>();
        return blocks.EnumerateArray()
            .Select(b => new ShellyDataBlock((long)(ShellyJson.Num(b, "ts") ?? 0), (int)(ShellyJson.Num(b, "period") ?? 60), (int)(ShellyJson.Num(b, "records") ?? 0)))
            .ToList();
    }

    /// <summary>Unix seconds [start, end] of a local day: from local midnight to one second before the next one.</summary>
    public static (long FromTs, long ToTs) DayWindow(DateOnly day, TimeZoneInfo tz)
    {
        var start = LocalMidnightUtc(day, tz);
        var end = LocalMidnightUtc(day.AddDays(1), tz);
        return (start.ToUnixTimeSeconds(), end.ToUnixTimeSeconds() - 1);
    }

    /// <summary>Sums rows per local day (Wh, rounded). Days after <paramref name="notAfter"/> (device clock ahead) are dropped.</summary>
    public static IReadOnlyList<DailyKwh> DailyWh(IEnumerable<ShellyEnergyRow> rows, TimeZoneInfo tz, DateOnly? notAfter = null)
    {
        var sums = new SortedDictionary<DateOnly, double>();
        foreach (var r in rows)
        {
            var day = LocalDay(DateTimeOffset.FromUnixTimeSeconds(r.Ts), tz);
            if (notAfter is { } max && day > max) continue;
            sums[day] = sums.GetValueOrDefault(day) + r.ActiveWh;
        }
        return sums.Select(kv => new DailyKwh(kv.Key.Year, kv.Key.Month, kv.Key.Day, (long)Math.Round(kv.Value))).ToList();
    }

    /// <summary>
    /// Daily Wh from our own readings of a cumulative counter (Shelly <c>aenergy.total</c>), for devices without
    /// history: the positive differences between consecutive readings, attributed to the local day of the later one.
    /// A decrease (device reboot / counter reset) contributes nothing.
    /// </summary>
    public static IReadOnlyList<DailyKwh> DailyFromCumulative(IEnumerable<(DateTimeOffset AtUtc, long TotalWh)> readings, TimeZoneInfo tz)
    {
        var sums = new SortedDictionary<DateOnly, long>();
        long? prev = null;
        foreach (var (at, total) in readings.OrderBy(r => r.AtUtc))
        {
            var day = LocalDay(at, tz);
            if (!sums.ContainsKey(day)) sums[day] = 0;
            if (prev is { } p && total > p) sums[day] += total - p;
            prev = total;
        }
        return sums.Select(kv => new DailyKwh(kv.Key.Year, kv.Key.Month, kv.Key.Day, kv.Value)).ToList();
    }

    public static DateOnly LocalDay(DateTimeOffset at, TimeZoneInfo tz) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, tz).DateTime);

    /// <summary>A midnight skipped by a DST change does not exist: the first valid instant after it (<see cref="KasaMonitor.Core.Home.LocalClock.ToUtc"/>).</summary>
    public static DateTimeOffset LocalMidnightUtc(DateOnly day, TimeZoneInfo tz) =>
        KasaMonitor.Core.Home.LocalClock.ToUtc(day.ToDateTime(TimeOnly.MinValue), tz);

    private static double Value(JsonElement row, int index)
    {
        var v = row[index];
        return v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    }
}
