using FluentAssertions;
using KasaMonitor.Core.Protocol.Dtos;
using System.Text.Json;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class DtoMappingTests
{
    private static T Load<T>(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixture);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public void SysInfo_BirouFixture_DeserializesCorrectly()
    {
        var dto = Load<SysInfoDto>("sysinfo_kp115_birou.json");
        dto.Alias.Should().Be("Birou");
        dto.Model.Should().StartWith("KP115");
        dto.Mac.Should().StartWith("C0:06:C3");
        dto.RelayState.Should().Be(1);
        dto.Rssi.Should().BeNegative();
    }

    [Fact]
    public void SysInfo_SufeagerieFixture_HasOldFirmware()
    {
        var dto = Load<SysInfoDto>("sysinfo_kp115_sufeagerie.json");
        dto.SwVer.Should().NotBeNull();
        dto.SwVer!.Should().Contain("210205");
    }

    [Fact]
    public void EmeterFixture_HasNonZeroPower()
    {
        var dto = Load<EmeterRealtimeDto>("emeter_kp115.json");
        dto.PowerMw.Should().BeGreaterThan(0);
        dto.VoltageMv.Should().BeGreaterThan(200_000); // > 200 V
        dto.ErrCode.Should().Be(0);
    }

    [Fact]
    public void DayStatFixture_ContainsDayEntries()
    {
        var dto = Load<DayStatDto>("daystat_kp115.json");
        dto.DayList.Should().NotBeEmpty();
        dto.DayList.Should().AllSatisfy(d =>
        {
            d.Year.Should().BeGreaterThan(2020);
            d.Day.Should().BeInRange(1, 31);
        });
    }

    [Fact]
    public void MonthStatFixture_ContainsMonthEntries()
    {
        var dto = Load<MonthStatDto>("monstat_kp115.json");
        dto.MonthList.Should().NotBeEmpty();
        dto.MonthList.Should().AllSatisfy(m => m.Month.Should().BeInRange(1, 12));
    }
}
