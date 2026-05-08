using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record EmeterRealtimeDto(
    [property: JsonPropertyName("voltage_mv")] long VoltageMv,
    [property: JsonPropertyName("current_ma")] long CurrentMa,
    [property: JsonPropertyName("power_mw")] long PowerMw,
    [property: JsonPropertyName("total_wh")] long TotalWh,
    [property: JsonPropertyName("err_code")] int ErrCode
);
