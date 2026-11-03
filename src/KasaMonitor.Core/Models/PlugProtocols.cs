namespace KasaMonitor.Core.Models;

/// <summary>Wire protocols a plug can be reached with. Stored as text in <c>Plugs.Protocol</c> and exported as the <c>protocol</c> metric label.</summary>
public static class PlugProtocols
{
    /// <summary>TP-Link Kasa legacy protocol: XOR-obfuscated JSON on TCP/UDP 9999.</summary>
    public const string Legacy = "legacy";

    /// <summary>TP-Link KLAP (HTTP /app/handshake1|2 + AES-CBC), used by Tapo SMART devices.</summary>
    public const string Klap = "klap";

    /// <summary>TP-Link "securePassthrough" (HTTP /app, RSA handshake + AES-CBC, login_device), used by older Tapo firmware.</summary>
    public const string Aes = "aes";

    /// <summary>Shelly Gen2/Gen3 local JSON-RPC over HTTP (<c>/rpc/&lt;Method&gt;</c>), optional digest auth.</summary>
    public const string ShellyRpc = "shelly-rpc";

    /// <summary>A protocol the application cannot speak (TPAP, Kasa-on-KLAP, ...). The plug is listed but never polled.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The TP-Link protocols (Kasa legacy, Tapo KLAP / AES) and the unsupported placeholders of the TP-Link discovery.</summary>
    public static bool IsTpLink(string protocol) => protocol is Legacy or Klap or Aes or Unsupported;

    /// <summary>The integration a plug belongs to (<see cref="Devices.IntegrationKeys"/>): Shelly RPC = shelly, the rest = tplink.</summary>
    public static string Integration(string protocol) =>
        protocol == ShellyRpc ? Devices.IntegrationKeys.Shelly : Devices.IntegrationKeys.TpLink;
}
