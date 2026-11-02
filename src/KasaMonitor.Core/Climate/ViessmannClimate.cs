using KasaMonitor.Core.Heating;

namespace KasaMonitor.Core.Climate;

/// <summary>
/// The boiler's temperatures as virtual climate sensors, fed from the stored boiler readings (<c>HeatingReadings</c>;
/// no extra call to the Viessmann API): the room thermostat (Zigbee sensor), the supply and the hot water.
/// </summary>
public static class ViessmannClimate
{
    public const string RoomSuffix = "room";
    public const string SupplySuffix = "supply";
    public const string DhwSuffix = "dhw";

    public const string RoomName = "Termostat (cameră)";
    public const string SupplyName = "Tur centrală";
    public const string DhwName = "Apă caldă";

    public static string ExternalId(Guid heatingDeviceId, string suffix) => heatingDeviceId.ToString("D") + ":" + suffix;

    /// <summary>One sample per value the reading has (none for a null value: the sensor is created on its first value).</summary>
    public static IEnumerable<ClimateSample> Samples(HeatingReading r)
    {
        if (r.RoomTempC is { } room)
            yield return new ClimateSample(ClimateSources.Viessmann, ExternalId(r.DeviceId, RoomSuffix), RoomName, r.TsUtc, room, null);
        if (r.SupplyTempC is { } supply)
            yield return new ClimateSample(ClimateSources.Viessmann, ExternalId(r.DeviceId, SupplySuffix), SupplyName, r.TsUtc, supply, null);
        if (r.DhwTempC is { } dhw)
            yield return new ClimateSample(ClimateSources.Viessmann, ExternalId(r.DeviceId, DhwSuffix), DhwName, r.TsUtc, dhw, null);
    }
}
