using FluentAssertions;
using KasaMonitor.Core.Climate;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class ClimateOverviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 13, 35, 0, TimeSpan.Zero);
    private static readonly Guid Boiler = Guid.NewGuid();

    private static ClimateSensorLive S(string source, string externalId, string name, string room, double? t, double? rh = null,
        bool outdoor = false, bool archived = false, int ageMin = 1) =>
        new(new ClimateSensor(Guid.NewGuid(), source, externalId, name, room, outdoor, archived, Now, null), t, rh, null, Now.AddMinutes(-ageMin));

    [Fact]
    public void OutsideIsTheSensorTickedOutdoor_RoomsAreTheOtherSensors_WithoutTheBoilerWater_IndoorFirst()
    {
        var sensors = new[]
        {
            S(ClimateSources.OpenMeteo, "home", "Open-Meteo", "", 6.5, 81, outdoor: true),
            S(ClimateSources.Govee, "EE", "Govee balcon", "Balcon", 5.9, 85, outdoor: true),
            S(ClimateSources.OpenMeteo, "other", "Open-Meteo (nebifat)", "Pod", 6.0),
            S(ClimateSources.Govee, "AA", "Govee H5179", "Sufragerie", 21.4, 48),
            S(ClimateSources.Viessmann, ViessmannClimate.ExternalId(Boiler, ViessmannClimate.RoomSuffix), ViessmannClimate.RoomName, "Hol", 22.1),
            S(ClimateSources.Viessmann, ViessmannClimate.ExternalId(Boiler, ViessmannClimate.SupplySuffix), ViessmannClimate.SupplyName, "", 48),
            S(ClimateSources.Viessmann, ViessmannClimate.ExternalId(Boiler, ViessmannClimate.DhwSuffix), ViessmannClimate.DhwName, "", 45),
            S(ClimateSources.Shelly, "BB", "H&T birou", "Birou", null),
            S(ClimateSources.Govee, "CC", "Govee vechi", "Dormitor", 19, archived: true),
            S(ClimateSources.Shelly, "DD", "H&T baie", "Baie", 23.0, 60, ageMin: 300),
        };

        var r = ClimateOverview.Build(sensors, Now, s => s.Sensor.ExternalId == "DD");

        r.Select(x => x.Room).Should().Equal("Baie", "Hol", "Pod", "Sufragerie", "Afară", "Afară");
        r.Where(x => x.IsOutdoor).Select(x => x.Name).Should().Equal("Govee balcon", "Open-Meteo");
        r.Single(x => x.Name == "Open-Meteo").Should().BeEquivalentTo(new { TemperatureC = 6.5, HumidityPercent = (double?)81, IsOutdoor = true });
        r.Single(x => x.Room == "Pod").IsOutdoor.Should().BeFalse("the „afară” tick decides, not the source");
        r.Single(x => x.Room == "Baie").Stale.Should().BeTrue();
        r.Single(x => x.Room == "Sufragerie").AgeSeconds.Should().Be(60);
        r.Should().NotContain(x => x.Name == ViessmannClimate.SupplyName || x.Name == ViessmannClimate.DhwName, "water temperatures stay on the boiler card");
    }

    [Fact]
    public void NoSensorWithAValue_TheCardIsLeftOut()
    {
        var state = new ClimateState();
        var overview = new ClimateOverview(state);
        overview.Readings(Now).Should().BeEmpty();
        state.Publish(S(ClimateSources.OpenMeteo, "home", "Open-Meteo", "", 5, outdoor: true));
        overview.Readings(Now).Should().ContainSingle().Which.Room.Should().Be("Afară");
    }
}
