using KasaMonitor.Core.Heating;

namespace KasaMonitor.Core.Climate;

/// <summary>
/// The boiler's room thermostat (Zigbee sensor) as a virtual climate sensor, fed from the stored boiler readings
/// (<c>HeatingReadings</c>; no extra call to the Viessmann API). The supply and hot-water temperatures are water, not air:
/// since 0.9.3 they are no longer climate sensors (they stay in <c>HeatingReadings</c> and on /centrala); the sensors
/// created for them before are removed once at startup (<see cref="IsWaterSensor"/>).
/// </summary>
public static class ViessmannClimate
{
    public const string RoomSuffix = "room";
    /// <summary>The former supply sensor (until 0.9.2), kept to recognise and remove it.</summary>
    public const string SupplySuffix = "supply";
    /// <summary>The former hot-water sensor (until 0.9.2), kept to recognise and remove it.</summary>
    public const string DhwSuffix = "dhw";

    public const string RoomName = "Termostat (cameră)";
    public const string SupplyName = "Tur centrală";
    public const string DhwName = "Apă caldă";

    public static string ExternalId(Guid heatingDeviceId, string suffix) => heatingDeviceId.ToString("D") + ":" + suffix;

    /// <summary>The room thermostat's sample (none without a room temperature: the sensor is created on its first value).</summary>
    public static IEnumerable<ClimateSample> Samples(HeatingReading r)
    {
        if (r.RoomTempC is { } room)
            yield return new ClimateSample(ClimateSources.Viessmann, ExternalId(r.DeviceId, RoomSuffix), RoomName, r.TsUtc, room, null);
    }

    /// <summary>The boiler's former supply / hot-water virtual sensor (a water temperature, not a room).</summary>
    public static bool IsWaterSensor(string source, string externalId) =>
        source == ClimateSources.Viessmann
        && (externalId.EndsWith(":" + SupplySuffix, StringComparison.Ordinal) || externalId.EndsWith(":" + DhwSuffix, StringComparison.Ordinal));
}
