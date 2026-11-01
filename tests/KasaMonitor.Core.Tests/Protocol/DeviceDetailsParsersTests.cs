using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Text.Json;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

/// <summary>Fixtures are hand-written in the shape of the TP-Link local API answers (not copied from other projects).</summary>
public class DeviceDetailsParsersTests
{
    private static readonly DateTimeOffset ReadAt = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static Dictionary<string, JsonElement> SmartResults()
    {
        using var doc = JsonDocument.Parse(LoadFixture("tapo_device_details_p115.json"));
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    [Fact]
    public void Smart_AllSections()
    {
        var d = DeviceDetailsParsers.ParseSmart(SmartResults(), Array.Empty<string>(), ReadAt);

        d.Get("Relay").Should().Be("on");
        d.Get("Uptime").Should().Be("1 d 2 h 3 min");
        d.Get("Default state after power loss").Should().Be("off");
        d.Get("Overheated").Should().Be("no");
        d.Get("LED").Should().Be("night mode (off at night)");
        d.Get("Auto-off").Should().Be("enabled, after 30 min");
        d.Get("Auto-off timer running").Should().Be("yes");
        d.Get("Auto-off in").Should().Be("12 min 34 s");
        d.Get("Countdown").Should().Be("enabled (1 rule)");
        d.Get("Power protection").Should().Be("enabled, 2500 W");
        d.Get("Max. protection threshold").Should().Be("3680 W");
        d.Get("Power protection status").Should().Be("normal");
        d.Get("Wi-Fi signal").Should().Be("-52 dBm (level 2/3)");
        d.Get("SSID").Should().Be("TestNet");
        d.Get("Firmware").Should().StartWith("1.4.3");
        d.Get("Hardware").Should().Be("1.0");
        d.Get("Device time").Should().Be("2026-11-01 14:00:00 +02:00");
        d.Get("Region").Should().Be("Europe/Bucharest");
        d.Items.Count(i => i.Label == "Region").Should().Be(1);
        d.Get("Cloud").Should().Be("connected");
        d.Get("Schedules").Should().Be("enabled, 2 rules");
        d.Errors.Should().BeEmpty();
        d.ReadAtUtc.Should().Be(ReadAt);
    }

    [Fact]
    public void Smart_MissingMethods_AreSkipped_AndErrorsKept()
    {
        var results = SmartResults();
        results.Remove(DeviceDetailsParsers.SmartProtection);
        results.Remove(DeviceDetailsParsers.SmartMaxPower);

        var d = DeviceDetailsParsers.ParseSmart(results, new[] { "get_protection_power: not supported by this device (error -1)" }, ReadAt);

        d.Get("Power protection").Should().BeNull();
        d.Errors.Should().ContainSingle().Which.Should().Contain("get_protection_power");
        d.Get("Relay").Should().Be("on");
    }

    [Fact]
    public void Smart_DefaultStates_LastState_AndLedAlways()
    {
        var results = SmartResults();
        results[DeviceDetailsParsers.SmartDeviceInfo] = JsonDocument.Parse("""{"device_on":false,"default_states":{"type":"last_states","state":{}},"auto_off_status":"off"}""").RootElement.Clone();
        results[DeviceDetailsParsers.SmartLed] = JsonDocument.Parse("""{"led_rule":"always","led_status":true}""").RootElement.Clone();
        results[DeviceDetailsParsers.SmartCloud] = JsonDocument.Parse("""{"status":1}""").RootElement.Clone();

        var d = DeviceDetailsParsers.ParseSmart(results, Array.Empty<string>(), ReadAt);

        d.Get("Relay").Should().Be("off");
        d.Get("Default state after power loss").Should().Be("last state");
        d.Get("LED").Should().Be("always on");
        d.Get("Auto-off timer running").Should().Be("no");
        d.Get("Auto-off in").Should().BeNull();
        d.Get("Cloud").Should().Be("not connected (status 1)");
    }

    [Fact]
    public void Legacy_MultiModuleAnswer()
    {
        var d = DeviceDetailsParsers.ParseLegacy(LoadFixture("legacy_device_details_kp115.json"), ReadAt);

        d.Get("Relay").Should().Be("on");
        d.Get("Uptime").Should().Be("1 h 2 min");
        d.Get("LED").Should().Be("off");
        d.Get("Wi-Fi signal").Should().Be("-58 dBm");
        d.Get("Model").Should().Be("KP115(EU)");
        d.Get("Firmware").Should().StartWith("1.0.20");
        d.Get("Device time").Should().Be("2026-11-01 14:05:09");
        d.Get("Time zone index").Should().Be("39");
        d.Get("Cloud").Should().Be("connected (bound to an account)");
        d.Get("Schedules").Should().Be("enabled, 1 rule");
        d.Get("Next action").Should().Be("none");
        d.Get("Away mode").Should().BeNull();
        d.Errors.Should().ContainSingle().Which.Should().Be("anti_theft.get_rules: module not support (-2)");
    }

    [Fact]
    public void Legacy_RequestAsksAllModules()
    {
        using var doc = JsonDocument.Parse(DeviceDetailsParsers.LegacyDetailsRequest);
        doc.RootElement.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("system", "time", "cnCloud", "schedule", "anti_theft");
    }
}
