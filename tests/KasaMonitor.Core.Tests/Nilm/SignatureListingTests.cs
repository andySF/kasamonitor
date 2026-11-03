using FluentAssertions;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

public class SignatureListingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static ApplianceSignature Sig(long id, int count, DateTimeOffset? lastSeen = null, string? name = null,
        SignatureLabelSource source = SignatureLabelSource.Cluster, Guid? plug = null) =>
        new(id, name ?? SignatureNaming.Unknown(id), "bi-question-circle", ApplianceCategories.Other, 30, 0, 1, count, lastSeen ?? Now.AddHours(-1), source, plug, false);

    [Theory]
    [InlineData(3, null, false, true)]          // three events
    [InlineData(2, null, false, false)]         // rare
    [InlineData(1, null, true, true)]           // „posibil <priză>”
    [InlineData(1, "Fierbător", false, true)]   // a name of its own
    [InlineData(0, null, false, false)]
    public void ShownByDefault_ThreeEvents_OrAName_OrAPossiblePlug(int count, string? name, bool possible, bool shown)
    {
        SignatureListing.ShownByDefault(Sig(7, count, name: name), possible).Should().Be(shown);
    }

    [Fact]
    public void ShownByDefault_ARenamedSignatureKeepingTheDefaultNameIsStillNamedByTheUser()
    {
        SignatureListing.ShownByDefault(Sig(7, 1, source: SignatureLabelSource.User), false).Should().BeTrue();
    }

    [Fact]
    public void RareAndOld_AtMostTwoEvents_LastSeenOverSevenDaysAgo_NoPlugNoNameNoBadge()
    {
        SignatureListing.IsRareAndOld(Sig(1, 2, Now.AddDays(-8)), Now, false).Should().BeTrue();
        SignatureListing.IsRareAndOld(Sig(1, 1, Now.AddDays(-8)), Now, false).Should().BeTrue();
        SignatureListing.IsRareAndOld(Sig(1, 1, Now.AddDays(-8)), Now, possiblePlug: true).Should().BeFalse("„posibil <priză>” badge");
        SignatureListing.IsRareAndOld(Sig(1, 3, Now.AddDays(-8)), Now, false).Should().BeFalse("three events");
        SignatureListing.IsRareAndOld(Sig(1, 2, Now.AddDays(-6)), Now, false).Should().BeFalse("seen in the last 7 days");
        SignatureListing.IsRareAndOld(Sig(1, 2, Now.AddDays(-8), "Fierbător", SignatureLabelSource.User), Now, false).Should().BeFalse("named by the user");
        SignatureListing.IsRareAndOld(Sig(1, 2, Now.AddDays(-8), "Fierbător"), Now, false).Should().BeFalse("a name of its own");
        SignatureListing.IsRareAndOld(Sig(1, 1, Now.AddDays(-8), "Frigider", SignatureLabelSource.Plug, Guid.NewGuid()), Now, false).Should().BeFalse("measured by a plug");
        SignatureListing.IsRareAndOld(Sig(1, 1) with { LastSeenUtc = null }, Now, false).Should().BeFalse("no event time: kept");
    }
}
