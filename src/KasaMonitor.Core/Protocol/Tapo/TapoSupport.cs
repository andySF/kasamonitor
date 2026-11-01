using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>Decides how a device that answered on UDP 20002 can be reached. Owner of the Romanian "unsupported" texts.</summary>
public static class TapoSupport
{
    public const string MissingCredentialsReason = "lipsesc credențialele TP-Link";
    public const string AuthFailedReason = "autentificare eșuată (verifică credențialele TP-Link)";
    public const string KasaKlapReason = "KLAP pe Kasa (IOT), neimplementat";
    private const string UnknownProtocolPrefix = "protocol ";

    /// <summary>Shown in the UI next to plugs whose discovery reports a protocol the app cannot speak (TPAP, ...).</summary>
    public const string ThirdPartyCompatibilityHint =
        "În aplicația Tapo: Setări dispozitiv → Third-Party Compatibility: oprit, apoi pornit, și repornește priza; de obicei revine pe KLAP.";

    public enum Route
    {
        /// <summary>Kasa device that also answers TDP with XOR: handled by the legacy 9999 sysinfo query.</summary>
        Legacy,
        /// <summary>Tapo SMART device over KLAP (auth hash v2).</summary>
        Klap,
        /// <summary>Tapo SMART device over AES securePassthrough.</summary>
        Aes,
        Unsupported,
    }

    public static (Route Route, string? Reason) Classify(TapoDiscoveryResponse r)
    {
        var smart = r.DeviceType.StartsWith("SMART.", StringComparison.OrdinalIgnoreCase);
        var iot = r.DeviceType.StartsWith("IOT.", StringComparison.OrdinalIgnoreCase);
        var enc = r.EncryptType.ToUpperInvariant();

        if (iot && enc is "XOR" or "") return (Route.Legacy, null);
        if (smart && enc == "KLAP") return (Route.Klap, null);
        if (smart && enc == "AES") return (Route.Aes, null);
        if (iot && enc == "KLAP") return (Route.Unsupported, KasaKlapReason);
        return (Route.Unsupported, UnknownProtocolPrefix + (enc.Length == 0 ? "necunoscut" : r.EncryptType));
    }

    /// <summary>Transport name (<see cref="PlugProtocols"/>) for a SMART route, null otherwise.</summary>
    public static string? ProtocolFor(Route route) => route switch
    {
        Route.Klap => PlugProtocols.Klap,
        Route.Aes => PlugProtocols.Aes,
        _ => null,
    };

    /// <summary>True for "protocol TPAP" style reasons (the ones the Third-Party Compatibility hint applies to).</summary>
    public static bool IsUnknownProtocolReason(string? reason) =>
        reason is not null && reason.StartsWith(UnknownProtocolPrefix, StringComparison.Ordinal);

    /// <summary>"nesuportat: P115(EU), protocol TPAP".</summary>
    public static string Describe(string model, string reason) =>
        string.IsNullOrWhiteSpace(model) ? $"nesuportat: {reason}" : $"nesuportat: {model}, {reason}";

    /// <summary>Placeholder PlugInfo for a device whose details cannot be read (alias = model + MAC).</summary>
    public static PlugInfo PlaceholderInfo(TapoDiscoveryResponse r) =>
        new(r.Mac, $"{(r.DeviceModel.Length > 0 ? r.DeviceModel : r.DeviceType)} {r.Mac}".Trim(), r.DeviceModel,
            "", "", "", false, 0, TimeSpan.Zero);
}
