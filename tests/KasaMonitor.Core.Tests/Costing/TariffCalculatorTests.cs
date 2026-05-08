using FluentAssertions;
using KasaMonitor.Core.Costing;
using Xunit;

namespace KasaMonitor.Core.Tests.Costing;

public class TariffCalculatorTests
{
    [Fact]
    public void CostFromWh_SimpleCase_ProducesRonAtTwoDecimals()
    {
        var calc = new TariffCalculator(ronPerKwh: 1.30m);
        calc.CostFromWh(1000).Should().Be(1.30m);
    }

    [Fact]
    public void CostFromWh_FractionalKwh_ProducesProportionalRon()
    {
        var calc = new TariffCalculator(ronPerKwh: 1.30m);
        calc.CostFromWh(500).Should().Be(0.65m); // 0.5 kWh
    }

    [Fact]
    public void CostFromWh_ZeroEnergy_IsZero()
    {
        new TariffCalculator(1.30m).CostFromWh(0).Should().Be(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void Constructor_RejectsNonPositiveRate(decimal rate)
    {
        var act = () => new TariffCalculator(rate);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
