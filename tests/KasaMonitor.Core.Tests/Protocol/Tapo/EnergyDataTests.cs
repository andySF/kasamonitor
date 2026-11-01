using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Core.Protocol.Tapo;
using System.Text.Json;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Tapo;

public class EnergyDataTests
{
    // Fixed UTC+3 (summer time in Romania) so the test does not depend on the machine's time zone or DST rules.
    private static readonly TimeZoneInfo Plus3 = TimeZoneInfo.CreateCustomTimeZone("test+3", TimeSpan.FromHours(3), "test+3", "test+3");

    private static JsonElement Result(string json) =>
        SmartMessages.ParseResult(json, "get_energy_data");

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void Params_AreLocalMidnightsAndDailyInterval()
    {
        var p = SmartMessages.EnergyDataParams(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Plus3);

        p["start_timestamp"]!.GetValue<long>().Should().Be(1790802000);   // 2026-09-30T21:00:00Z = 2026-10-01 00:00 +03
        p["end_timestamp"]!.GetValue<long>().Should().Be(1790802000 + 30 * 86400);
        p["interval"]!.GetValue<int>().Should().Be(1440);
    }

    [Fact]
    public void Request_CarriesParams()
    {
        var json = SmartMessages.BuildRequest("get_energy_data", DateTimeOffset.UnixEpoch,
            SmartMessages.EnergyDataParams(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Plus3));
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("method").GetString().Should().Be("get_energy_data");
        doc.RootElement.GetProperty("params").GetProperty("interval").GetInt32().Should().Be(1440);
    }

    [Fact]
    public void DailyData_MapsFromStartTimestamp_AndDropsFutureDays()
    {
        var r = Result(LoadFixture("tapo_energy_data_daily.json"));

        var days = SmartMessages.MapDailyEnergyData(r, new DateOnly(2026, 10, 1), today: new DateOnly(2026, 10, 15), Plus3);

        days.Should().HaveCount(15);
        days[0].Should().Be(new DailyKwh(2026, 10, 1, 400));
        days[14].Should().Be(new DailyKwh(2026, 10, 15, 414));
    }

    [Fact]
    public void DailyData_StartTimestampWins_OverRequestedDay()
    {
        // Device answered the whole quarter although we asked from November.
        var start = SmartMessages.LocalMidnight(new DateOnly(2026, 10, 1), Plus3).ToUnixTimeSeconds();
        var r = Result("{\"error_code\":0,\"result\":{\"data\":[1,2,3],\"start_timestamp\":" + start + ",\"interval\":1440}}");

        var days = SmartMessages.MapDailyEnergyData(r, new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 31), Plus3);

        days.Select(d => (d.Month, d.Day, d.EnergyWh)).Should().Equal((10, 1, 1L), (10, 2, 2L), (10, 3, 3L));
    }

    [Fact]
    public void DailyData_DeviceMidnightInUtc_StillMapsToTheSameDay()
    {
        // Device used 00:00 UTC (= 03:00 local): +12 h rounding keeps 2026-10-01.
        var start = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var r = Result("{\"error_code\":0,\"result\":{\"data\":[7],\"start_timestamp\":" + start + ",\"interval\":1440}}");

        SmartMessages.MapDailyEnergyData(r, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Plus3)
            .Single().Should().Be(new DailyKwh(2026, 10, 1, 7));
    }

    [Fact]
    public void DailyData_WithoutStartTimestamp_UsesRequestedDay()
    {
        var r = Result("""{"error_code":0,"result":{"data":[5,6]}}""");

        SmartMessages.MapDailyEnergyData(r, new DateOnly(2026, 10, 30), new DateOnly(2026, 12, 31), Plus3)
            .Should().Equal(new DailyKwh(2026, 10, 30, 5), new DailyKwh(2026, 10, 31, 6));
    }

    [Fact]
    public void WrongInterval_Throws()
    {
        var r = Result("""{"error_code":0,"result":{"data":[1],"interval":60}}""");
        var act = () => SmartMessages.MapDailyEnergyData(r, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), Plus3);
        act.Should().Throw<KasaProtocolException>();
    }
}
