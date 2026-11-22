using FluentAssertions;
using KasaMonitor.Core.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Heating;

/// <summary>
/// vicare_b0ka_features.json: the real bulk features answer of a Vitodens 050-W B0KA (2026-11-01, 73 features) with the
/// installation id, gateway serial, boiler serial, ETN and Zigbee id replaced by fictitious values.
/// vicare_synthetic_features.json: hand-written in the PyViCare shape (boiler/outside/return sensors, day[] series in kWh).
/// </summary>
internal static class VicareFixtures
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));
    public static string B0ka => Text("vicare_b0ka_features.json");
    public static string Synthetic => Text("vicare_synthetic_features.json");
    public static string Installations => Text("vicare_installations.json");
}

public class HeatingParserTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 11, 1, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void B0ka_MapsTheFeaturesTheBoilerHas()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.B0ka, ReadAt);

        s.FeatureCount.Should().Be(73);
        s.SupplyTempC.Should().Be(36.2);
        s.BoilerTargetC.Should().Be(20);
        s.RoomTempC.Should().Be(23.7);
        s.DhwTempC.Should().Be(34.1);
        s.DhwTargetC.Should().Be(45);
        s.BurnerActive.Should().BeFalse();
        s.ModulationPct.Should().Be(0);
        s.PumpPct.Should().Be(0);
        s.BurnerStarts.Should().Be(31089);
        s.BurnerHours.Should().Be(2891);
        s.OperatingMode.Should().Be("heating");
        s.ActiveProgram.Should().Be("normal");
        s.ProgramTemps.Should().BeEquivalentTo(new Dictionary<string, double> { ["normal"] = 23, ["comfort"] = 22, ["reduced"] = 21.5 });
        s.RoomTargetC.Should().Be(23, "the temperature of the active program");
        HeatingReading.From(Guid.NewGuid(), s).RoomTargetC.Should().Be(23, "a reading keeps the room target");
        new HeatingSnapshot { ActiveProgram = "reduced", ProgramTemps = s.ProgramTemps }.RoomTargetC.Should().Be(21.5);
        new HeatingSnapshot { ActiveProgram = "standby", ProgramTemps = s.ProgramTemps }.RoomTargetC.Should().BeNull("a program without a temperature has no target");
        HeatingProgram.Target(null, s.ProgramTemps).Should().BeNull();
        HeatingProgram.Target("normal", null).Should().BeNull();
        s.SupplyTargetC.Should().Be(0, "no demand at the moment of the dump");
        s.SupplyMinC.Should().Be(20);
        s.SupplyMaxC.Should().Be(82);
        s.EcoActive.Should().BeFalse("normalEnergySaving.active = false");
        s.EcoReason.Should().BeNull("the reason only when the energy saving is on");
        s.CircuitPumpOn.Should().BeNull("the dump of 1 Nov has no heating.circuits.0.circulation.pump");
        s.CircuitName.Should().Be("Centrala bucatarie");
        s.Schedule.Should().HaveCount(7).And.OnlyContain(x => x.Mode == "normal" && x.Start == "06:00" && x.End == "22:00");
        s.FrostProtection.Should().Be("off");
        s.DhwMode.Should().Be("eco");
        s.DhwActive.Should().BeTrue();
        s.DhwStatus.Should().Be("on");
        s.DhwComfort.Should().BeFalse();
        s.DhwOneTimeCharge.Should().BeFalse();
        s.DhwScaldProtection.Should().BeTrue();
        s.MalfunctionLock.Should().BeFalse();
        s.WifiDbm.Should().Be(-65);
        s.RoomSensorBatteryPct.Should().Be(63);
        s.RoomSensorLqiPct.Should().Be(56);
        s.GasType.Should().Be("EGas");
        s.DaylightSaving.Should().BeTrue();
    }

    [Fact]
    public void B0ka_SensorsTheBoilerLacks_StayNull()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.B0ka, ReadAt);

        s.PressureBar.Should().BeNull("heating.sensors.pressure.supply has status notConnected");
        s.ReturnTempC.Should().BeNull();
        s.OutsideTempC.Should().BeNull();
        s.BoilerTempC.Should().BeNull("heating.boiler.temperature is the target, not a sensor");
    }

    [Fact]
    public void B0ka_Messages_StatusCodeIsTheNewest_NoActiveError()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.B0ka, ReadAt);

        s.Messages.Select(m => m.Code).Should().Equal("S.6", "S.1");
        s.Messages.Should().OnlyContain(m => m.Source == "status" && m.Priority == "status");
        s.StatusCode.Should().Be("S.6");
        s.ActiveError.Should().BeNull();
    }

    [Fact]
    public void B0ka_GasIsInCubicMeters_PerPurpose()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.B0ka, ReadAt);

        s.GasHeating.Should().NotBeNull();
        s.GasHeating!.Unit.Should().Be(EnergyUnit.CubicMeter);
        s.GasHeating.CurrentDay.Should().Be(1.3);
        s.GasHeating.LastSevenDays.Should().Be(8.5);
        s.GasHeating.CurrentMonth.Should().Be(1.3);
        s.GasHeating.LastMonth.Should().Be(14.6);
        s.GasHeating.CurrentYear.Should().Be(368.8);
        s.GasHeating.LastYear.Should().Be(295.7);
        s.GasHeating.Days.Should().BeNull("the B0KA has no day[] series");
        s.GasHeating.FeatureTs.Should().Be(DateTimeOffset.Parse("2026-11-01T15:44:42.641Z"));
        s.GasDhw!.CurrentDay.Should().Be(0.1);
        s.GasDhw.LastMonth.Should().Be(14.8);
        s.HeatHeating!.Unit.Should().Be(EnergyUnit.KilowattHour);
        s.HeatHeating.CurrentDay.Should().Be(14);
        s.HeatDhw!.LastMonth.Should().Be(148.3);
        s.PowerHeating!.CurrentDay.Should().Be(0.3);
        s.PowerDhw!.CurrentYear.Should().Be(11.7);
    }

    [Fact]
    public void B0ka_UnmappedFeatures_AreListedNotErrors()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.B0ka, ReadAt);
        var names = s.Unmapped.Select(u => u.Name).ToList();

        names.Should().Contain(new[] { "device.serial", "device.etn", "heating.configuration.houseLocation", "gateway.devices",
            "device.identification" });
        names.Should().NotContain(new[] { "heating.gas.consumption.summary.dhw", "heating.dhw.sensors.temperature.outlet",
            "heating.dhw.sensors.temperature.hotWaterStorage", "heating.circuits.0.sensors.temperature.supply", "device.power.battery",
            "tcu.wifi", "gateway.wifi", "heating.sensors.pressure.supply", "heating.circuits.0.name", "heating.circuits.0",
            "heating.circuits.0.operating.programs.normalEnergySaving", "heating.circuits.0.operating.programs.reducedEnergySaving",
            "heating.circuits.0.operating.programs.comfortEnergySaving", "heating.circuits.0.temperature", "heating.circuits.0.temperature.levels" });
        (s.FeatureCount - s.Unmapped.Count).Should().BeGreaterThan(40);
    }

    [Fact]
    public void Synthetic_PyViCareShape_SensorsSeriesAndErrors()
    {
        var s = HeatingFeatureParser.Parse(VicareFixtures.Synthetic, ReadAt);

        s.BoilerTempC.Should().Be(55.5);
        s.BoilerTargetC.Should().Be(60);
        s.OutsideTempC.Should().Be(-3.5);
        s.ReturnTempC.Should().Be(41.2);
        s.PressureBar.Should().Be(1.6);
        s.SupplyTempC.Should().Be(48.0, "without commonSupply the circuit's supply sensor is used");
        s.RoomTempC.Should().BeNull("status notConnected");
        s.BurnerActive.Should().BeTrue();
        s.ModulationPct.Should().Be(37);
        s.BurnerStarts.Should().Be(4321);
        s.BurnerHours.Should().Be(1234.5);
        s.ActiveError.Should().Be("F.160");
        s.Unmapped.Select(u => u.Name).Should().Equal("heating.something.new");

        var gas = s.GasHeating!;
        gas.Unit.Should().Be(EnergyUnit.KilowattHour);
        gas.Days.Should().Equal(12.0, 30.5, 28.0, 25.0, 20.0, 22.0, 18.0, 15.0);
        gas.CurrentDay.Should().Be(12.0, "day[0] = today");
        gas.LastSevenDays.Should().Be(12.0 + 30.5 + 28.0 + 25.0 + 20.0 + 22.0 + 18.0);
        gas.CurrentMonth.Should().Be(300);
        gas.LastMonth.Should().Be(900);
        gas.CurrentYear.Should().Be(5000);
        gas.LastYear.Should().Be(9000);
        gas.DaysReadAt.Should().Be(DateTimeOffset.Parse("2026-11-01T09:00:00Z"));
        s.GasDhw!.LastSevenDays.Should().BeNull("only two days in the series");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\": []}")]
    [InlineData("{\"data\": [{\"feature\": 5}, {\"no\": \"feature\"}]}")]
    public void EmptyOrOddAnswers_GiveAnEmptySnapshot(string json)
    {
        var s = HeatingFeatureParser.Parse(json, ReadAt);
        s.FeatureCount.Should().Be(0);
        s.SupplyTempC.Should().BeNull();
        s.GasHeating.Should().BeNull();
    }

    [Theory]
    [InlineData("cubicMeter", EnergyUnit.CubicMeter)]
    [InlineData("kilowattHour", EnergyUnit.KilowattHour)]
    [InlineData("", EnergyUnit.Unknown)]
    [InlineData(null, EnergyUnit.Unknown)]
    public void Units(string? unit, EnergyUnit expected) => HeatingFeatureParser.ParseUnit(unit).Should().Be(expected);

    [Fact]
    public void Installations_PickTheBoiler()
    {
        var list = ViessmannInstallations.Parse(VicareFixtures.Installations);
        list.Should().ContainSingle();
        list[0].Gateways.Single().Devices.Select(d => d.Id).Should().Equal("0", "zigbee-0011223344556677", "TCU");

        var boiler = ViessmannInstallations.PickBoiler(list);
        boiler.Should().Be(new ViessmannBoilerRef("1000001", "1111222233334444", "0", "E3_Vitodens_050_0822", "WorksProperly"));
        ViessmannInstallations.FriendlyModel(boiler!.ModelId).Should().Be("Vitodens 050");
        ViessmannInstallations.PickBoiler(ViessmannInstallations.Parse("{\"data\": []}")).Should().BeNull();
    }

    [Fact]
    public void FeaturesUrl_IsTheBulkGatewayCall()
    {
        ViessmannApi.FeaturesUrl("1000001", "1111222233334444").Should().Be(
            "https://api.viessmann-climatesolutions.com/iot/v2/features/installations/1000001/gateways/1111222233334444/features?includeDevicesFeatures=true");
        ViessmannApi.InstallationsUrl.Should().Be("https://api.viessmann-climatesolutions.com/iot/v2/equipment/installations?includeGateways=true");
        ViessmannApi.CallsPerDay(120).Should().Be(720);
        ViessmannApi.CallsPerDay(60).Should().Be(1440);
    }

    /// <summary>The B0KA dump with the given features' properties replaced (or the feature added for the boiler, device "0").</summary>
    private static string B0kaWith(params (string Feature, string PropertiesJson)[] changes)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(VicareFixtures.B0ka)!;
        var data = root["data"]!.AsArray();
        foreach (var (feature, props) in changes)
        {
            var f = data.FirstOrDefault(x => (string?)x!["feature"] == feature && (string?)x["deviceId"] == "0");
            if (f is null)
            {
                f = new System.Text.Json.Nodes.JsonObject { ["feature"] = feature, ["deviceId"] = "0" };
                data.Add(f);
            }
            f["properties"] = System.Text.Json.Nodes.JsonNode.Parse(props);
        }
        return root.ToJsonString();
    }

    [Fact]
    public void Eco_IsTheEnergySavingOfTheActiveProgram_WithItsReason()
    {
        const string on = """{"active":{"type":"boolean","value":true},"reason":{"type":"string","value":"summerEco"},"demand":{"type":"string","value":"heating"}}""";
        const string reduced = """{"value":{"type":"string","value":"reduced"}}""";

        var normalOn = HeatingFeatureParser.Parse(B0kaWith(("heating.circuits.0.operating.programs.normalEnergySaving", on)), ReadAt);
        normalOn.EcoActive.Should().BeTrue();
        normalOn.EcoReason.Should().Be("summerEco");

        var otherProgram = HeatingFeatureParser.Parse(B0kaWith(("heating.circuits.0.operating.programs.normalEnergySaving", on),
            ("heating.circuits.0.operating.programs.active", reduced)), ReadAt);
        otherProgram.EcoActive.Should().BeFalse("only the energy saving of the active program counts");
        otherProgram.EcoReason.Should().BeNull();

        var ecoProgram = HeatingFeatureParser.Parse(B0kaWith(("heating.circuits.0.operating.programs.active",
            """{"value":{"type":"string","value":"normalEnergySaving"}}""")), ReadAt);
        ecoProgram.EcoActive.Should().BeTrue("the active program is itself an energy saving one");
        ecoProgram.RoomTargetC.Should().BeNull("an energy saving program has no temperature of its own");
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void CircuitPump_AndSupplyTarget(string status, bool expected)
    {
        var s = HeatingFeatureParser.Parse(B0kaWith(("heating.circuits.0.circulation.pump", $$$"""{"status":{"type":"string","value":"{{{status}}}"}}"""),
            ("heating.circuits.0.temperature", """{"value":{"type":"number","value":40.8,"unit":"celsius"}}""")), ReadAt);

        s.CircuitPumpOn.Should().Be(expected);
        s.SupplyTargetC.Should().Be(40.8);
        s.Unmapped.Select(u => u.Name).Should().NotContain("heating.circuits.0.circulation.pump");
        var r = HeatingReading.From(Guid.NewGuid(), s);
        r.CircuitPumpOn.Should().Be(expected);
        r.SupplyTargetC.Should().Be(40.8);
    }
}
