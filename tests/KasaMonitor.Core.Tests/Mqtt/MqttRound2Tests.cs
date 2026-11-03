using System.Text;
using FluentAssertions;
using KasaMonitor.Core.Devices;
using KasaMonitor.Core.Devices.Entities;
using KasaMonitor.Core.Devices.Generic;
using KasaMonitor.Core.Mqtt;
using Xunit;

namespace KasaMonitor.Core.Tests.Mqtt;

/// <summary>0.11, round 2 of the review: hostile templates and payloads (R1, R6, R7), counters (R2, R5), retained states (R3),
/// freshness without availability (R4), abbreviations (R8), HA semantics (R12), template errors and the deduced state class.</summary>
public class MqttRound2Tests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static MqttEntitySpec Spec(string json, string topic = "homeassistant/sensor/t/x/config") =>
        MqttEntityMapper.Map(MqttDiscoveryParser.Parse(topic, json).Components.Single())!;

    // ---- R1 / R6: the template parser never crashes nor recurses without a bound ----

    [Theory]
    [InlineData(10_000)]
    [InlineData(40)]
    public void R1_DeepNesting_IsRefused_NotAStackOverflow(int depth)
    {
        var t = "{{ " + new string('(', depth) + "value" + new string(')', depth) + " }}";
        ValueTemplate.TryParse(t, out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        ValueTemplate.TryParse("{{ " + new string('-', 20_000) + "value }}", out _, out _).Should().BeFalse("a long chain of unary minus");
        ValueTemplate.TryParse("{{ " + string.Join(" + ", Enumerable.Repeat("1", 300)) + " }}", out _, out _).Should().BeFalse("too long / too many nodes");
        ValueTemplate.TryParse("{{ ((value)) }}", out var ok, out _).Should().BeTrue("ordinary nesting still works");
        ok!.Render("5").Should().Be("5");
    }

    [Fact]
    public void R1_AHostileRetainedDiscoveryPayload_OnlyMarksItsEntity()
    {
        var deep = "{{ " + new string('(', 20_000) + "value" + new string(')', 20_000) + " }}";
        var json = "{\"uniq_id\":\"h1\",\"stat_t\":\"h/1\",\"val_tpl\":\"" + deep + "\",\"dev\":{\"ids\":\"hostile\"}}";
        var reg = new MqttRegistry();
        reg.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/h/1/config", json), Now);
        reg.Get("hostile", Now)!.Entities.Single().Spec.Problem.Should().StartWith("șablon neacceptat");
        // A payload nested deeper than JSON's limit is an invalid message, not a crash.
        var nested = "{\"a\":" + new string('[', 5000) + new string(']', 5000) + "}";
        MqttDiscoveryParser.Parse("homeassistant/sensor/h/2/config", nested).Kind.Should().Be(MqttDiscoveryKind.Invalid);
    }

    [Theory]
    [InlineData("{{ value | int * 99999999999999999999 }}")]
    [InlineData("{{ value_json[-9223372036854775808] }}")]
    [InlineData("{{ value_json[99999999999999999999] }}")]
    public void R6_Overflow_IsARefusal_NotAnException(string template) =>
        ValueTemplate.TryParse(template, out _, out var error).Should().BeFalse(error);

    [Fact]
    public void TemplateErrors_AreCountedInMemory_AndClearedByTheNextGoodMessage()
    {
        var reg = new MqttRegistry();
        reg.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/e/1/config",
            """{"uniq_id":"e1","stat_t":"e/1","val_tpl":"{{ value | float }}","dev_cla":"temperature","unit_of_meas":"°C","dev":{"ids":"err"}}"""), Now);
        reg.ApplyMessage("e/1", "abc", Now);
        reg.ApplyMessage("e/1", "xyz", Now.AddMinutes(1));
        var e = reg.Get("err", Now)!.Entities.Single();
        (e.ReadErrors, e.LastReadErrorUtc, e.Spec.Problem).Should().Be((2, Now.AddMinutes(1), null));
        e.LastReadError.Should().Contain("float");
        reg.ApplyMessage("e/1", "21.5", Now.AddMinutes(2)).Should().ContainSingle();
        reg.Get("err", Now)!.Entities.Single().Should().Match<MqttEntitySnapshot>(x => x.ReadErrors == 0 && x.LastReadError == null && x.Number == 21.5);
    }

    // ---- R2 / R5: counters ----

    [Fact]
    public void R2_ASmallDipOfATotalIncreasingCounter_IsNotAReset()
    {
        var points = new[] { new GenericPoint(Now.AddMinutes(10), 1_234_566), new GenericPoint(Now.AddMinutes(20), 1_234_570) };
        var h = GenericRollup.Hours(points, new GenericPoint(Now.AddMinutes(-5), 1_234_567), StateClass.TotalIncreasing, Now, Now.AddHours(1), TimeSpan.FromHours(2));
        h.Single().Delta.Should().Be(3, "the 1 Wh dip is ignored and the way back is not counted twice");

        double? basis = 1000;
        GenericRollup.CounterStep(ref basis, 950, StateClass.TotalIncreasing).Should().Be(0, "5 % lower: noise");
        basis.Should().Be(1000);
        GenericRollup.CounterStep(ref basis, 899, StateClass.TotalIncreasing).Should().Be(899, "under 90 %: a reset, the new value was counted since");
        basis.Should().Be(899);
        GenericRollup.CounterStep(ref basis, 0, StateClass.TotalIncreasing).Should().Be(0);
        GenericRollup.CounterStep(ref basis, 7, StateClass.TotalIncreasing).Should().Be(7);
        double? net = 10;
        GenericRollup.CounterStep(ref net, 8, StateClass.Total).Should().Be(-2, "a total may go down");
    }

    [Fact]
    public void R5_WhatACounterCountedDuringAGap_GoesToTheHourAfterIt()
    {
        // The last reading at 07:00, KasaMonitor stopped, the next at 10:20: 300 Wh counted meanwhile.
        var points = new[] { new GenericPoint(Now.AddHours(-2).AddMinutes(20), 1_300), new GenericPoint(Now.AddHours(-2).AddMinutes(40), 1_310) };
        var hours = GenericRollup.Hours(points, new GenericPoint(Now.AddHours(-5), 1_000), StateClass.TotalIncreasing, Now.AddHours(-3), Now,
            TimeSpan.FromHours(2));
        hours.Select(h => h.HourUtc).Should().Equal(new[] { Now.AddHours(-2), Now.AddHours(-1) }, "09:00 (in the gap) has no row; 11:00 carries the value");
        hours[1].Delta.Should().Be(0);
        hours[0].Delta.Should().Be(310, "300 Wh of the gap + 10 Wh of the hour, nothing lost");
        hours[0].Min.Should().Be(1_300, "min / mean / max still use only what the carry allows");
    }

    // ---- R3: retained states ----

    [Fact]
    public void R3_ARetainedState_IsNotANewReading()
    {
        var reg = new MqttRegistry();
        reg.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/r/1/config",
            """{"uniq_id":"r1","stat_t":"r/1","dev_cla":"temperature","unit_of_meas":"°C","stat_cla":"measurement","dev":{"ids":"ret"}}"""), Now);
        reg.ApplyMessage("r/1", "20.5", Now, retained: true).Should().BeEmpty("nothing to store");
        var e = reg.Get("ret", Now)!.Entities.Single();
        (e.Number, e.Ts, e.Available).Should().Be((20.5, null, false), "the value is known, its time is not: not available");
        reg.ApplyMessage("r/1", "21.0", Now.AddMinutes(1)).Should().ContainSingle();
        reg.ApplyMessage("r/1", "21.0", Now.AddHours(5), retained: true).Should().BeEmpty("a reconnection gives back the retained value");
        reg.Get("ret", Now.AddHours(5))!.Entities.Single().Ts.Should().Be(Now.AddMinutes(1), "the last live reading stays");
    }

    // ---- R4: freshness without availability nor expiry ----

    [Fact]
    public void R4_ExpectedInterval_FromTheObservedCadence_ElseTheKind()
    {
        var number = Spec("""{"uniq_id":"n","stat_t":"n","dev_cla":"temperature","unit_of_meas":"°C","stat_cla":"measurement"}""");
        var door = Spec("""{"uniq_id":"d","stat_t":"d","dev_cla":"door"}""", "homeassistant/binary_sensor/t/d/config");
        MqttAvailabilityRule.Expected(number, null).Should().Be(TimeSpan.FromHours(1));
        MqttAvailabilityRule.Expected(door, null).Should().Be(TimeSpan.FromHours(12), "an on/off publishes on change");
        MqttAvailabilityRule.Expected(number, TimeSpan.FromSeconds(10)).Should().Be(TimeSpan.FromSeconds(30), "clamped");
        MqttAvailabilityRule.Cadence([Now, Now.AddMinutes(5), Now.AddMinutes(10), Now.AddMinutes(40)]).Should().Be(TimeSpan.FromMinutes(5), "the median gap");
        MqttAvailabilityRule.Cadence([Now, Now.AddMinutes(5)]).Should().BeNull("fewer than 3 messages");

        var reg = new MqttRegistry();
        reg.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/c/1/config",
            """{"uniq_id":"c1","stat_t":"c/1","dev_cla":"temperature","unit_of_meas":"°C","stat_cla":"measurement","dev":{"ids":"cad"}}"""), Now);
        for (var i = 0; i < 4; i++) reg.ApplyMessage("c/1", "20", Now.AddMinutes(i * 5));
        var t = Now.AddMinutes(15);
        reg.Get("cad", t.AddMinutes(9))!.Should().Match<MqttDeviceSnapshot>(d => d.Available && !d.Late, "≤ 2 × 5 min");
        reg.Get("cad", t.AddMinutes(20))!.Should().Match<MqttDeviceSnapshot>(d => d.Available && d.Late, "≤ 6 × 5 min: Întârziat");
        reg.Get("cad", t.AddMinutes(31))!.Available.Should().BeFalse("older than 6 intervals: stale");
    }

    // ---- R7: hostile payloads ----

    [Fact]
    public void R7_WildcardTopics_LongTexts_AndTheDeviceCap()
    {
        Spec("""{"uniq_id":"w","stat_t":"#","dev_cla":"temperature","unit_of_meas":"°C"}""").Problem.Should().StartWith("topic cu wildcard");
        var avail = Spec("""{"uniq_id":"a","stat_t":"a/s","avty":[{"t":"a/+/status"},{"t":"a/status"}]}""");
        avail.Availability.Select(a => a.Topic).Should().Equal("a/status");
        var text = Spec("""{"uniq_id":"t","stat_t":"t/s"}""");
        MqttValueParser.Parse(text, new string('x', 10_000)).Text!.Length.Should().Be(MqttValueParser.MaxTextValue);
        MqttEntityMapper.Map(MqttDiscoveryParser.Parse("homeassistant/sensor/t/y/config", "{\"uniq_id\":\"" + new string('u', 129) + "\",\"stat_t\":\"x\"}")
            .Components.Single()).Should().BeNull("a unique id longer than 128 cannot be a key");

        var reg = new MqttRegistry();
        for (var i = 0; i < MqttRegistry.MaxDevices + 3; i++)
            reg.ApplyDiscovery(MqttDiscoveryParser.Parse($"homeassistant/sensor/n{i}/x/config", $$$"""{"uniq_id":"u{{{i}}}","stat_t":"s/{{{i}}}","dev":{"ids":"d{{{i}}}"}}"""), Now);
        reg.Devices(Now).Should().HaveCount(MqttRegistry.MaxDevices);
        reg.Problems.Should().Contain(p => p.Reason.StartsWith("prea multe dispozitive", StringComparison.Ordinal));
        reg.Topics().Should().NotContain("#");
    }

    // ---- R8: the abbreviations of HA's abbreviations.py ----

    [Theory]
    [InlineData("hum_state_tpl", "target_humidity_state_template")]
    [InlineData("max_k", "max_kelvin")]
    [InlineData("min_k", "min_kelvin")]
    [InlineData("dsp_prc", "display_precision")]
    [InlineData("def_ent_id", "default_entity_id")]
    [InlineData("stat_cla", "state_class")]
    [InlineData("uniq_id", "unique_id")]
    [InlineData("t", "topic")]
    [InlineData("cmps", "components")]
    public void R8_EntityAbbreviations_AsInHomeAssistant(string key, string full) => MqttAbbreviations.Entity[key].Should().Be(full);

    [Fact]
    public void R8_WrongKeysAreGone_DeviceAndOriginTables()
    {
        MqttAbbreviations.Entity.Keys.Should().NotContain(new[] { "hum_stat_tpl", "obj_id", "i_ver_t", "dock_t", "tilt_inv_stat" });
        MqttAbbreviations.Entity.Count.Should().BeGreaterThan(250);
        MqttAbbreviations.Device["cdev"].Should().Be("child_device");
        MqttAbbreviations.Device["ids"].Should().Be("identifiers");
        MqttAbbreviations.Origin["sw"].Should().Be("sw_version");
    }

    // ---- R12: Home Assistant's semantics ----

    [Fact]
    public void R12_UnknownAvailabilityPayload_IsIgnored_NameNullVsAbsent_AndKeysDoNotCollide()
    {
        var reg = new MqttRegistry();
        reg.ApplyDiscovery(MqttDiscoveryParser.Parse("homeassistant/sensor/v/1/config",
            """{"uniq_id":"v1","stat_t":"v/1","avty_t":"v/status","dev":{"ids":"v","name":"Senzor V"}}"""), Now);
        reg.ApplyMessage("v/status", "online", Now);
        reg.ApplyMessage("v/1", "x", Now);
        reg.ApplyMessage("v/status", "rebooting", Now.AddSeconds(1));
        reg.Get("v", Now)!.Entities.Single().Available.Should().BeTrue("an unknown availability payload does not overwrite the last state");

        var named = """{"uniq_id":"n1","stat_t":"x","dev_cla":"temperature","unit_of_meas":"°C","dev":{"ids":"x","name":"Termometru"}}""";
        Spec(named.Replace("\"uniq_id\"", "\"name\":null,\"uniq_id\"")).Name.Should().Be("Termometru", "name: null = the device's main feature");
        Spec(named).Name.Should().Be("Temperatură", "absent name = the class's name");

        MqttEntityMapper.Slug("a/b").Should().NotBe(MqttEntityMapper.Slug("a_b"));
        MqttEntityMapper.Slug("a/b").Should().Be("a%2Fb");
        MqttEntityMapper.Slug("a%2Fb").Should().Be("a%252Fb");
        EntityKeys.DeviceOf(EntityKeys.Of("dev:1", MqttEntityMapper.Slug("x/y/z"))).Should().Be("dev:1");
    }

    // ---- The deduced state class (answer 2 of the review) ----

    [Fact]
    public void StateClass_IsDeducedOnlyForTheMeasuredClasses_WithARecognisedUnit()
    {
        var t = Spec("""{"uniq_id":"i1","stat_t":"x","dev_cla":"temperature","unit_of_meas":"°C"}""");
        (t.StateClass, t.StateClassInferred).Should().Be((StateClass.Measurement, true));
        t.Describe("dev:1").StateClassInferred.Should().BeTrue();
        Spec("""{"uniq_id":"i2","stat_t":"x","dev_cla":"energy","unit_of_meas":"kWh"}""").StateClass.Should().BeNull("energy: total or total_increasing cannot be told");
        Spec("""{"uniq_id":"i3","stat_t":"x","unit_of_meas":"rpm"}""").StateClass.Should().BeNull("a number without a class may be a code");
        Spec("""{"uniq_id":"i4","stat_t":"x","dev_cla":"temperature","unit_of_meas":"°C","ent_cat":"diagnostic"}""").StateClass.Should().BeNull();
        Spec("""{"uniq_id":"i5","stat_t":"x","dev_cla":"power","unit_of_meas":"hp"}""").StateClass.Should().BeNull("an unknown unit makes the class generic");
        var sent = Spec("""{"uniq_id":"i6","stat_t":"x","dev_cla":"temperature","unit_of_meas":"°C","stat_cla":"measurement"}""");
        sent.StateClassInferred.Should().BeFalse();

        // The HA documentation's device-based example (no state_class) is now a climate sensor.
        var reg = new MqttRegistry();
        foreach (var m in MqttFixtures.Load("ha-discovery.jsonl")) reg.ApplyDiscovery(MqttDiscoveryParser.Parse(m.Topic, m.Payload), Now);
        GenericClassifier.Classify(reg.Get("0AFFD2", Now)!.Entities.Select(e => e.Spec.Describe("dev:1")).ToList()).Category
            .Should().Be(DeviceCategory.ClimateSensor);
    }
}
