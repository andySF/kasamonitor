namespace KasaMonitor.Core.Climate;

/// <summary>Where a climate sensor's values come from (column <c>ClimateSensors.Source</c>, metric label <c>source</c>).</summary>
public static class ClimateSources
{
    /// <summary>Virtual sensors fed from the boiler readings (<c>HeatingReadings</c>): room thermostat, supply, hot water.</summary>
    public const string Viessmann = "viessmann";
    /// <summary>Govee Wi-Fi thermometers through the Govee Developer API v1 (cloud).</summary>
    public const string Govee = "govee";
    /// <summary>Shelly Gen2+ devices with <c>temperature:N</c> / <c>humidity:N</c> components (H&amp;T Gen3, Plus Add-on), local RPC.</summary>
    public const string Shelly = "shelly";
    /// <summary>The outdoor temperature of Open-Meteo (free, no key) for the configured latitude/longitude.</summary>
    public const string OpenMeteo = "openmeteo";

    public static readonly IReadOnlyList<string> All = new[] { Viessmann, Govee, Shelly, OpenMeteo };

    /// <summary>Romanian display name of a source.</summary>
    public static string DisplayName(string source) => source switch
    {
        Viessmann => "Centrală (Viessmann)",
        Govee => "Govee",
        Shelly => "Shelly",
        OpenMeteo => "Open-Meteo",
        _ => source,
    };
}

/// <summary>
/// A climate sensor (table <c>ClimateSensors</c>): one temperature (and optionally humidity) series. (Source, ExternalId)
/// is unique: the boiler's virtual sensors use <c>&lt;heating device id&gt;:room|supply|dhw</c>, Govee the device id, Shelly
/// <c>&lt;mac&gt;:&lt;component id&gt;</c>, Open-Meteo <c>outdoor</c>. Name, room and „afară” are edited in /clima.
/// </summary>
public sealed record ClimateSensor(
    Guid Id,
    string Source,
    string ExternalId,
    string Name,
    string Room,
    bool IsOutdoor,
    bool IsArchived,
    DateTimeOffset? LastSeenUtc,
    int? BatteryPercent);

/// <summary>One stored reading (table <c>ClimateReadings</c>), written on change or as the 30-minute heartbeat (<see cref="ClimateWriteRule"/>).</summary>
public sealed record ClimateReading(Guid SensorId, DateTimeOffset TsUtc, double TemperatureC, double? HumidityPercent, int? BatteryPercent);

/// <summary>
/// One value read from a source, before it is matched to a sensor. <see cref="Online"/> false (Govee <c>online:false</c>)
/// or a missing temperature mark the sensor offline and store nothing. The defaults are used only when the sensor is new.
/// </summary>
public sealed record ClimateSample(
    string Source,
    string ExternalId,
    string DefaultName,
    DateTimeOffset TsUtc,
    double? TemperatureC,
    double? HumidityPercent,
    int? BatteryPercent = null,
    bool Online = true,
    string DefaultRoom = "",
    bool DefaultOutdoor = false,
    bool BatteryPowered = false);

/// <summary>
/// When a reading is stored: the first one, a temperature change of at least 0.1 °C, a humidity change of at least 1 %
/// RH (or humidity appearing/disappearing) against the last STORED reading, or 30 minutes since it (heartbeat). A reading
/// not newer than the last stored one is never stored (the key is sensor + second).
/// </summary>
public static class ClimateWriteRule
{
    public const double MinTemperatureDeltaC = 0.1;
    public const double MinHumidityDeltaPercent = 1.0;
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(30);

    private const double Epsilon = 1e-9;

    public static bool ShouldWrite(ClimateReading? lastStored, DateTimeOffset tsUtc, double temperatureC, double? humidityPercent)
    {
        if (lastStored is null) return true;
        if (tsUtc.ToUnixTimeSeconds() <= lastStored.TsUtc.ToUnixTimeSeconds()) return false;
        if (tsUtc - lastStored.TsUtc >= Heartbeat) return true;
        if (Math.Abs(temperatureC - lastStored.TemperatureC) >= MinTemperatureDeltaC - Epsilon) return true;
        if (humidityPercent is null != lastStored.HumidityPercent is null) return true;
        return humidityPercent is { } h && lastStored.HumidityPercent is { } lh && Math.Abs(h - lh) >= MinHumidityDeltaPercent - Epsilon;
    }
}

/// <summary>How fresh the last value of a sensor is, against the interval its source delivers at.</summary>
public enum ClimateFreshnessLevel
{
    /// <summary>Within 2 expected intervals (green).</summary>
    Fresh,
    /// <summary>Within 6 expected intervals (yellow).</summary>
    Late,
    /// <summary>Older, or never read (red).</summary>
    Stale,
}

public static class ClimateFreshness
{
    public const int FreshIntervals = 2;
    public const int LateIntervals = 6;

    /// <summary>Govee is polled every 10 minutes (the battery thermometers upload about every 30 minutes).</summary>
    public static readonly TimeSpan GoveeInterval = TimeSpan.FromMinutes(10);
    /// <summary>Open-Meteo is read every 15 minutes (its „current” values are 15-minute slots).</summary>
    public static readonly TimeSpan OpenMeteoInterval = TimeSpan.FromMinutes(15);
    /// <summary>A mains/USB powered Shelly is read every minute.</summary>
    public static readonly TimeSpan ShellyInterval = TimeSpan.FromMinutes(1);
    /// <summary>
    /// A Shelly H&amp;T on batteries sleeps and is read only when it wakes (on a change or its periodic report); 2 hours
    /// is an assumption, not verified on the device.
    /// </summary>
    public static readonly TimeSpan ShellyBatteryInterval = TimeSpan.FromHours(2);

    /// <summary>The interval a source delivers at; the boiler's is its poll interval (default 120 s).</summary>
    public static TimeSpan Expected(string source, bool batteryPowered, int viessmannPollSeconds) => source switch
    {
        ClimateSources.Viessmann => TimeSpan.FromSeconds(Math.Max(60, viessmannPollSeconds)),
        ClimateSources.Govee => GoveeInterval,
        ClimateSources.Shelly => batteryPowered ? ShellyBatteryInterval : ShellyInterval,
        ClimateSources.OpenMeteo => OpenMeteoInterval,
        _ => TimeSpan.FromMinutes(10),
    };

    public static ClimateFreshnessLevel Level(DateTimeOffset? lastReadUtc, DateTimeOffset now, TimeSpan expected)
    {
        if (lastReadUtc is not { } last) return ClimateFreshnessLevel.Stale;
        var age = now - last;
        if (age <= expected * FreshIntervals) return ClimateFreshnessLevel.Fresh;
        return age <= expected * LateIntervals ? ClimateFreshnessLevel.Late : ClimateFreshnessLevel.Stale;
    }
}
