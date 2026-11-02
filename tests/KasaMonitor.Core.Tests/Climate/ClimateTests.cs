using System.Net;
using System.Text.Json;
using FluentAssertions;
using KasaMonitor.Core.Climate;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Protocol.Shelly;
using KasaMonitor.Core.Tests.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Climate;

internal static class ClimateFixtures
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    public static JsonElement Json(string name)
    {
        using var doc = JsonDocument.Parse(Text(name));
        return doc.RootElement.Clone();
    }
}

public class GoveeParserTests
{
    [Fact]
    public void Devices_OfTheAccount_ThermometersRecognised()
    {
        var devices = GoveeParser.ParseDevices(ClimateFixtures.Text("govee_devices.json"));

        devices.Should().HaveCount(3);
        devices[0].Should().BeEquivalentTo(new { Sku = "H5179", Device = "AA:BB:CC:DD:EE:FF:00:11", Name = "Baie", Type = "devices.types.thermometer" });
        devices[0].IsThermometer.Should().BeTrue();
        devices[1].IsThermometer.Should().BeTrue();
        devices[2].IsThermometer.Should().BeFalse();
    }

    [Fact]
    public void State_TemperatureInFahrenheit_IsConvertedToCelsius()
    {
        // Real answer (anonymised): 78.26 °F in the bathroom = 25.7 °C.
        var state = GoveeParser.ParseState(ClimateFixtures.Text("govee_state_baie.json"));

        state.Online.Should().BeTrue();
        state.TemperatureC.Should().Be(25.7);
        state.HumidityPercent.Should().Be(56.7);
        state.BatteryPercent.Should().BeNull();
    }

    [Fact]
    public void State_Offline_WithEmptyStrings_HasNoValues()
    {
        var state = GoveeParser.ParseState(ClimateFixtures.Text("govee_state_offline.json"));

        state.Online.Should().BeFalse();
        state.TemperatureC.Should().BeNull();
        state.HumidityPercent.Should().BeNull();
    }

    [Theory]
    [InlineData(32, 0)]
    [InlineData(212, 100)]
    [InlineData(-40, -40)]
    [InlineData(70.7, 21.5)]
    public void FahrenheitToCelsius(double f, double c) => GoveeParser.FahrenheitToCelsius(f).Should().Be(c);

    [Fact]
    public void ErrorCodes_InTheBody_Throw_429IsTheRateLimit()
    {
        var limit = () => GoveeParser.ParseState("""{"code":429,"message":"Too many requests"}""");
        limit.Should().Throw<GoveeRateLimitException>();
        var other = () => GoveeParser.ParseDevices("""{"code":401,"message":"invalid key"}""");
        other.Should().Throw<GoveeException>().Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public void RequestsPerDay_OfTheSelectedThermometers()
    {
        GoveeApi.RequestsPerDay(1).Should().Be(144 + 4);
        GoveeApi.RequestsPerDay(0).Should().Be(4);
    }
}

public class GoveeClientTests
{
    [Fact]
    public async Task State_IsAPost_WithTheKeyHeader_AndTheSkuAndDevice()
    {
        var fake = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, ClimateFixtures.Text("govee_state_baie.json")));
        var client = new GoveeClient(TimeSpan.FromSeconds(5), fake);

        var state = await client.GetStateAsync("test-key", "H5179", "AA:BB:CC:DD:EE:FF:00:11", CancellationToken.None);

        state.TemperatureC.Should().Be(25.7);
        var (req, body) = fake.Requests.Single();
        req.Method.Should().Be(HttpMethod.Post);
        req.RequestUri!.ToString().Should().Be(GoveeApi.StateUrl);
        req.Headers.GetValues(GoveeApi.KeyHeader).Should().Equal("test-key");
        using var doc = JsonDocument.Parse(body!);
        Guid.TryParse(doc.RootElement.GetProperty("requestId").GetString(), out _).Should().BeTrue();
        doc.RootElement.GetProperty("payload").GetProperty("sku").GetString().Should().Be("H5179");
        doc.RootElement.GetProperty("payload").GetProperty("device").GetString().Should().Be("AA:BB:CC:DD:EE:FF:00:11");
    }

    [Fact]
    public async Task Devices_IsAGet_429AndRefusedKey_Throw_WithoutTheKeyInTheMessage()
    {
        var fake = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, ClimateFixtures.Text("govee_devices.json")));
        (await new GoveeClient(TimeSpan.FromSeconds(5), fake).ListDevicesAsync("k", CancellationToken.None)).Should().HaveCount(3);
        fake.Requests.Single().Request.Method.Should().Be(HttpMethod.Get);
        fake.Requests.Single().Request.RequestUri!.ToString().Should().Be(GoveeApi.DevicesUrl);

        var limited = new GoveeClient(TimeSpan.FromSeconds(5), new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        await limited.Invoking(c => c.ListDevicesAsync("secret-key-123", CancellationToken.None)).Should().ThrowAsync<GoveeRateLimitException>();

        var refused = new GoveeClient(TimeSpan.FromSeconds(5), new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        var ex = await refused.Invoking(c => c.ListDevicesAsync("secret-key-123", CancellationToken.None)).Should().ThrowAsync<GoveeException>();
        ex.Which.Message.Should().NotContain("secret-key-123");
    }
}

public class ShellyClimateParserTests
{
    [Fact]
    public void HtG3_TemperatureHumidityAndBattery()
    {
        var parsed = ShellyClimateParser.Parse(ClimateFixtures.Json("shelly_htg3_status.json"), ClimateFixtures.Json("shelly_htg3_config.json"));

        parsed.Channels.Should().ContainSingle();
        parsed.Channels[0].Should().Be(new ShellyClimateChannel(0, 22.47, 48.4, null));
        parsed.BatteryPercent.Should().Be(87);
        parsed.ExternalPower.Should().BeFalse();
        parsed.OnBattery.Should().BeTrue();

        var info = ShellyDeviceInfo.Parse(ClimateFixtures.Json("shelly_htg3_deviceinfo.json"));
        ShellyClimateParser.SensorName(info, ClimateFixtures.Json("shelly_htg3_config.json"), parsed.Channels[0], many: false).Should().Be("Camera tehnica");
        ShellyClimateParser.ExternalId(info.Mac, 0).Should().Be("aabbccddee01:0");
    }

    [Fact]
    public void HtG3_OnUsb_IsNotOnBattery()
    {
        var status = ClimateFixtures.Text("shelly_htg3_status.json").Replace("\"present\": false", "\"present\": true");
        using var doc = JsonDocument.Parse(status);
        ShellyClimateParser.Parse(doc.RootElement).OnBattery.Should().BeFalse();
    }

    [Fact]
    public void PlusAddon_PairsHumidityById_KeepsAFailedProbe_IgnoresHumidityWithoutTemperature_AndTheSwitchTemperature()
    {
        var config = ClimateFixtures.Json("shelly_plus1_addon_config.json");
        var parsed = ShellyClimateParser.Parse(ClimateFixtures.Json("shelly_plus1_addon_status.json"), config);

        parsed.Channels.Select(c => c.Id).Should().Equal(100, 101);
        parsed.Channels[0].Should().Be(new ShellyClimateChannel(100, 19.81, 61.2, "Pod"));
        parsed.Channels[1].Should().Be(new ShellyClimateChannel(101, null, null, null));
        parsed.BatteryPercent.Should().BeNull();
        parsed.OnBattery.Should().BeFalse();

        var info = new ShellyDeviceInfo("shellyplus1-aabbccddee02", "aabbccddee02", "SNSW-001X16EU", 2, "1.4.4", "Plus1", false, null);
        ShellyClimateParser.SensorName(info, config, parsed.Channels[1], many: true).Should().Be("Centrala releu #101");
    }

    [Fact]
    public void Meter_WithoutClimateComponents_HasNoChannels()
    {
        ShellyClimateParser.Parse(ClimateFixtures.Json("shelly_emg3_status.json")).Channels.Should().BeEmpty();
    }
}

public class OpenMeteoTests
{
    [Fact]
    public void Current_TemperatureHumidityAndTheSlotInUtc()
    {
        var c = OpenMeteoParser.Parse(ClimateFixtures.Text("openmeteo_current.json"));

        c.TemperatureC.Should().Be(9.4);
        c.HumidityPercent.Should().Be(78);
        c.TimeUtc.Should().Be(new DateTimeOffset(2026, 11, 2, 13, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Fahrenheit_IsConverted_ErrorAndMissingTemperature_Throw()
    {
        OpenMeteoParser.Parse("""{"current_units":{"temperature_2m":"°F"},"current":{"temperature_2m":50}}""").TemperatureC.Should().Be(10);
        var error = () => OpenMeteoParser.Parse("""{"error":true,"reason":"Latitude must be in range of -90 to 90°."}""");
        error.Should().Throw<OpenMeteoException>().WithMessage("*Latitude*");
        var missing = () => OpenMeteoParser.Parse("""{"current":{"relative_humidity_2m":50}}""");
        missing.Should().Throw<OpenMeteoException>();
    }

    [Fact]
    public void Url_HasTheLocation_InvariantCulture()
    {
        OpenMeteoApi.CurrentUrl(44.4268, 26.1025).Should().Be(
            "https://api.open-meteo.com/v1/forecast?latitude=44.4268&longitude=26.1025&current=temperature_2m,relative_humidity_2m&timezone=auto");
    }

    [Fact]
    public async Task Client_ReadsTheUrl_AndReportsTheReasonOfA400()
    {
        var fake = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, ClimateFixtures.Text("openmeteo_current.json")));
        (await new OpenMeteoClient(TimeSpan.FromSeconds(5), fake).GetCurrentAsync(44.4268, 26.1025, CancellationToken.None)).TemperatureC.Should().Be(9.4);
        fake.Requests.Single().Request.RequestUri!.ToString().Should().Be(OpenMeteoApi.CurrentUrl(44.4268, 26.1025));

        var bad = new OpenMeteoClient(TimeSpan.FromSeconds(5), new FakeHttpHandler((_, _) =>
            FakeHttpHandler.Json(HttpStatusCode.BadRequest, """{"error":true,"reason":"Latitude must be in range of -90 to 90°."}""")));
        await bad.Invoking(c => c.GetCurrentAsync(100, 0, CancellationToken.None)).Should().ThrowAsync<OpenMeteoException>().WithMessage("*400*Latitude*");
    }
}

public class ClimateWriteRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly ClimateReading Last = new(Guid.Empty, T0, 21.0, 50, null);

    [Fact]
    public void FirstReading_IsWritten() => ClimateWriteRule.ShouldWrite(null, T0, 21, null).Should().BeTrue();

    [Theory]
    [InlineData(21.05, 50.0, false)]   // < 0.1 °C, same humidity
    [InlineData(21.1, 50.0, true)]     // 0.1 °C
    [InlineData(20.9, 50.0, true)]     // −0.1 °C
    [InlineData(21.0, 50.9, false)]    // < 1 % RH
    [InlineData(21.0, 51.0, true)]     // 1 % RH
    [InlineData(21.0, 49.0, true)]
    public void Change_ThresholdsAgainstTheLastStored(double temp, double rh, bool written) =>
        ClimateWriteRule.ShouldWrite(Last, T0.AddMinutes(2), temp, rh).Should().Be(written);

    [Fact]
    public void Heartbeat_After30Minutes_EvenUnchanged()
    {
        ClimateWriteRule.ShouldWrite(Last, T0.AddMinutes(29), 21, 50).Should().BeFalse();
        ClimateWriteRule.ShouldWrite(Last, T0.AddMinutes(30), 21, 50).Should().BeTrue();
    }

    [Fact]
    public void HumidityAppearingOrDisappearing_IsAChange_SameOrOlderSecond_IsNot()
    {
        ClimateWriteRule.ShouldWrite(Last, T0.AddMinutes(1), 21, null).Should().BeTrue();
        ClimateWriteRule.ShouldWrite(Last with { HumidityPercent = null }, T0.AddMinutes(1), 21, 50).Should().BeTrue();
        ClimateWriteRule.ShouldWrite(Last, T0.AddMilliseconds(500), 25, 50).Should().BeFalse();
        ClimateWriteRule.ShouldWrite(Last, T0.AddMinutes(-5), 25, 50).Should().BeFalse();
    }
}

public class ClimateFreshnessTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Expected_PerSource()
    {
        ClimateFreshness.Expected(ClimateSources.Viessmann, false, 120).Should().Be(TimeSpan.FromMinutes(2));
        ClimateFreshness.Expected(ClimateSources.Viessmann, false, 10).Should().Be(TimeSpan.FromMinutes(1));
        ClimateFreshness.Expected(ClimateSources.Govee, false, 120).Should().Be(TimeSpan.FromMinutes(10));
        ClimateFreshness.Expected(ClimateSources.Shelly, false, 120).Should().Be(TimeSpan.FromMinutes(1));
        ClimateFreshness.Expected(ClimateSources.Shelly, true, 120).Should().Be(TimeSpan.FromHours(2));
        ClimateFreshness.Expected(ClimateSources.OpenMeteo, false, 120).Should().Be(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(20, ClimateFreshnessLevel.Fresh)]
    [InlineData(21, ClimateFreshnessLevel.Late)]
    [InlineData(60, ClimateFreshnessLevel.Late)]
    [InlineData(61, ClimateFreshnessLevel.Stale)]
    public void Level_GreenYellowRed(int minutesAgo, ClimateFreshnessLevel level) =>
        ClimateFreshness.Level(Now.AddMinutes(-minutesAgo), Now, TimeSpan.FromMinutes(10)).Should().Be(level);

    [Fact]
    public void NeverRead_IsStale() => ClimateFreshness.Level(null, Now, TimeSpan.FromMinutes(10)).Should().Be(ClimateFreshnessLevel.Stale);
}

public class ClimateAggregationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    private static ClimatePoint P(int minutes, double c, double? rh = null) => new(T0.AddMinutes(minutes), c, rh);

    [Fact]
    public void Stats_MeanIsWeightedByHowLongEachValueHeld()
    {
        // 20 °C for 50 min, then 22 °C for 10 min (written often while changing): mean 20.33, not (20+22+22)/3.
        var points = new[] { P(0, 20, 40), P(50, 22, 60), P(55, 22.05, 60) };

        var s = ClimateAggregation.Stats(points, T0, T0.AddMinutes(60));

        s.MinC.Should().Be(20);
        s.MaxC.Should().Be(22.05);
        s.AvgC.Should().BeApproximately((20 * 50 + 22 * 5 + 22.05 * 5) / 60.0, 1e-9);
        s.MinHumidity.Should().Be(40);
        s.MaxHumidity.Should().Be(60);
        s.AvgHumidity.Should().BeApproximately((40 * 50 + 60 * 10) / 60.0, 1e-9);
        s.Count.Should().Be(3);
    }

    [Fact]
    public void Stats_AReadingBeforeThePeriodCountsWhileItHolds_ALongSilenceIsAGap()
    {
        // 30 min before the period: holds for its first 30 min (hold = 60 min). Then nothing for 2 hours, then 10 °C.
        var points = new[] { P(-30, 18), P(120, 10) };

        var s = ClimateAggregation.Stats(points, T0, T0.AddMinutes(180));

        s.MinC.Should().Be(10);
        s.MaxC.Should().Be(18);
        // 18 °C for 30 min, gap 30..120, 10 °C for 60 min.
        s.AvgC.Should().BeApproximately((18 * 30 + 10 * 60) / 90.0, 1e-9);
        s.Count.Should().Be(1);
        s.AvgHumidity.Should().BeNull();
    }

    [Fact]
    public void Stats_NothingInThePeriod_IsEmpty_AndAReadingExactlyAtTheEnd_CountsByItsValue()
    {
        ClimateAggregation.Stats(Array.Empty<ClimatePoint>(), T0, T0.AddHours(1)).Should().Be(ClimateStats.Empty);
        ClimateAggregation.Stats(new[] { P(-120, 5) }, T0, T0.AddHours(1)).Should().Be(ClimateStats.Empty);
        var atEnd = ClimateAggregation.Stats(new[] { P(60, 7, 30) }, T0, T0.AddHours(1));
        atEnd.AvgC.Should().Be(7);
        atEnd.AvgHumidity.Should().Be(30);
        atEnd.Count.Should().Be(1);
    }

    [Fact]
    public void Buckets_TimeWeightedPerBucket_EmptyBucketsAreNull()
    {
        var points = new[] { P(0, 20, 50), P(7, 23, 50), P(40, 25) };

        var b = ClimateAggregation.Buckets(points, T0, T0.AddMinutes(120), TimeSpan.FromMinutes(10));

        b.Should().HaveCount(12);
        b[0].StartUtc.Should().Be(T0);
        b[0].TemperatureC.Should().BeApproximately((20 * 7 + 23 * 3) / 10.0, 1e-9);
        b[1].TemperatureC.Should().Be(23);
        b[4].TemperatureC.Should().Be(25);
        b[4].HumidityPercent.Should().BeNull();
        b[9].TemperatureC.Should().Be(25);       // 40 + 60 min hold = up to minute 100
        b[10].TemperatureC.Should().BeNull();    // gap
        b[11].TemperatureC.Should().BeNull();
    }

    [Fact]
    public void Buckets_UnsortedInputIsSorted_AndBucketSizePerPeriod()
    {
        var b = ClimateAggregation.Buckets(new[] { P(30, 30), P(0, 10) }, T0, T0.AddMinutes(60), TimeSpan.FromMinutes(30));
        b.Select(x => x.TemperatureC).Should().Equal(10, 30);

        ClimateAggregation.BucketFor(TimeSpan.FromHours(24)).Should().Be(TimeSpan.FromMinutes(5));
        ClimateAggregation.BucketFor(TimeSpan.FromDays(7)).Should().Be(TimeSpan.FromMinutes(30));
        ClimateAggregation.BucketFor(TimeSpan.FromDays(30)).Should().Be(TimeSpan.FromHours(2));
    }
}

public class ViessmannClimateTests
{
    [Fact]
    public void Reading_BecomesRoomSupplyAndHotWater_NullValuesNone()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var ts = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var r = new HeatingReading(id, ts, null, null, 45.5, null, null, 21.3, 48.0, null, null, null, null, null, null, null, null, null, null, null, null);

        var samples = ViessmannClimate.Samples(r).ToList();

        samples.Select(s => (s.ExternalId, s.DefaultName, s.TemperatureC)).Should().Equal(
            (id + ":room", "Termostat (cameră)", 21.3),
            (id + ":supply", "Tur centrală", 45.5),
            (id + ":dhw", "Apă caldă", 48.0));
        samples.Should().OnlyContain(s => s.Source == ClimateSources.Viessmann && s.TsUtc == ts && s.HumidityPercent == null);

        ViessmannClimate.Samples(r with { RoomTempC = null }).Should().HaveCount(2);
    }
}
