using System.Net;
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

public class SingleApplianceTests
{
    private static Plug P(bool single, string? name = null, PlugNilmState state = PlugNilmState.Auto) =>
        new(Guid.NewGuid(), "aabbccddee00", IPAddress.Loopback, "Priza bucatarie", false, "KP115", "1", "1", "x",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false, null, NilmState: state, SingleAppliance: single, ApplianceName: name);

    [Fact]
    public void PlugRule_ASingleAppliancePlugKeepsItsSignatureFromTheFirstEvent_OtherPlugsNeedThreeAndSixtyPercent()
    {
        SignatureNaming.LinksToPlug(1, 1).Should().BeFalse("one event is no evidence for a plain plug");
        SignatureNaming.LinksToPlug(1, 1, singleAppliancePlug: true, linkedToThatPlug: true).Should().BeTrue();
        SignatureNaming.LinksToPlug(1, 10, singleAppliancePlug: true, linkedToThatPlug: true).Should().BeTrue("no 60 % share either");
        SignatureNaming.LinksToPlug(0, 10, singleAppliancePlug: true, linkedToThatPlug: true).Should().BeFalse("none of its events from the plug");
        SignatureNaming.LinksToPlug(1, 1, singleAppliancePlug: true, linkedToThatPlug: false).Should().BeFalse(
            "an anonymous signature with one event of the plug is not taken over (the plug's events get their own signature)");
        SignatureNaming.LinksToPlug(1, 1, singleAppliancePlug: false, linkedToThatPlug: true).Should().BeFalse();
        SignatureNaming.LinksToPlug(3, 4, singleAppliancePlug: false, linkedToThatPlug: false).Should().BeTrue("the plain rule still applies");
    }

    [Fact]
    public void ApplianceName_GivenOrTheAlias_OnlyForASingleAppliancePlug_AndItLeavesTheTraining()
    {
        var plain = P(false, "Frigider");
        plain.Appliance.Should().BeNull();
        plain.DisplayName.Should().Be("Priza bucatarie");
        plain.ExcludedFromTraining.Should().BeFalse();

        var fridge = P(true, " Frigider ");
        fridge.Appliance.Should().Be("Frigider");
        fridge.DisplayName.Should().Be("Frigider");
        fridge.ExcludedFromTraining.Should().BeTrue();
        fridge.TeachesNilm.Should().BeTrue("it still labels events: not NilmIgnored");

        P(true).Appliance.Should().Be("Priza bucatarie", "no name: the alias");
        P(false, state: PlugNilmState.IgnoredManual).ExcludedFromTraining.Should().BeTrue();

        new PlugLiveState(Guid.NewGuid(), "m", "Priza AC", true, null, DateTimeOffset.UnixEpoch, ApplianceName: "AC sufragerie").DisplayName.Should().Be("AC sufragerie");
        new PlugLiveState(Guid.NewGuid(), "m", "Priza AC", true, null, DateTimeOffset.UnixEpoch).DisplayName.Should().Be("Priza AC");
    }
}
