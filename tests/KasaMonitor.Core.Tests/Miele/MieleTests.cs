using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Miele;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Tests.Heating;
using Xunit;

namespace KasaMonitor.Core.Tests.Miele;

public class MieleSseParserTests
{
    private static List<MieleSseEvent> Feed(params string[] lines)
    {
        var p = new MieleSseParser();
        var list = new List<MieleSseEvent>();
        foreach (var l in lines)
            if (p.Feed(l) is { } e) list.Add(e);
        return list;
    }

    [Fact]
    public void WireForm_PymieleNames_DevicesActionsPing()
    {
        var events = Feed(
            "event: devices", "data: {\"000148508574\":{\"ident\":{},\"state\":{}}}", "",
            "event: actions", "data: {\"000148508574\":{\"processAction\":[]}}", "",
            "event: ping", "data: ", "");
        events.Select(e => e.Kind).Should().Equal(MieleEventKind.Devices, MieleEventKind.Actions, MieleEventKind.Ping);
        events[0].Data.Should().StartWith("{\"000148508574\"");
        events[0].Name.Should().Be("devices");
    }

    [Fact]
    public void DocsForm_UpperCaseNames_CrLf_Comments_MultiLineData()
    {
        var events = Feed(
            ": keep-alive comment", "event: IDENT\r", "data: {\"000148508574\":\r", "data: {\"state\":{}}}\r", "\r",
            "event:ACTION", "data:{}", "",
            "event: PING", "");
        events.Select(e => e.Kind).Should().Equal(MieleEventKind.Devices, MieleEventKind.Actions, MieleEventKind.Ping);
        events[0].Data.Should().Be("{\"000148508574\":\n{\"state\":{}}}", "data lines are joined with LF, one leading space dropped");
        MieleStateParser.Parse(events[0].Data).Should().ContainSingle().Which.DeviceId.Should().Be("000148508574");
    }

    [Fact]
    public void EventWithoutName_ClassifiedByItsData()
    {
        var events = Feed("data: {\"ident\":{\"deviceIdentLabel\":{\"fabNumber\":\"1\"}}}", "", "data: hello", "", "id: 5", "");
        events.Select(e => e.Kind).Should().Equal(MieleEventKind.Devices, MieleEventKind.Unknown);
        MieleSseParser.KindOf("weird", "{}").Should().Be(MieleEventKind.Unknown);
        MieleSseParser.KindOf("", "").Should().Be(MieleEventKind.Ping);
    }
}

public class MieleStateParserTests
{
    [Fact]
    public void OfficialSpecExample_WashingMachineWaitingToStart()
    {
        var devices = MieleStateParser.Parse(VicareFixtures.Text("miele_devices_spec_washingmachine.json"));
        var d = devices.Should().ContainSingle().Subject;
        d.DeviceId.Should().Be("000148508574");
        d.Ident!.TypeRaw.Should().Be(1);
        d.Ident.TechType.Should().Be("WWV980");
        d.Ident.DeviceName.Should().BeNull("an empty deviceName is no name");
        d.Status!.Raw.Should().Be(4);
        d.Status.Localized.Should().Be("Waiting to start");
        d.ProgramId!.Raw.Should().Be(1);
        d.ProgramId.Localized.Should().Be("Cottons");
        d.ProgramType!.Raw.Should().Be(1);
        d.Phase!.Raw.Should().Be(256);
        d.Phase.Localized.Should().BeNull();
        d.RemainingSec.Should().Be(2 * 3600 + 14 * 60);
        d.StartTimeSec.Should().Be(3600 + 34 * 60);
        d.ElapsedSec.Should().Be(0);
        d.TargetTempC.Should().Be(60, "6000 = 60 °C × 100; the -32768 entries are skipped");
        d.SpinRpm.Should().Be(1600);
        d.DryingStepRaw.Should().BeNull();
        d.DoorOpen.Should().BeFalse();
        d.SignalFailure.Should().BeFalse();
        d.SignalInfo.Should().BeFalse();
        d.EcoEnergyKwh.Should().Be(0);
        d.EcoWaterL.Should().Be(0);
        d.EcoEnergyForecast.Should().Be(0.4);
        d.EcoWaterForecast.Should().Be(0.3);
        d.RemoteFullControl.Should().BeTrue();
        d.RemoteSmartGrid.Should().BeTrue();
        d.RemoteMobileStart.Should().BeFalse();

        var v = MieleStateValues.From(d, d.Ident.TypeRaw);
        v.StatusText.Should().Be("Așteaptă pornirea");
        v.ProgramIdText.Should().Be("Cottons");
        v.ProgramTypeText.Should().Be("Program propriu");
        v.PhaseText.Should().Be("nu rulează");
        v.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void SingleApplianceForm_AndMissingCapabilities()
    {
        const string json = """
            {"ident":{"type":{"value_raw":2,"value_localized":"Tumble dryer"},"deviceName":"Uscator","deviceIdentLabel":{"fabNumber":"000111222333","techType":"TWV780"}},
             "state":{"status":{"value_raw":5,"value_localized":"In use"},"programPhase":{"value_raw":514,"value_localized":"Drying"},
                      "remainingTime":[0,45],"targetTemperature":[{"value_raw":-32768,"value_localized":null}],"spinningSpeed":null,
                      "ecoFeedback":null,"signalDoor":null}}
            """;
        var d = MieleStateParser.Parse(json).Should().ContainSingle().Subject;
        d.DeviceId.Should().Be("000111222333");
        d.Ident!.DeviceName.Should().Be("Uscator");
        d.RemainingSec.Should().Be(45 * 60);
        d.TargetTempC.Should().BeNull();
        d.SpinRpm.Should().BeNull();
        d.EcoEnergyKwh.Should().BeNull();
        d.DoorOpen.Should().BeNull();
        MieleStateValues.From(d, 2).PhaseText.Should().Be("Uscare");
    }

    [Fact]
    public void NotAnObjectOrNoAppliances_Empty()
    {
        MieleStateParser.Parse("[]").Should().BeEmpty();
        MieleStateParser.Parse("{\"x\":1}").Should().BeEmpty();
    }
}

public class MieleTextsTests
{
    [Theory]
    [InlineData(1, 260, "Main wash", "Spălare principală")]
    [InlineData(1, 11004, null, "Spălare principală")]
    [InlineData(1, 266, null, "Centrifugare")]
    [InlineData(1, 265, null, "Evacuare")]
    [InlineData(2, 514, null, "Uscare")]
    [InlineData(24, 522, null, "Terminat")]
    [InlineData(24, 260, null, "Spălare principală")]
    [InlineData(1, 1795, "Main wash", "Main wash")]
    [InlineData(1, 1795, null, "faza 1795")]
    [InlineData(7, 1792, null, "faza 1792")]
    public void Phase_CommunityDictionary_ThenLocalized_ThenRaw(int type, int raw, string? localized, string expected) =>
        MieleTexts.Phase(type, new MieleValue(raw, localized)).Should().Be(expected);

    [Fact]
    public void Status_OfficialEnum_WithFallbacks()
    {
        MieleTexts.Status(new MieleValue(5, "In use")).Should().Be("În funcțiune");
        MieleTexts.StatusCode(5).Should().Be("running");
        MieleTexts.StatusCode(255).Should().Be("not_connected");
        MieleTexts.Status(new MieleValue(144, "Default")).Should().Be("Default", "144 is not in the spec enum");
        MieleTexts.Status(new MieleValue(147, null)).Should().Be("stare 147");
        MieleTexts.StatusCode(147).Should().Be("status_147");
        MieleTexts.Phase(1, null).Should().BeNull();
        MieleTexts.Program(new MieleValue(133, null)).Should().Be("program 133");
        MieleTexts.TypeCode(1).Should().Be("washing_machine");
        MieleTexts.TypeText(99, "Robot").Should().Be("Robot");
        MieleTexts.ExpectedLoad("Spălare principală").Should().Contain("rezistență");
        MieleTexts.ExpectedLoad("Clătire").Should().BeNull();
    }
}

public class MieleCycleTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static MieleStateValues V(int status, int? phase = null, string? phaseText = null, double? kwh = null, double? water = null, int? program = 1) =>
        new(status, null, null, null, program, program is null ? null : "Cottons", phase, phaseText, null, null, null, null, null, null,
            null, null, null, kwh, water, null, null, null, null, null);

    [Fact]
    public void StartsAtRunning_RecordsPhases_ClosesAtEndProgrammed_WithHighestEcoValues()
    {
        var t = new MieleCycleTracker("dev");
        t.Observe(T0, V(4, 256, "nu rulează")).Any.Should().BeFalse("waiting to start is no cycle yet");
        var opened = t.Observe(T0.AddMinutes(1), V(5, 260, "Spălare principală", kwh: 0.1)).Opened;
        opened.Should().NotBeNull();
        opened!.DeviceId.Should().Be("dev");
        opened.StartUtc.Should().Be(T0.AddMinutes(1));
        t.SetId(42);
        t.Observe(T0.AddMinutes(2), V(5, 260, "Spălare principală", kwh: 0.1)).Any.Should().BeFalse("nothing changed");
        t.Observe(T0.AddMinutes(40), V(5, 261, "Clătire", kwh: 0.8, water: 30)).Updated!.Phases.Should().HaveCount(2);
        t.Observe(T0.AddMinutes(60), V(5, 266, "Centrifugare", kwh: 0.9, water: 45));
        var closed = t.Observe(T0.AddMinutes(70), V(7, 268, "Terminat", kwh: 0, water: 0)).Closed;
        closed.Should().NotBeNull();
        closed!.Id.Should().Be(42);
        closed.EndUtc.Should().Be(T0.AddMinutes(70));
        closed.EndReason.Should().Be(MieleEndReasons.End);
        closed.EnergyKwh.Should().Be(0.9, "the counter resets at the end: the highest value of the cycle");
        closed.WaterL.Should().Be(45);
        closed.Phases.Select(p => p.PhaseText).Should().Equal("Spălare principală", "Clătire", "Centrifugare", "Terminat");
        closed.Phases[0].ToUtc.Should().Be(T0.AddMinutes(40));
        closed.Phases[^1].ToUtc.Should().Be(T0.AddMinutes(70));
        t.Open.Should().BeNull();
    }

    [Theory]
    [InlineData(10, "idle")]
    [InlineData(1, "off")]
    public void ClosesAtIdleOrOff(int status, string reason)
    {
        var t = new MieleCycleTracker("dev");
        t.Observe(T0, V(5, 260, "Spălare principală"));
        t.Observe(T0.AddMinutes(5), V(status)).Closed!.EndReason.Should().Be(reason);
    }

    [Fact]
    public void ThirtyMinutesWithoutRunning_ClosesAtTheLastRunningTime()
    {
        var t = new MieleCycleTracker("dev");
        t.Observe(T0, V(5, 260, "Spălare principală"));
        t.Observe(T0.AddMinutes(10), V(5, 261, "Clătire"));
        t.Observe(T0.AddMinutes(12), V(6, 261, "Clătire")).Closed.Should().BeNull("a pause is not the end");
        t.Tick(T0.AddMinutes(39)).Closed.Should().BeNull("29 min without Running");
        var closed = t.Tick(T0.AddMinutes(41)).Closed;
        closed!.EndReason.Should().Be(MieleEndReasons.Timeout);
        closed.EndUtc.Should().Be(T0.AddMinutes(10));
        // Seen again (not connected) after the timeout through Observe: also closed there.
        var t2 = new MieleCycleTracker("dev");
        t2.Observe(T0, V(5, 260, "Spălare principală"));
        t2.Observe(T0.AddMinutes(31), V(255)).Closed!.EndReason.Should().Be(MieleEndReasons.Timeout);
    }

    [Fact]
    public void RestoredOpenCycle_ContinuesAfterARestart()
    {
        var open = new MieleCycle(7, "dev", T0, null, T0.AddMinutes(20), 1, "Cottons",
            new[] { new MielePhaseSpan(260, "Spălare principală", T0, null) }, 0.5, 20, null, null);
        var t = new MieleCycleTracker("dev", open);
        t.Observe(T0.AddMinutes(25), V(5, 261, "Clătire", kwh: 0.6)).Updated!.Id.Should().Be(7);
        t.Observe(T0.AddMinutes(30), V(7)).Closed!.EnergyKwh.Should().Be(0.6);
    }

    [Fact]
    public void SnapshotRule_OnChange_OrHeartbeatEveryFiveMinutes()
    {
        var a = V(5, 260, "Spălare principală");
        MieleSnapshotRule.ShouldWrite(null, a, T0).Should().BeTrue();
        var prev = new MieleStateSnapshot(T0, "dev", a);
        MieleSnapshotRule.ShouldWrite(prev, a with { }, T0.AddMinutes(4)).Should().BeFalse();
        MieleSnapshotRule.ShouldWrite(prev, a, T0.AddMinutes(5)).Should().BeTrue();
        MieleSnapshotRule.ShouldWrite(prev, a with { RemainingSec = 60 }, T0.AddSeconds(10)).Should().BeTrue();
    }
}

public class MieleGroundTruthRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Plug = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static MieleLinkedAppliance Washer(params MieleStatusPoint[] points) => new(Plug, "Mașina", points);

    [Fact]
    public void RunningApplianceOnTheEventsPlug_LabelledWithThePhase()
    {
        var w = Washer(new MieleStatusPoint(T0, 5, "Spălare principală"), new MieleStatusPoint(T0.AddMinutes(30), 5, "Centrifugare"));
        MieleGroundTruthRule.Label(Plug, T0.AddMinutes(10), new[] { w }).Should().Be("Mașina · Spălare principală");
        MieleGroundTruthRule.Label(Plug, T0.AddMinutes(31), new[] { w }).Should().Be("Mașina · Centrifugare");
        MieleGroundTruthRule.IsGroundTruthName("Mașina · Centrifugare").Should().BeTrue();
        MieleGroundTruthRule.PhaseOf("Mașina · Centrifugare").Should().Be("Centrifugare");
    }

    [Fact]
    public void NoPlugConfirmation_OtherPlug_NotRunning_OrBeforeTheTimeline_NoLabel()
    {
        var w = Washer(new MieleStatusPoint(T0, 5, "Spălare principală"), new MieleStatusPoint(T0.AddMinutes(60), 7, "Terminat"));
        MieleGroundTruthRule.Label(null, T0.AddMinutes(10), new[] { w }).Should().BeNull("an event without the plug's confirmation is never labelled");
        MieleGroundTruthRule.Label(Guid.NewGuid(), T0.AddMinutes(10), new[] { w }).Should().BeNull("another plug");
        MieleGroundTruthRule.Label(Plug, T0.AddMinutes(61), new[] { w }).Should().BeNull("not running any more");
        MieleGroundTruthRule.Label(Plug, T0.AddMinutes(-1), new[] { w }).Should().BeNull("before anything is known");
        MieleGroundTruthRule.Label(Plug, T0, new[] { Washer(new MieleStatusPoint(T0, 5, "nu rulează")) }).Should().BeNull("idle phase");
        MieleGroundTruthRule.Label(Plug, T0, Array.Empty<MieleLinkedAppliance>()).Should().BeNull();
    }
}

public class MielePlugEnergyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Minutes_SummedWithPartialEdges_SamplesHeldUntilTheNext()
    {
        var plug = Guid.NewGuid();
        var minutes = Enumerable.Range(0, 10).Select(i => new LiveMinuteAggregate(plug, T0.AddMinutes(i), 0, 600, 600, null, 10, 60, 60)).ToList();
        MielePlugEnergy.FromMinutes(minutes, T0.AddSeconds(30), T0.AddMinutes(5)).Should().BeApproximately(45, 1e-9, "4.5 minutes of 10 Wh minutes");
        MielePlugEnergy.FromMinutes(Array.Empty<LiveMinuteAggregate>(), T0, T0.AddMinutes(5)).Should().BeNull();

        var samples = new List<Sample>
        {
            new(0, plug, T0, true, 1_000_000, null, null, null, null, null, null),               // 1000 W
            new(0, plug, T0.AddMinutes(30), true, 2_000_000, null, null, null, null, null, null),
        };
        // Each poll is held at most 5 minutes: 1000 W × 5 min + 2000 W × 5 min.
        MielePlugEnergy.FromSamples(samples, T0, T0.AddHours(1)).Should().BeApproximately(1000.0 / 12 + 2000.0 / 12, 1e-6);
    }
}

public class MieleClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParseToken_RotatesTheRefreshToken_RefreshDueAtNinetyPercent()
    {
        var t = MieleClient.ParseToken("""{"access_token":"A2","refresh_token":"R2","token_type":"Bearer","expires_in":2592000}""", Now, "R1");
        t.RefreshToken.Should().Be("R2");
        t.ExpiresAtUtc.Should().Be(Now.AddDays(30));
        t.RefreshDueUtc.Should().Be(Now.AddDays(27));
        MieleClient.ParseToken("""{"access_token":"A3","expires_in":"3600"}""", Now, "R2").RefreshToken.Should().Be("R2");
        var act = () => MieleClient.ParseToken("{}", Now);
        act.Should().Throw<MieleAuthException>();
    }

    [Fact]
    public async Task Refresh_PostsClientSecretAndRefreshToken_ToTheNewProvider_ReturnsTheRotatedToken()
    {
        var fake = new FakeHttpHandler((_, _) => FakeHttpHandler.Json(HttpStatusCode.OK, """{"access_token":"A2","refresh_token":"R2","expires_in":2592000}"""));
        var client = new MieleClient(TimeSpan.FromSeconds(5), fake, () => Now);
        var t = await client.RefreshAsync("cid", "sec", "R1", CancellationToken.None);
        t.AccessToken.Should().Be("A2");
        t.RefreshToken.Should().Be("R2");
        var (req, body) = fake.Requests.Should().ContainSingle().Subject;
        req.RequestUri!.ToString().Should().Be(MieleApi.TokenUrl);
        body.Should().Contain("grant_type=refresh_token").And.Contain("client_secret=sec").And.Contain("refresh_token=R1").And.Contain("client_id=cid");
    }

    [Fact]
    public async Task TokenErrors_InvalidGrantIsFinal_ServerErrorIsTemporary()
    {
        var status = HttpStatusCode.BadRequest;
        var body = """{"error":"invalid_grant","error_description":"Token is not active"}""";
        var client = new MieleClient(TimeSpan.FromSeconds(5), new FakeHttpHandler((_, _) => FakeHttpHandler.Json(status, body)), () => Now);
        await client.Invoking(c => c.RefreshAsync("cid", "sec", "R1", CancellationToken.None)).Should().ThrowAsync<MieleAuthException>();
        status = HttpStatusCode.ServiceUnavailable;
        body = """{"code":503,"message":"unavailable"}""";
        await client.Invoking(c => c.RefreshAsync("cid", "sec", "R1", CancellationToken.None)).Should().ThrowAsync<MieleApiException>();
    }

    [Fact]
    public async Task Devices_And_Events_SendBearer_AndAccept()
    {
        var fake = new FakeHttpHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/events")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("event: ping\ndata: \n\n") }
            : FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var client = new MieleClient(TimeSpan.FromSeconds(5), fake, () => Now);
        (await client.GetDevicesJsonAsync("AT", CancellationToken.None)).Should().Be("{}");
        await using (var s = await client.OpenEventsAsync("AT", CancellationToken.None))
        {
            (await s.ReadLineAsync(CancellationToken.None)).Should().Be("event: ping");
        }
        fake.Requests.Should().HaveCount(2);
        fake.Requests.Should().OnlyContain(r => r.Request.Headers.Authorization!.Parameter == "AT");
        fake.Requests[1].Request.Headers.Accept.ToString().Should().Contain("text/event-stream");
        var unauthorized = new MieleClient(TimeSpan.FromSeconds(5), new FakeHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)), () => Now);
        await unauthorized.Invoking(c => c.GetDevicesJsonAsync("AT", CancellationToken.None)).Should().ThrowAsync<MieleAuthException>();
    }
}
