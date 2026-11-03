using FluentAssertions;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Mqtt;
using Xunit;

namespace KasaMonitor.Core.Tests.Mqtt;

/// <summary>0.11, F2: MQTT Discovery — both formats, abbreviations, the ~ base, the template subset, the mapping, availability, the registry (on fixtures from documentation).</summary>
public class MqttDiscoveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static MqttDiscoveryResult Parse(string file, int index) { var m = MqttFixtures.Load(file)[index]; return MqttDiscoveryParser.Parse(m.Topic, m.Payload); }

    [Fact]
    public void PerEntityFormat_Z2m_ParsesTopicDeviceAndConfig()
    {
        var r = Parse("z2m-discovery.jsonl", 0);
        r.Kind.Should().Be(MqttDiscoveryKind.Config);
        var c = r.Components.Single();
        c.Component.Should().Be("sensor");
        c.NodeId.Should().Be("0xaabbccddee000001");
        c.ObjectId.Should().Be("temperature");
        c.UniqueId.Should().Be("0xaabbccddee000001_temperature_zigbee2mqtt");
        c.Device!.ExternalId.Should().Be("zigbee2mqtt_0xaabbccddee000001");
        c.Device.Manufacturer.Should().Be("Aqara");
        c.Device.ViaDevice.Should().Be("zigbee2mqtt_bridge_0xaabbccddee0000ff");
    }

    [Fact]
    public void Abbreviations_EspHome_AreExpanded_InTheRootTheDeviceAndAvailability()
    {
        var c = Parse("esphome-discovery.jsonl", 0).Components.Single();
        c.Config.ContainsKey("state_topic").Should().BeTrue();
        c.Config.ContainsKey("stat_t").Should().BeFalse();
        MqttJson.Text(c.Config["device_class"]).Should().Be("carbon_dioxide");
        MqttJson.Text(c.Config["unit_of_measurement"]).Should().Be("ppm");
        c.UniqueId.Should().Be("ESPsensorco2");
        c.Device!.Identifiers.Should().Equal("aabbccddee04");
        c.Device.Connections.Should().ContainSingle().Which.Should().Be(("mac", "aabbccddee04"));
        c.Device.Model.Should().Be("esp32dev");
        c.Device.SwVersion.Should().Be("esphome v2025.5.0");
        // An availability list item uses the same table („t” → topic).
        var item = MqttDiscoveryParser.Parse("homeassistant/sensor/x/y/config", """{"uniq_id":"u","stat_t":"a","avty":[{"t":"b/status","pl_avail":"up"}]}""")
            .Components.Single().Config["availability"]!.AsArray()[0]!.AsObject();
        MqttJson.Text(item["topic"]).Should().Be("b/status");
        MqttJson.Text(item["payload_available"]).Should().Be("up");
    }

    [Fact]
    public void TopicBase_IsSubstitutedInEveryTopicKey()
    {
        var c = Parse("ha-discovery.jsonl", 1).Components.Single();
        MqttJson.Text(c.Config["command_topic"]).Should().Be("homeassistant/light/kitchen/set");
        MqttJson.Text(c.Config["state_topic"]).Should().Be("homeassistant/light/kitchen/state");
        c.Config.ContainsKey("~").Should().BeFalse();
        var suffix = MqttDiscoveryParser.Parse("homeassistant/sensor/n/o/config", """{"~":"base","uniq_id":"u","stat_t":"pre/~"}""").Components.Single();
        MqttJson.Text(suffix.Config["state_topic"]).Should().Be("pre/base");
    }

    [Fact]
    public void DeviceBasedFormat_ComponentsInheritTheSharedOptions()
    {
        var r = Parse("ha-discovery.jsonl", 0);
        r.Kind.Should().Be(MqttDiscoveryKind.Config);
        r.TopicComponent.Should().Be("device");
        r.Components.Should().HaveCount(2);
        r.Components.Select(c => c.Component).Should().OnlyContain(c => c == "sensor");
        r.Components.Should().OnlyContain(c => MqttJson.Text(c.Config["state_topic"]) == "sensorBedroom/state");
        r.Components.Should().OnlyContain(c => c.Device!.ExternalId == "0AFFD2");
        r.Components.Select(c => c.UniqueId).Should().BeEquivalentTo("temp01ae_t", "temp01ae_h");
    }

    [Fact]
    public void Removal_Invalid_AndOtherTopics()
    {
        MqttDiscoveryParser.Parse("homeassistant/sensor/a/b/config", "").Kind.Should().Be(MqttDiscoveryKind.Removed);
        MqttDiscoveryParser.Parse("homeassistant/sensor/a/b/config", "{nu e json").Kind.Should().Be(MqttDiscoveryKind.Invalid);
        MqttDiscoveryParser.Parse("homeassistant/status", "online").Kind.Should().Be(MqttDiscoveryKind.NotDiscovery);
        MqttDiscoveryParser.Parse("zigbee2mqtt/Priza birou", "{}").Kind.Should().Be(MqttDiscoveryKind.NotDiscovery);
        MqttDiscoveryParser.Parse("ha/sensor/a/config", """{"uniq_id":"x","stat_t":"t"}""", "ha").Kind.Should().Be(MqttDiscoveryKind.Config, "a custom prefix");
        MqttDiscoveryParser.Parse("homeassistant/device/a/config", """{"dev":{"ids":"a"}}""").Kind.Should().Be(MqttDiscoveryKind.Invalid);
    }

    // ---- templates ----

    [Theory]
    [InlineData("{{ value }}", "21.5", "21.5")]
    [InlineData("{{ value_json.temperature }}", """{"temperature":21.37}""", "21.37")]
    [InlineData("{{ value_json['temperature'] }}", """{"temperature":21}""", "21")]
    [InlineData("{{ value_json.a.b[1] }}", """{"a":{"b":[1,2,3]}}""", "2")]
    [InlineData("{{ value_json.contact }}", """{"contact":false}""", "False")]
    [InlineData("{{ value_json.state }}", """{"state":"ON"}""", "ON")]
    [InlineData("{{ value_json.p | float * 1000 }}", """{"p":"1.5"}""", "1500.0")]
    [InlineData("{{ (value_json.t * 10) | round(1) }}", """{"t":2.345}""", "23.5")]
    [InlineData("{{ value | int }}", "42.9", "42")]
    [InlineData("{{ value_json.missing | default(0) }}", """{"a":1}""", "0")]
    [InlineData("{{ value_json.s | upper }}", """{"s":"on"}""", "ON")]
    [InlineData("{{ value_json.missing }}", """{"a":1}""", "")]
    [InlineData("{{ -value_json.x + 2 }}", """{"x":5}""", "-3")]
    [InlineData("{{ value_json.x / 4 }}", """{"x":2}""", "0.5")]
    public void Template_Subset_RendersLikeJinja(string template, string payload, string expected)
    {
        ValueTemplate.TryParse(template, out var t, out var error).Should().BeTrue(error);
        t!.Render(payload).Should().Be(expected);
    }

    [Theory]
    [InlineData("{% if value_json.p %}{{ value_json.p }}{% endif %}")]
    [InlineData("{{ value_json.t | timestamp_custom('%H') }}")]
    [InlineData("valoare: {{ value }}")]
    [InlineData("{{ states('sensor.x') }}")]
    [InlineData("{{ value_json.x ** 2 }}")]
    [InlineData("{{ value }} {{ value }}")]
    [InlineData("{{ value | round(1, 'floor') }}")]
    public void Template_OutsideTheSubset_IsRefused(string template) =>
        ValueTemplate.TryParse(template, out _, out var error).Should().BeFalse(error);

    [Fact]
    public void Template_FailingEvaluation_RendersNothing()
    {
        ValueTemplate.TryParse("{{ value | float }}", out var t, out _);
        t!.Render("abc").Should().BeNull("float of a text without a default fails; the raw payload is never used instead");
        ValueTemplate.TryParse("{{ value_json.a * 2 }}", out var u, out _);
        u!.Render("""{"b":1}""").Should().BeNull();
    }

    // ---- mapping ----

    private static MqttEntitySpec Spec(string file, int index) => MqttEntityMapper.Map(Parse(file, index).Components.Single())!;

    [Fact]
    public void Mapping_Z2mSensors_ClassUnitStateClassCategory_AndConversions()
    {
        var t = Spec("z2m-discovery.jsonl", 0);
        (t.Kind, t.DataType, t.Class, t.Unit, t.StateClass, t.Category).Should().Be((EntityKind.Sensor, EntityDataType.Number, DeviceClass.Temperature, "°C",
            StateClass.Measurement, EntityCategory.Primary));
        t.Availability.Should().HaveCount(2);
        t.AvailabilityMode.Should().Be(MqttAvailabilityMode.All);
        Spec("z2m-discovery.jsonl", 2).Class.Should().Be(DeviceClass.Pressure);
        var battery = Spec("z2m-discovery.jsonl", 3);
        (battery.Class, battery.Category).Should().Be((DeviceClass.Battery, EntityCategory.Diagnostic));
        var mv = Spec("z2m-discovery.jsonl", 4);
        (mv.Class, mv.Unit).Should().Be((DeviceClass.Voltage, "V"));
        mv.Conversion.Apply(2985).Should().BeApproximately(2.985, 1e-9);
        var lqi = Spec("z2m-discovery.jsonl", 5);
        (lqi.Class, lqi.Unit, lqi.Name).Should().Be((DeviceClass.None, "lqi", "Linkquality"));
        var energy = Spec("z2m-discovery.jsonl", 8);
        (energy.Class, energy.Unit, energy.StateClass).Should().Be((DeviceClass.Energy, "Wh", StateClass.TotalIncreasing));
        energy.Conversion.Apply(12.34).Should().BeApproximately(12_340, 1e-6);
    }

    [Fact]
    public void Mapping_SwitchContactAndEspHome()
    {
        var sw = Spec("z2m-discovery.jsonl", 6);
        sw.Kind.Should().Be(EntityKind.Switch);
        sw.Writable.Should().BeTrue();
        sw.Command!.Topic.Should().Be("zigbee2mqtt/Priza birou/set");
        sw.Name.Should().Be("Priza birou", "name null: the device's name");
        var door = Spec("z2m-discovery.jsonl", 11);
        (door.Kind, door.Class, door.PayloadOn, door.PayloadOff).Should().Be((EntityKind.Binary, DeviceClass.Door, "False", "True"));
        var co2 = Spec("esphome-discovery.jsonl", 0);
        (co2.Class, co2.Unit, co2.Availability.Single().Topic).Should().Be((DeviceClass.CarbonDioxide, "ppm", "senzor-co2/status"));
        Spec("esphome-discovery.jsonl", 1).Precision.Should().Be(1);
        Spec("esphome-discovery.jsonl", 2).Category.Should().Be(EntityCategory.Diagnostic);
        var relay = Spec("esphome-discovery.jsonl", 3);
        (relay.Kind, relay.Command!.Optimistic).Should().Be((EntityKind.Switch, false));
    }

    [Fact]
    public void Mapping_UnsupportedComponentTemplateAndMissingUniqueId()
    {
        var light = Spec("ha-discovery.jsonl", 1);
        light.Kind.Should().Be(EntityKind.Unsupported);
        light.Problem.Should().Be("neacceptat (light)");
        var garden = Spec("ha-discovery.jsonl", 2);
        garden.Problem.Should().StartWith("șablon neacceptat");
        garden.Template.Should().BeNull();
        MqttValueParser.Parse(garden, """{"p":1.2}""").HasValue.Should().BeFalse("the raw payload is never used in place of a refused template");
        MqttEntityMapper.Map(Parse("ha-discovery.jsonl", 3).Components.Single()).Should().BeNull("no unique_id: cannot be adopted");
    }

    [Fact]
    public void ValueParser_ConvertsAndComparesPayloads()
    {
        MqttValueParser.Parse(Spec("z2m-discovery.jsonl", 4), """{"voltage":2985}""").Number.Should().BeApproximately(2.985, 1e-9);
        var door = Spec("z2m-discovery.jsonl", 11);
        MqttValueParser.Parse(door, """{"contact":false}""").Number.Should().Be(1, "contact false = open (payload_on)");
        MqttValueParser.Parse(door, """{"contact":true}""").Number.Should().Be(0);
        MqttValueParser.Parse(door, """{"battery":3}""").HasValue.Should().BeFalse();
        MqttValueParser.Parse(Spec("esphome-discovery.jsonl", 0), "612").Number.Should().Be(612);
        MqttValueParser.Parse(Spec("esphome-discovery.jsonl", 0), "unavailable").HasValue.Should().BeFalse();
        MqttValueParser.Parse(Spec("esphome-discovery.jsonl", 3), "ON").Number.Should().Be(1);
    }

    // ---- availability (R3) ----

    [Fact]
    public void Availability_AllMode_BridgeAndDevice_AndExpireAfter()
    {
        var t = Spec("z2m-discovery.jsonl", 0);
        var topics = new Dictionary<string, (bool? State, DateTimeOffset Ts)>();
        MqttAvailabilityRule.IsAvailable(t, topics, Now, Now).Should().BeFalse("a topic never heard is not available");
        topics["zigbee2mqtt/bridge/state"] = (true, Now);
        MqttAvailabilityRule.IsAvailable(t, topics, Now, Now).Should().BeFalse("all: the device's own topic too");
        topics["zigbee2mqtt/Dormitor senzor/availability"] = (true, Now);
        MqttAvailabilityRule.IsAvailable(t, topics, Now.AddHours(-5), Now).Should().BeTrue("available by its topics, whatever the value's age");
        topics["zigbee2mqtt/bridge/state"] = (false, Now);
        MqttAvailabilityRule.IsAvailable(t, topics, Now, Now).Should().BeFalse();

        var expiring = MqttEntityMapper.Map(MqttDiscoveryParser.Parse("homeassistant/sensor/e/t/config",
            """{"uniq_id":"e_t","stat_t":"e/t","dev_cla":"temperature","unit_of_meas":"°C","exp_aft":600}""").Components.Single())!;
        MqttAvailabilityRule.IsAvailable(expiring, topics, Now.AddMinutes(-5), Now).Should().BeTrue();
        MqttAvailabilityRule.IsAvailable(expiring, topics, Now.AddMinutes(-11), Now).Should().BeFalse("older than expire_after");
        KasaMonitor.Core.Climate.ClimateFreshness.MqttInterval(600).Should().Be(TimeSpan.FromMinutes(5));
        KasaMonitor.Core.Climate.ClimateFreshness.MqttInterval(null).Should().Be(TimeSpan.FromHours(1), "R3: a sensor that reports on change, not 10 min");
        KasaMonitor.Core.Climate.ClimateFreshness.Expected(KasaMonitor.Core.Climate.ClimateSources.Mqtt, false, 120, null).Should().Be(TimeSpan.FromHours(1));
    }

    // ---- registry on all the fixtures ----

    private static MqttRegistry Registry(bool states = true)
    {
        var reg = new MqttRegistry();
        foreach (var m in MqttFixtures.All("z2m-discovery.jsonl", "esphome-discovery.jsonl", "ha-discovery.jsonl"))
            reg.ApplyDiscovery(MqttDiscoveryParser.Parse(m.Topic, m.Payload), Now);
        if (states)
            foreach (var m in MqttFixtures.All("z2m-states.jsonl", "esphome-states.jsonl", "ha-states.jsonl"))
                reg.ApplyMessage(m.Topic, m.Payload, Now);
        return reg;
    }

    [Fact]
    public void Registry_OneDevicePerPhysicalDevice_TopicsAndProblems()
    {
        var reg = Registry(states: false);
        var devices = reg.Devices(Now);
        devices.Select(d => d.ExternalId).Should().BeEquivalentTo("zigbee2mqtt_0xaabbccddee000001", "zigbee2mqtt_0xaabbccddee000002",
            "zigbee2mqtt_0xaabbccddee000003", "aabbccddee04", "0AFFD2", "kitchen-light", "garden-meter");
        devices.Single(d => d.ExternalId == "zigbee2mqtt_0xaabbccddee000001").Entities.Should().HaveCount(6);
        reg.Topics().Should().Contain(new[] { "zigbee2mqtt/Dormitor senzor", "zigbee2mqtt/bridge/state", "senzor-co2/status", "sensorBedroom/state" })
            .And.NotContain("garden/meter/state", "an entity that cannot be read is not subscribed");
        reg.Problems.Should().ContainSingle(p => p.Topic == "homeassistant/sensor/orphan/x/config");
    }

    [Fact]
    public void Registry_StatesGiveValues_InCanonicalUnits_WithAvailability()
    {
        var reg = Registry();
        var sensor = reg.Get("zigbee2mqtt_0xaabbccddee000001", Now)!;
        sensor.Available.Should().BeTrue();
        sensor.Entities.Single(e => e.Spec.Class == DeviceClass.Temperature).Number.Should().Be(21.37);
        sensor.Entities.Single(e => e.Spec.Class == DeviceClass.Voltage).Number.Should().BeApproximately(2.985, 1e-9);
        var plug = reg.Get("zigbee2mqtt_0xaabbccddee000002", Now)!;
        plug.Entities.Single(e => e.Spec.Class == DeviceClass.Energy).Number.Should().BeApproximately(12_340, 1e-6);
        plug.Entities.Single(e => e.Spec.Kind == EntityKind.Switch).Number.Should().Be(1);
        reg.Get("zigbee2mqtt_0xaabbccddee000003", Now)!.Entities.Single(e => e.Spec.Kind == EntityKind.Binary).Number.Should().Be(1, "the door is open");
        reg.Get("aabbccddee04", Now)!.Entities.Single(e => e.Spec.Class == DeviceClass.CarbonDioxide).Number.Should().Be(612);
        reg.Get("0AFFD2", Now)!.Entities.Select(e => e.Number).Should().BeEquivalentTo(new double?[] { 23.1, 51 });
        // The bridge goes offline: every Z2M device is unavailable (availability_mode all).
        reg.ApplyMessage("zigbee2mqtt/bridge/state", """{"state":"offline"}""", Now.AddMinutes(1));
        reg.Get("zigbee2mqtt_0xaabbccddee000001", Now.AddMinutes(1))!.Available.Should().BeFalse();
        // R4 (round 2): a publisher without availability nor expiry: its values' age against 1 h (a number, no cadence seen yet).
        reg.Get("0AFFD2", Now.AddMinutes(61))!.Should().Match<MqttDeviceSnapshot>(d => d.Available && !d.Late, "fresh up to 2 intervals");
        reg.Get("0AFFD2", Now.AddHours(3))!.Should().Match<MqttDeviceSnapshot>(d => d.Available && d.Late, "„Întârziat” up to 6 intervals");
        reg.Get("0AFFD2", Now.AddHours(7))!.Available.Should().BeFalse("stale: no longer exported as current");
        reg.Get("0AFFD2", Now.AddHours(7))!.Entities.Should().OnlyContain(e => !e.Available);
    }

    [Fact]
    public void Registry_RetainedConfigComesBackIdempotent_AndAnEmptyPayloadRemoves()
    {
        var reg = Registry();
        var again = reg.ApplyDiscovery(MqttDiscoveryParser.Parse(MqttFixtures.Load("z2m-discovery.jsonl")[0].Topic, MqttFixtures.Load("z2m-discovery.jsonl")[0].Payload), Now);
        again.ChangedDevices.Should().BeEmpty("the same retained configuration changes nothing");
        reg.Get("zigbee2mqtt_0xaabbccddee000001", Now)!.Entities.Single(e => e.Spec.Class == DeviceClass.Temperature).Number.Should().Be(21.37, "the value stays");

        var removed = reg.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/0xaabbccddee000001/temperature/config", ""), Now);
        removed.Removed.Should().ContainSingle().Which.Should().Be(("zigbee2mqtt_0xaabbccddee000001", "0xaabbccddee000001_temperature_zigbee2mqtt"));
        reg.Get("zigbee2mqtt_0xaabbccddee000001", Now)!.Entities.Should().HaveCount(5);
        // A device-based message listing fewer components removes the missing one.
        var r2 = reg.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/device/0AFFD2/config",
            """{"dev":{"ids":"0AFFD2","name":"Foobar"},"o":{"name":"x"},"cmps":{"a":{"p":"sensor","device_class":"temperature","unit_of_measurement":"°C","unique_id":"temp01ae_t","value_template":"{{ value_json.temperature }}"}},"state_topic":"sensorBedroom/state"}"""), Now);
        r2.Removed.Should().ContainSingle().Which.UniqueId.Should().Be("temp01ae_h");
    }
}
