using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Shelly;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol.Shelly;

public class ShellyEnergyDataTests
{
    /// <summary>Romania in November (EET, UTC+2), fixed so the tests do not depend on the machine's zone.</summary>
    private static readonly TimeZoneInfo Plus2 = TimeZoneInfo.CreateCustomTimeZone("test+2", TimeSpan.FromHours(2), "test+2", "test+2");

    [Fact]
    public void GetData_RealAnswer_RowsPerMinute_AndDailySumMatchesTheDeviceCounter()
    {
        var page = ShellyEnergyData.ParseGetData(ShellyFixtures.Json("shelly_emg3_getdata.json"));

        page.Rows.Should().HaveCount(10);              // 9 rows in the first block, 1 in the second
        page.NextRecordTs.Should().BeNull();
        page.Rows[0].Ts.Should().Be(1793544120);
        page.Rows[1].Ts.Should().Be(1793544180);       // ts + period
        page.Rows[3].ActiveWh.Should().Be(6.8409);
        page.Rows[0].ReturnedWh.Should().Be(5.1036);
        page.Rows[9].Ts.Should().Be(1793565720);

        var days = ShellyEnergyData.DailyWh(page.Rows, Plus2);

        // 2026-11-01 16:42 .. 22:42 local, one day; 43.43 Wh = em1data:0.total_act_energy of the same device.
        days.Should().Equal(new DailyKwh(2026, 11, 1, 43));
    }

    [Fact]
    public void DailyWh_SplitsAtLocalMidnight()
    {
        // Four one-minute rows from 23:58 local: two before midnight, two after.
        var start = new DateTimeOffset(2026, 11, 1, 23, 58, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds();
        var json = $$"""
            {"keys":["total_act_energy","total_act_ret_energy","avg_voltage"],
             "data":[{"ts":{{start}},"period":60,"values":[[10.4,0,230],[10.4,0,230],[5.25,1.0,230],[5.35,0,230]]}],
             "next_record_ts":{{start + 240}}}
            """;
        var page = ShellyEnergyData.ParseGetData(ShellyFixtures.Parse(json));

        page.NextRecordTs.Should().Be(start + 240);
        ShellyEnergyData.DailyWh(page.Rows, Plus2).Should().Equal(
            new DailyKwh(2026, 11, 1, 21),   // 20.8 -> 21
            new DailyKwh(2026, 11, 2, 11));  // 10.6 -> 11
    }

    [Fact]
    public void DailyWh_DropsDaysAfterToday_DeviceClockAhead()
    {
        var rows = new[]
        {
            new ShellyEnergyRow(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds(), 5, 0),
            new ShellyEnergyRow(new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.FromHours(2)).ToUnixTimeSeconds(), 7, 0),
        };

        ShellyEnergyData.DailyWh(rows, Plus2, notAfter: new DateOnly(2026, 11, 1)).Should().Equal(new DailyKwh(2026, 11, 1, 5));
    }

    [Fact]
    public void GetData_WithoutActiveEnergyKey_Throws()
    {
        var act = () => ShellyEnergyData.ParseGetData(ShellyFixtures.Parse("""{"keys":["avg_voltage"],"data":[]}"""));
        act.Should().Throw<KasaMonitor.Core.Protocol.KasaProtocolException>();
    }

    [Fact]
    public void Records_RealAnswer()
    {
        var blocks = ShellyEnergyData.ParseRecords(ShellyFixtures.Json("shelly_emg3_records.json"));

        blocks.Should().HaveCount(3);
        blocks[1].Should().Be(new ShellyDataBlock(1793544120, 60, 9));
    }

    [Fact]
    public void DayWindow_IsLocalMidnightToMidnight()
    {
        var (from, to) = ShellyEnergyData.DayWindow(new DateOnly(2026, 11, 2), Plus2);

        DateTimeOffset.FromUnixTimeSeconds(from).Should().Be(new DateTimeOffset(2026, 11, 1, 22, 0, 0, TimeSpan.Zero));
        (to - from).Should().Be(86_399);
    }

    [Fact]
    public void DailyFromCumulative_SumsIncreases_AcrossMidnight_AndIgnoresResets()
    {
        DateTimeOffset At(int day, int hour, int min) => new(2026, 11, day, hour, min, 0, TimeSpan.FromHours(2));
        var readings = new[]
        {
            (At(1, 23, 50), 1000L),
            (At(1, 23, 59), 1010L),   // +10 on 11-01
            (At(2, 0, 5), 1013L),     // +3 on 11-02
            (At(2, 8, 0), 5L),        // device reset: nothing
            (At(2, 9, 0), 25L),       // +20 on 11-02
        };

        ShellyEnergyData.DailyFromCumulative(readings, Plus2).Should().Equal(
            new DailyKwh(2026, 11, 1, 10),
            new DailyKwh(2026, 11, 2, 23));
    }
}
