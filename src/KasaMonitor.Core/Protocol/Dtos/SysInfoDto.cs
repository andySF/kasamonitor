using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record SysInfoDto(
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("dev_name")] string? DevName,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("hw_ver")] string? HwVer,
    [property: JsonPropertyName("sw_ver")] string? SwVer,
    [property: JsonPropertyName("mac")] string? Mac,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("hwId")] string? HwId,
    [property: JsonPropertyName("oemId")] string? OemId,
    [property: JsonPropertyName("relay_state")] int RelayState,
    [property: JsonPropertyName("on_time")] long OnTime,
    [property: JsonPropertyName("rssi")] int Rssi,
    [property: JsonPropertyName("feature")] string? Feature
);
