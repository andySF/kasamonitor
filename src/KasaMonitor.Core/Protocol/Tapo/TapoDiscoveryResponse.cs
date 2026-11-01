using System.Net;

namespace KasaMonitor.Core.Protocol.Tapo;

/// <summary>Parsed answer to the TP-Link "TDP" discovery probe on UDP 20002.</summary>
/// <param name="Mac">Normalized (see <see cref="MacFormat"/>).</param>
/// <param name="DeviceType">e.g. "SMART.TAPOPLUG", "IOT.SMARTPLUGSWITCH".</param>
/// <param name="DeviceModel">e.g. "P115(EU)".</param>
/// <param name="EncryptType">mgt_encrypt_schm.encrypt_type: "KLAP", "AES", "TPAP", "XOR", ... (empty when absent).</param>
/// <param name="LoginVersion">mgt_encrypt_schm.lv (null when absent).</param>
public sealed record TapoDiscoveryResponse(
    IPAddress IpAddress,
    string Mac,
    string DeviceType,
    string DeviceModel,
    string EncryptType,
    int HttpPort,
    int? LoginVersion,
    bool IsSupportHttps);
