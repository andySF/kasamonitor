using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record MonthStatDto(
    [property: JsonPropertyName("month_list")] IReadOnlyList<MonthEntryDto> MonthList,
    [property: JsonPropertyName("err_code")] int ErrCode
);

public sealed record MonthEntryDto(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("energy_wh")] long EnergyWh
);
