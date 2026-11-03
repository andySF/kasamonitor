using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Mqtt;
using KasaMonitor.Core.Tests.Mqtt;
using Xunit;

namespace KasaMonitor.Core.Tests.Devices;

/// <summary>0.11, F2: classification, write rule, hourly rollup, metric families (R5), history bars, R4, R11.</summary>
public class GenericRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<DeviceEntity> FixtureDevice(string externalId)
    {
        var reg = new MqttRegistry();
        foreach (var m in MqttFixtures.All("z2m-discovery.jsonl", "esphome-discovery.jsonl", "ha-discovery.jsonl"))
            reg.ApplyDiscovery(MqttDiscoveryParser.Parse(m.Topic, m.Payload), T0);
        return reg.Get(externalId, T0)!.Entities.Select(e => e.Spec.Describe("dev:1")).ToList();
    }

    [Fact]
    public void Classify_Fixtures_MeterClimateGenericAndMixed()
    {
        var plug = GenericClassifier.Classify(FixtureDevice("zigbee2mqtt_0xaabbccddee000002"));
        plug.Category.Should().Be(DeviceCategory.Meter, "power + energy counter");
        plug.Capabilities.Should().Contain("releu").And.NotContain("putere");

        var sensor = GenericClassifier.Classify(FixtureDevice("zigbee2mqtt_0xaabbccddee000001"));
        sensor.Category.Should().Be(DeviceCategory.ClimateSensor);
        sensor.Label.Should().Be("Senzor temperatură și umiditate");
        sensor.TemperatureKey.Should().Be("dev:1/0xaabbccddee000001_temperature_zigbee2mqtt");
        sensor.HumidityKey.Should().EndWith("_humidity_zigbee2mqtt");
        sensor.BatteryKey.Should().EndWith("_battery_zigbee2mqtt", "a diagnostic battery is paired too");

        var door = GenericClassifier.Classify(FixtureDevice("zigbee2mqtt_0xaabbccddee000003"));
        door.Category.Should().Be(DeviceCategory.Generic);
        door.Label.Should().Be("Senzor ușă");
        door.TemperatureKey.Should().BeNull();

        var co2 = GenericClassifier.Classify(FixtureDevice("aabbccddee04"));
        co2.Category.Should().Be(DeviceCategory.Relay, "ESPHome board with a relay: the switch rule comes before the temperature");
        co2.Capabilities.Should().Contain("temperatură");
        co2.TemperatureKey.Should().NotBeNull("its temperature is still routed to Climă");
    }

    [Fact]
    public void Classify_Priorities()
    {
        DeviceEntity E(string slug, EntityKind k, DeviceClass c, StateClass? sc, EntityCategory cat = EntityCategory.Primary) =>
            new("dev:1/" + slug, slug, k, k is EntityKind.Switch or EntityKind.Binary ? EntityDataType.Boolean : EntityDataType.Number, c, null, null, sc, null, false, cat);
        var relay = E("sw", EntityKind.Switch, DeviceClass.None, null);
        var temp = E("t", EntityKind.Sensor, DeviceClass.Temperature, StateClass.Measurement);
        GenericClassifier.Classify([relay]).Category.Should().Be(DeviceCategory.Relay);
        GenericClassifier.Classify([relay, temp]).Should().Match<GenericClassification>(c => c.Category == DeviceCategory.Relay && c.Capabilities.Contains("temperatură"),
            "the switch rule comes before the temperature; the temperature is a badge (and still routed to Climă)");
        GenericClassifier.Classify([relay, temp]).TemperatureKey.Should().Be("dev:1/t");
        GenericClassifier.Classify([E("dt", EntityKind.Sensor, DeviceClass.Temperature, StateClass.Measurement, EntityCategory.Diagnostic)])
            .Category.Should().Be(DeviceCategory.Generic, "a device temperature (diagnostic) is not a climate sensor");
        GenericClassifier.Classify([E("co2", EntityKind.Sensor, DeviceClass.CarbonDioxide, StateClass.Measurement)]).Label.Should().Be("Senzor CO₂");
        GenericClassifier.AllowedOverrides.Should().NotContain(DeviceCategory.Plug, "„Priză” needs a dedicated adapter (totals, NILM)");
    }

    private static readonly DeviceEntity Power = new("dev:1/p", "P", EntityKind.Sensor, EntityDataType.Number, DeviceClass.Power, null, "W", StateClass.Measurement);
    private static readonly DeviceEntity Temp = new("dev:1/t", "T", EntityKind.Sensor, EntityDataType.Number, DeviceClass.Temperature, null, "°C", StateClass.Measurement);

    [Fact]
    public void WriteRule_FirstChangeHeartbeat_AndAtMostOneRowIn10Seconds()
    {
        GenericWriteRule.Decide(null, T0, 21, null, 0.1).Should().Be(GenericWriteDecision.Write);
        var last = new GenericStored(T0, 21, null);
        GenericWriteRule.Decide(last, T0.AddSeconds(3), 23, null, 0.1).Should().Be(GenericWriteDecision.Defer, "a change inside 10 s waits");
        GenericWriteRule.Decide(last, T0.AddSeconds(3), 21.05, null, 0.1).Should().Be(GenericWriteDecision.Skip);
        GenericWriteRule.Decide(last, T0.AddSeconds(30), 21.1, null, 0.1).Should().Be(GenericWriteDecision.Write);
        GenericWriteRule.Decide(last, T0.AddMinutes(20), 21.05, null, 0.1).Should().Be(GenericWriteDecision.Skip);
        GenericWriteRule.Decide(last, T0.AddMinutes(30), 21.0, null, 0.1).Should().Be(GenericWriteDecision.Write, "heartbeat");
        GenericWriteRule.Decide(last, T0, 25, null, 0.1).Should().Be(GenericWriteDecision.Skip, "not newer");
        GenericWriteRule.Decide(new GenericStored(T0, null, "idle"), T0.AddMinutes(1), null, "running", 0).Should().Be(GenericWriteDecision.Write);
    }

    [Fact]
    public void WriteRule_Thresholds_FromPrecisionElseTheClass()
    {
        GenericWriteRule.Threshold(Temp, 21).Should().Be(0.1);
        GenericWriteRule.Threshold(Temp with { Precision = 2 }, 21).Should().BeApproximately(0.01, 1e-12);
        GenericWriteRule.Threshold(Power, 20).Should().Be(1);
        GenericWriteRule.Threshold(Power, 2000).Should().Be(40, "2 % of the last value");
        GenericWriteRule.Threshold(Temp with { Class = DeviceClass.Humidity }, 50).Should().Be(1);
    }

    [Fact]
    public void Rollup_TimeWeightedMean_CarriedValue_AndAGapAfterTheCarry()
    {
        var points = new[] { new GenericPoint(T0.AddMinutes(15), 20), new GenericPoint(T0.AddMinutes(45), 22) };
        var h = GenericRollup.Hours(points, new GenericPoint(T0.AddMinutes(-30), 18), StateClass.Measurement, T0, T0.AddHours(4), TimeSpan.FromHours(2));
        h[0].Min.Should().Be(18);
        h[0].Max.Should().Be(22);
        h[0].Avg.Should().BeApproximately((18 * 15 + 20 * 30 + 22 * 15) / 60.0, 1e-9);
        h[0].Count.Should().Be(2);
        h[0].Last.Should().Be(22);
        h[1].Avg.Should().Be(22, "the last value holds");
        h[1].Count.Should().Be(0);
        h.Should().HaveCount(3, "carried until 2 h after the last reading (10:45 → 12:45), then a gap");
        h[2].HourUtc.Should().Be(T0.AddHours(2));
        h[2].Avg.Should().Be(22);
    }

    [Fact]
    public void Rollup_Counters_IncreasesAndResets_BinaryShare()
    {
        var points = new[] { new GenericPoint(T0.AddMinutes(10), 105), new GenericPoint(T0.AddMinutes(20), 110), new GenericPoint(T0.AddMinutes(30), 3), new GenericPoint(T0.AddMinutes(40), 8) };
        var inc = GenericRollup.Hours(points, new GenericPoint(T0.AddMinutes(-5), 100), StateClass.TotalIncreasing, T0, T0.AddHours(1), TimeSpan.FromHours(2));
        inc.Single().Delta.Should().Be(5 + 5 + 3 + 5, "a decrease is a reset: the new value is what was counted since");
        var net = GenericRollup.Hours(points, new GenericPoint(T0.AddMinutes(-5), 100), StateClass.Total, T0, T0.AddHours(1), TimeSpan.FromHours(2));
        net.Single().Delta.Should().Be(8 - 100);

        var door = new[] { new GenericPoint(T0.AddMinutes(15), 1), new GenericPoint(T0.AddMinutes(30), 0) };
        GenericRollup.Hours(door, new GenericPoint(T0.AddHours(-3), 0), null, T0, T0.AddHours(1), TimeSpan.FromDays(7)).Single().Avg
            .Should().BeApproximately(0.25, 1e-9, "open 15 of 60 minutes");
    }

    [Fact]
    public void History_Aggregate_MinMeanMaxSumAndTimeOn()
    {
        var hours = new[]
        {
            new GenericHour(T0, 1, 2, 3, 2, 4, 10),
            new GenericHour(T0.AddHours(1), 0, 4, 6, 5, 2, 5),
            new GenericHour(T0.AddHours(5), 0, 0.5, 1, 1, 1, null),
        };
        var b = GenericHistory.Aggregate(hours, new[] { (T0, T0.AddHours(2)), (T0.AddHours(2), T0.AddHours(4)), (T0.AddHours(4), T0.AddHours(6)) });
        b[0].Should().Be(new GenericBucketStat(0, 3, 6, 15, 2));
        b[1].Should().Be(GenericBucketStat.Empty);
        b[2].OnSeconds.Should().Be(1800);
    }

    [Fact]
    public void MetricFamilies_OneUnitPerFamily_R5()
    {
        GenericMetricFamilies.For(Temp).Should().Be(GenericMetricFamilies.Temperature);
        GenericMetricFamilies.For(Power).Should().Be(GenericMetricFamilies.Power);
        GenericMetricFamilies.For(Power with { Class = DeviceClass.Energy, Unit = "Wh", StateClass = StateClass.TotalIncreasing })!.Counter.Should().BeTrue();
        GenericMetricFamilies.For(Power with { Class = DeviceClass.None, Unit = "lqi" }).Should().Be(GenericMetricFamilies.Value, "an unknown class goes to entity_value{unit}");
        GenericMetricFamilies.For(new DeviceEntity("dev:1/d", "Ușă", EntityKind.Binary, EntityDataType.Boolean, DeviceClass.Door)).Should().Be(GenericMetricFamilies.State);
        GenericMetricFamilies.For(new DeviceEntity("dev:1/s", "Stare", EntityKind.Sensor, EntityDataType.Text)).Should().BeNull();
        GenericMetricFamilies.All.Select(f => f.Name).Should().OnlyHaveUniqueItems();
        // Each canonical unit maps to exactly one family name (no family mixes units).
        GenericMetricFamilies.All.Where(f => f != GenericMetricFamilies.Value).Should().OnlyContain(f => f.Name.StartsWith("entity_", StringComparison.Ordinal));
    }

    [Fact]
    public void R4_TheMqttClimateSourceIsTheIntegrationKey_R11_UnknownProtocolIsAnError()
    {
        ClimateSources.Mqtt.Should().Be(IntegrationKeys.Mqtt);
        ClimateSources.All.Should().Contain(ClimateSources.Mqtt);
        ClimateSources.DisplayName(ClimateSources.Mqtt).Should().Be("MQTT");
        PlugProtocols.Integration(PlugProtocols.ShellyRpc).Should().Be(IntegrationKeys.Shelly);
        foreach (var p in new[] { PlugProtocols.Legacy, PlugProtocols.Klap, PlugProtocols.Aes, PlugProtocols.Unsupported })
            PlugProtocols.Integration(p).Should().Be(IntegrationKeys.TpLink);
        var act = () => PlugProtocols.Integration("zigbee");
        act.Should().Throw<ArgumentOutOfRangeException>().WithMessage("*zigbee*");
        DeviceCategories.All.TakeLast(2).Should().Equal(DeviceCategory.Relay, DeviceCategory.Generic);
        DeviceCategories.Slug(DeviceCategory.Generic).Should().Be("generic");
    }
}
