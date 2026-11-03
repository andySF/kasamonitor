using FluentAssertions;
using KasaMonitor.Core.Heating;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Tapo;
using KasaMonitor.Web.Ui;
using KasaMonitor.Workers;
using Xunit;

namespace KasaMonitor.Web.Tests;

/// <summary>The rules of the integrations' state (chips of /dispozitive, header of each /settings tab).</summary>
public class IntegrationStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static PlugLiveState Plug(bool online, string protocol = PlugProtocols.Legacy) =>
        new(Guid.NewGuid(), "aabbccddee00", "Priză", online, null, Now.AddSeconds(-10), Protocol: protocol);

    [Fact]
    public void TpLink_AllAnswer_Ok_SomeDoNot_Warning_NoneDoes_Error()
    {
        var scan = new DiscoveryScan(Now.AddMinutes(-3), true, []);
        IntegrationStatusRules.TpLink(true, false, scan, [Plug(true), Plug(true)], Now).Level.Should().Be(IntegrationLevel.Ok);
        var some = IntegrationStatusRules.TpLink(true, false, scan, [Plug(true), Plug(false)], Now);
        some.Level.Should().Be(IntegrationLevel.Warning);
        some.Text.Should().Contain("1/2 prize răspund").And.Contain("ultima scanare acum 3 min");
        IntegrationStatusRules.TpLink(true, false, scan, [Plug(false)], Now).Level.Should().Be(IntegrationLevel.Error);
        IntegrationStatusRules.TpLink(false, false, null, [], Now).Level.Should().Be(IntegrationLevel.Off);
    }

    [Fact]
    public void TpLink_TapoPlugsWithoutTheAccount_AreAWarning()
    {
        var item = new DiscoveryResultItem(new DiscoveredPlug(System.Net.IPAddress.Loopback,
            new PlugInfo("aabbccddee00", "Tapo", "P110", "", "", "", false, 0, TimeSpan.Zero), PlugProtocols.Klap, TapoSupport.MissingCredentialsReason), DiscoveryStatus.Unsupported, null);
        var st = IntegrationStatusRules.TpLink(true, false, new DiscoveryScan(Now, false, [item]), [], Now);
        st.Level.Should().Be(IntegrationLevel.Warning);
        st.Text.Should().Contain("au nevoie de contul TP-Link");
    }

    [Fact]
    public void Viessmann_Off_NoClient_NotConnected_Connected()
    {
        var state = new HeatingState();
        HeatingRuntimeSettings S(bool on = true, string client = "c", ViessmannToken? token = null, string? err = null) =>
            new(on, client, token, err, 120, 0.31m, EnergyUnit.CubicMeter, 10.55);
        IntegrationStatusRules.Viessmann(S(on: false), state, Now).Level.Should().Be(IntegrationLevel.Off);
        IntegrationStatusRules.Viessmann(S(client: ""), state, Now).Level.Should().Be(IntegrationLevel.NotConfigured);
        IntegrationStatusRules.Viessmann(S(), state, Now).Level.Should().Be(IntegrationLevel.NotConfigured);
        IntegrationStatusRules.Viessmann(S(err: "refresh refuzat"), state, Now).Should().Match<IntegrationStatus>(s => s.Level == IntegrationLevel.Error && s.Text.Contains("refresh refuzat"));

        var token = new ViessmannToken("a", "r", Now.AddHours(1));
        var waiting = IntegrationStatusRules.Viessmann(S(token: token), state, Now);
        waiting.Level.Should().Be(IntegrationLevel.Warning, "connected but nothing read yet");
        waiting.DailyLimit.Should().Be(ViessmannApi.DailyCallLimit);

        state.Publish(new HeatingLive(new HeatingDevice(Guid.NewGuid(), "viessmann", "i", "g", "0", "Vitodens", "Centrală", true, Now, null), null, null, Now.AddSeconds(-30), null, true));
        IntegrationStatusRules.Viessmann(S(token: token), state, Now).Should().Match<IntegrationStatus>(s =>
            s.Level == IntegrationLevel.Ok && s.Text.StartsWith("conectat") && s.LastSuccessUtc == Now.AddSeconds(-30));
    }

    [Fact]
    public void Govee_WithoutKey_NotConfigured_SourceProblem_Warning_AllFresh_Ok()
    {
        IntegrationStatusRules.Govee(false, "fișier lipsă", 0, 0, null, 0, null).Level.Should().Be(IntegrationLevel.NotConfigured);
        IntegrationStatusRules.Govee(true, null, 2, 2, "HTTP 429", 10, Now).Level.Should().Be(IntegrationLevel.Warning);
        var ok = IntegrationStatusRules.Govee(true, null, 2, 2, null, 300, Now);
        ok.Level.Should().Be(IntegrationLevel.Ok);
        ok.CallsToday.Should().Be(300);
        IntegrationStatusRules.Govee(true, null, 2, 0, null, 300, Now).Level.Should().Be(IntegrationLevel.Error);
    }

    [Fact]
    public void OpenMeteo_WithoutCoordinates_IsNotConfigured()
    {
        IntegrationStatusRules.OpenMeteo(false, null, null, false, 0).Level.Should().Be(IntegrationLevel.NotConfigured);
        IntegrationStatusRules.OpenMeteo(true, null, Now, true, 4).Level.Should().Be(IntegrationLevel.Ok);
        IntegrationStatusRules.OpenMeteo(true, null, null, false, 0).Text.Should().Be("nicio citire încă");
    }

    [Fact]
    public void Miele_NoClient_NoSecret_Disconnected_RefreshRefused_StreamDown_Connected()
    {
        var state = new MieleState();
        var token = new KasaMonitor.Core.Miele.MieleToken("a", "r", Now.AddDays(30), 2_592_000);
        MieleRuntimeSettings S(string client = "c", bool secret = true, KasaMonitor.Core.Miele.MieleToken? t = null, string? err = null) =>
            new(client, secret, secret ? "fișier" : null, secret ? null : "fișier lipsă", t, err, null);

        IntegrationStatusRules.Miele(S(client: ""), state, Now).Level.Should().Be(IntegrationLevel.NotConfigured);
        IntegrationStatusRules.Miele(S(secret: false), state, Now).Text.Should().Contain("secretul");
        IntegrationStatusRules.Miele(S(), state, Now).Level.Should().Be(IntegrationLevel.NotConfigured, "never connected");
        IntegrationStatusRules.Miele(S(err: "refuzat"), state, Now).Level.Should().Be(IntegrationLevel.Error);
        IntegrationStatusRules.Miele(S(t: token, err: "refresh refuzat"), state, Now)
            .Should().Match<IntegrationStatus>(s => s.Level == IntegrationLevel.Error && s.Text.StartsWith("reconectare necesară"));
        var down = IntegrationStatusRules.Miele(S(t: token), state, Now);
        down.Level.Should().Be(IntegrationLevel.Warning, "connected, but the SSE stream is down");
        down.Text.Should().Contain("niciun aparat");

        state.SetStream(true, MieleStreamMode.Sse, Now);
        IntegrationStatusRules.Miele(S(t: token), state, Now).Should().Match<IntegrationStatus>(s => s.Level == IntegrationLevel.Ok && s.Text.StartsWith("flux conectat"));
        state.SetStream(true, MieleStreamMode.Polling, Now);
        IntegrationStatusRules.Miele(S(t: token), state, Now).Text.Should().StartWith("flux citit la 60 s");
    }

    [Fact]
    public void Shelly_ChannelsAndSensors_SourceProblem_NothingFound()
    {
        var on = Plug(true, PlugProtocols.ShellyRpc);
        var off = Plug(false, PlugProtocols.ShellyRpc);
        var all = IntegrationStatusRules.Shelly(true, [on], 2, 2, null, Now);
        all.Level.Should().Be(IntegrationLevel.Ok);
        all.Text.Should().Be("1/1 canal răspund · 2/2 senzori răspund");
        IntegrationStatusRules.Shelly(true, [on, off], 1, 1, null, Now).Level.Should().Be(IntegrationLevel.Warning);
        IntegrationStatusRules.Shelly(true, [on], 1, 1, "H&T: timeout", Now).Level.Should().Be(IntegrationLevel.Warning, "a source problem");
        IntegrationStatusRules.Shelly(true, [off], 1, 0, null, Now).Level.Should().Be(IntegrationLevel.Error);
        IntegrationStatusRules.Shelly(true, [], 0, 0, null, Now).Should().Match<IntegrationStatus>(s => s.Level == IntegrationLevel.Ok && s.Text.StartsWith("niciun dispozitiv Shelly"));
        IntegrationStatusRules.Shelly(false, [], 0, 0, null, Now).Level.Should().Be(IntegrationLevel.Off);
    }

    [Fact]
    public void Levels_HaveRomanianNames()
    {
        Enum.GetValues<IntegrationLevel>().Select(IntegrationStatus.LevelName)
            .Should().Equal("OK", "Atenție", "Eroare", "Oprit", "Neconfigurat");
    }
}
