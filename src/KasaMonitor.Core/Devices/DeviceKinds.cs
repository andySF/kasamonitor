namespace KasaMonitor.Core.Devices;

/// <summary>
/// What a device is, across integrations (the filter „Categorie” of /dispozitive). A plug's category follows its role
/// (<see cref="Models.PlugRole.Plug"/> = priză, main meter / sub-meter = contor).
/// </summary>
public enum DeviceCategory
{
    Plug,
    Meter,
    Boiler,
    Appliance,
    ClimateSensor,
    Weather,
}

public static class DeviceCategories
{
    public static readonly IReadOnlyList<DeviceCategory> All = Enum.GetValues<DeviceCategory>();

    /// <summary>The Romanian name shown in tables and filters.</summary>
    public static string Name(DeviceCategory c) => c switch
    {
        DeviceCategory.Plug => "Priză",
        DeviceCategory.Meter => "Contor",
        DeviceCategory.Boiler => "Centrală",
        DeviceCategory.Appliance => "Electrocasnic",
        DeviceCategory.ClimateSensor => "Senzor climă",
        DeviceCategory.Weather => "Sursă meteo",
        _ => c.ToString(),
    };

    /// <summary>Bootstrap icon of the category.</summary>
    public static string Icon(DeviceCategory c) => c switch
    {
        DeviceCategory.Plug => "bi-outlet",
        DeviceCategory.Meter => "bi-speedometer",
        DeviceCategory.Boiler => "bi-fire",
        DeviceCategory.Appliance => "bi-water",
        DeviceCategory.ClimateSensor => "bi-thermometer-half",
        DeviceCategory.Weather => "bi-cloud-sun",
        _ => "bi-question-circle",
    };

    /// <summary>The value in the address (<c>?cat=</c>): the enum name in lower case.</summary>
    public static string Slug(DeviceCategory c) => c.ToString().ToLowerInvariant();

    public static DeviceCategory? FromSlug(string? slug) =>
        All.Cast<DeviceCategory?>().FirstOrDefault(c => string.Equals(Slug(c!.Value), slug, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Keys of the integrations (how the app talks to something), the axis of /settings tabs and of the „Firmă” filter of
/// /dispozitive. The names shown are the vendors' (registry <c>Integrations</c> in the web project).
/// </summary>
public static class IntegrationKeys
{
    /// <summary>TP-Link Kasa (legacy 9999) and Tapo (KLAP / AES): one account, one discovery.</summary>
    public const string TpLink = "tplink";
    /// <summary>Shelly Gen2/Gen3 over RPC: meters, plugs and climate sensors (H&amp;T, Plus Add-on).</summary>
    public const string Shelly = "shelly";
    /// <summary>Viessmann ViCare cloud: the boiler and its room thermostat.</summary>
    public const string Viessmann = "viessmann";
    /// <summary>Miele 3rd Party API: appliances.</summary>
    public const string Miele = "miele";
    /// <summary>Govee cloud: Wi-Fi thermometers.</summary>
    public const string Govee = "govee";
    /// <summary>Open-Meteo: the outdoor weather (a service, not a device vendor).</summary>
    public const string OpenMeteo = "openmeteo";
}
