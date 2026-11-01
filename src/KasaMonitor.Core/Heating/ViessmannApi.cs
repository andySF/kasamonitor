namespace KasaMonitor.Core.Heating;

/// <summary>
/// Viessmann (ViCare) cloud API: endpoints, OAuth scope and the free-plan limits. Single owner of these values
/// (worker, settings page, README all read them from here).
/// </summary>
public static class ViessmannApi
{
    public const string Provider = "viessmann";
    public const string AuthorizeUrl = "https://iam.viessmann-climatesolutions.com/idp/v3/authorize";
    public const string TokenUrl = "https://iam.viessmann-climatesolutions.com/idp/v3/token";
    public const string ApiBase = "https://api.viessmann-climatesolutions.com/iot/v2";
    /// <summary>"offline_access" asks for a refresh token.</summary>
    public const string Scope = "IoT User offline_access";
    /// <summary>Path of the OAuth redirect inside KasaMonitor (registered on the Viessmann developer portal).</summary>
    public const string CallbackPath = "/centrala/callback";

    /// <summary>Free (Basic) plan: 1450 calls per sliding 24 h and 120 per 10 minutes; above that the API answers 429.</summary>
    public const int DailyCallLimit = 1450;
    public const int TenMinuteCallLimit = 120;

    public const int MinPollIntervalSeconds = 60;
    public const int DefaultPollIntervalSeconds = 120;
    public const int MaxPollIntervalSeconds = 3600;

    /// <summary>The access token is refreshed this long before it expires (it lives 3600 s).</summary>
    public static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(5);

    /// <summary>Default gas calorific factor (kWh per m³) used to convert the boiler's m³ into kWh for the cost.</summary>
    public const double DefaultKwhPerM3 = 10.55;

    public static string InstallationsUrl => $"{ApiBase}/equipment/installations?includeGateways=true";

    /// <summary>One bulk call: every feature of the gateway and of the devices behind it.</summary>
    public static string FeaturesUrl(string installationId, string gatewaySerial) =>
        $"{ApiBase}/features/installations/{Uri.EscapeDataString(installationId)}/gateways/{Uri.EscapeDataString(gatewaySerial)}/features?includeDevicesFeatures=true";

    /// <summary>API calls per day at a given poll interval (one bulk call per poll).</summary>
    public static int CallsPerDay(int intervalSeconds) => 86400 / Math.Max(1, intervalSeconds);
}
