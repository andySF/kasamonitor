using FluentAssertions;
using KasaMonitor.Core.Costing;
using KasaMonitor.Core.Models;
using KasaMonitor.Web.Ui;
using Xunit;

namespace KasaMonitor.Web.Tests;

public class EnergyFormatTests
{
    [Theory]
    [InlineData(458, "0,46")]
    [InlineData(9_994, "9,99")]
    [InlineData(12_345, "12,3")]
    [InlineData(1_234_567, "1.234,6")]
    [InlineData(0, "0,00")]
    public void Kwh_RoRo(long wh, string expected) => EnergyFormat.Kwh(wh).Should().Be(expected);

    [Fact]
    public void Lines_UseTheTariff_AndRoRoFormat()
    {
        var tariff = new TariffCalculator(1.11m);

        EnergyFormat.Line("Azi", 458, tariff).Should().Be("Azi: 0,46 kWh · 0,51 lei");          // 0.458 × 1.11 = 0.508
        EnergyFormat.Line("Luna", 12_300, tariff).Should().Be("Luna: 12,3 kWh · 13,65 lei");     // 12.3 × 1.11 = 13.653
        EnergyFormat.Lei(1234.5m).Should().Be("1.234,50 lei");
    }

    [Fact]
    public void Sum_IgnoresReadingsWithoutTheCounter()
    {
        var readings = new EmeterReading?[]
        {
            new(1000, null, null, 0, TodayWh: 400, MonthWh: 9_000),
            new(1000, null, null, 0),                          // legacy plug before its first get_daystat
            null,                                              // offline plug
            new(1000, null, null, 0, TodayWh: 58, MonthWh: 3_300),
        };

        EnergyFormat.Sum(readings, r => r.TodayWh).Should().Be(458);
        EnergyFormat.Sum(readings, r => r.MonthWh).Should().Be(12_300);
        EnergyFormat.Sum(new EmeterReading?[] { null }, r => r.TodayWh).Should().BeNull();
    }
}
