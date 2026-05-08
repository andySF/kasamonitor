using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record DayStatDto(
    [property: JsonPropertyName("day_list")] IReadOnlyList<DayEntryDto> DayList,
    [property: JsonPropertyName("err_code")] int ErrCode
);

public sealed record DayEntryDto(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("day")] int Day,
    [property: JsonPropertyName("energy_wh")] long EnergyWh
);
