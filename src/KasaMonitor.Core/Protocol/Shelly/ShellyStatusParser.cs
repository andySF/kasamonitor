using System.Text.Json;
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Shelly;

/// <summary>Which component family carries the measurement of a Shelly device (chosen from the keys of <c>Shelly.GetStatus</c>).</summary>
public enum ShellyMeterKind
{
    /// <summary>No measuring component (e.g. a relay without power metering): nothing to poll.</summary>
    None,
    /// <summary><c>em:0</c> + <c>emdata:0</c>, three-phase meter (Pro 3EM): exposed as one channel with the totals.</summary>
    Em,
    /// <summary><c>em1:N</c> + <c>em1data:N</c>, single-phase meter channels (EM Gen3, Pro EM).</summary>
    Em1,
    /// <summary><c>pm1:N</c>, power meter without relay (PM Mini).</summary>
    Pm1,
    /// <summary><c>switch:N</c> with <c>apower</c> (Plus Plug S, Plus 1PM, Pro xPM).</summary>
    Switch,
}

public sealed record ShellyChannelReading(int Channel, EmeterReading Reading);

/// <summary>Readings of every measured channel of one device, from one <c>Shelly.GetStatus</c> answer.</summary>
public sealed record ShellyStatusReadings(ShellyMeterKind Kind, IReadOnlyList<ShellyChannelReading> Channels)
{
    /// <summary>The device keeps per-minute energy history readable with <c>EM1Data.GetData</c>.</summary>
    public bool HasDeviceHistory => Kind == ShellyMeterKind.Em1;

    public EmeterReading? For(int channel) => Channels.FirstOrDefault(c => c.Channel == channel)?.Reading;
}

/// <summary>
/// Maps <c>Shelly.GetStatus</c> to readings. Components are found from the answer's keys, never from the model:
/// <c>em:0</c> wins over <c>em1:N</c>, then <c>pm1:N</c>, then <c>switch:N</c> with <c>apower</c>; the channel number is
/// the component id. Field names follow the official Gen2+ component docs (EM, EM1, EMData, EM1Data, PM1, Switch).
/// </summary>
public static class ShellyStatusParser
{
    public static ShellyStatusReadings Parse(JsonElement status)
    {
        var comps = ShellyJson.Components(status)
            .Where(c => c.Value.ValueKind == JsonValueKind.Object && c.Id >= 0)
            .GroupBy(c => c.Type)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).ToList());

        var switches = comps.GetValueOrDefault("switch") ?? new();
        JsonElement? Switch(int id) => switches.Where(s => s.Id == id).Select(s => (JsonElement?)s.Value).FirstOrDefault();

        if (comps.TryGetValue("em", out var em))
        {
            var c = em[0];
            var data = ShellyJson.Component(status, "emdata", c.Id);
            var sw = Switch(0);
            var reading = Reading(
                powerW: ShellyJson.Num(c.Value, "total_act_power"),
                voltageV: null,
                currentA: ShellyJson.Num(c.Value, "total_current"),
                totalWh: ShellyJson.Num(data, "total_act"),
                returnedWh: ShellyJson.Num(data, "total_act_ret"),
                pf: null,
                apparentVa: ShellyJson.Num(c.Value, "total_aprt_power"),
                freqHz: ShellyJson.Num(c.Value, "a_freq"),
                relay: ShellyJson.Bool(sw, "output"),
                temperatureC: Temperature(sw));
            return new ShellyStatusReadings(ShellyMeterKind.Em, new[] { new ShellyChannelReading(0, reading) });
        }

        if (comps.TryGetValue("em1", out var em1))
        {
            var list = em1.Select(c =>
            {
                var data = ShellyJson.Component(status, "em1data", c.Id);
                // The relay of EM Gen3 / Pro EM belongs to the device, not to a meter channel: shown on channel 0 only.
                var sw = c.Id == 0 ? Switch(0) : null;
                return new ShellyChannelReading(c.Id, Reading(
                    powerW: ShellyJson.Num(c.Value, "act_power"),
                    voltageV: ShellyJson.Num(c.Value, "voltage"),
                    currentA: ShellyJson.Num(c.Value, "current"),
                    totalWh: ShellyJson.Num(data, "total_act_energy"),
                    returnedWh: ShellyJson.Num(data, "total_act_ret_energy"),
                    pf: ShellyJson.Num(c.Value, "pf"),
                    apparentVa: ShellyJson.Num(c.Value, "aprt_power"),
                    freqHz: ShellyJson.Num(c.Value, "freq"),
                    relay: ShellyJson.Bool(sw, "output"),
                    temperatureC: Temperature(sw)));
            }).ToList();
            return new ShellyStatusReadings(ShellyMeterKind.Em1, list);
        }

        if (comps.TryGetValue("pm1", out var pm1))
            return new ShellyStatusReadings(ShellyMeterKind.Pm1,
                pm1.Select(c => new ShellyChannelReading(c.Id, PowerComponent(c.Value, relay: null))).ToList());

        var metered = switches.Where(s => ShellyJson.Num(s.Value, "apower") is not null).ToList();
        if (metered.Count > 0)
            return new ShellyStatusReadings(ShellyMeterKind.Switch,
                metered.Select(c => new ShellyChannelReading(c.Id, PowerComponent(c.Value, ShellyJson.Bool(c.Value, "output")))).ToList());

        return new ShellyStatusReadings(ShellyMeterKind.None, Array.Empty<ShellyChannelReading>());
    }

    /// <summary>Wi-Fi RSSI (dBm) from the <c>wifi</c> component, 0 when absent.</summary>
    public static int Rssi(JsonElement status) => (int)(ShellyJson.Num(ShellyJson.Obj(status, "wifi"), "rssi") ?? 0);

    /// <summary>Time since boot from <c>sys.uptime</c>.</summary>
    public static TimeSpan Uptime(JsonElement status) =>
        TimeSpan.FromSeconds(ShellyJson.Num(ShellyJson.Obj(status, "sys"), "uptime") ?? 0);

    /// <summary>pm1:N and metering switch:N share field names (apower, voltage, current, pf, freq, aenergy.total, ret_aenergy.total).</summary>
    private static EmeterReading PowerComponent(JsonElement c, bool? relay) => Reading(
        powerW: ShellyJson.Num(c, "apower"),
        voltageV: ShellyJson.Num(c, "voltage"),
        currentA: ShellyJson.Num(c, "current"),
        totalWh: ShellyJson.Num(ShellyJson.Obj(c, "aenergy"), "total"),
        returnedWh: ShellyJson.Num(ShellyJson.Obj(c, "ret_aenergy"), "total"),
        pf: ShellyJson.Num(c, "pf"),
        apparentVa: null,
        freqHz: ShellyJson.Num(c, "freq"),
        relay: relay,
        temperatureC: Temperature(c));

    private static double? Temperature(JsonElement? component) => ShellyJson.Num(ShellyJson.Obj(component, "temperature"), "tC");

    private static EmeterReading Reading(double? powerW, double? voltageV, double? currentA, double? totalWh, double? returnedWh,
        double? pf, double? apparentVa, double? freqHz, bool? relay, double? temperatureC) =>
        new(
            PowerMw: (long)Math.Round((powerW ?? 0) * 1000),
            VoltageMv: voltageV is { } v ? (long)Math.Round(v * 1000) : null,
            CurrentMa: currentA is { } a ? (long)Math.Round(a * 1000) : null,
            // Floor keeps a monotonic device counter monotonic (the Prometheus counter only adds positive deltas).
            // A missing counter stays null: 0 would make the next normal reading add the whole total again.
            TotalWh: totalWh is { } wh ? (long)Math.Floor(wh) : null,
            PowerFactor: pf,
            ApparentPowerVa: apparentVa,
            FrequencyHz: freqHz,
            ReturnedWh: returnedWh,
            TemperatureC: temperatureC,
            RelayOn: relay);
}
