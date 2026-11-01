using FluentAssertions;
using KasaMonitor.Core.History;
using KasaMonitor.Core.Models;
using System.Net;
using Xunit;

namespace KasaMonitor.Core.Tests.History;

public class AllPlugsDailyEnergyTests
{
    private static Plug MakePlug(string alias, bool archived = false, string? unsupported = null) =>
        new(Guid.NewGuid(), "mac-" + alias, IPAddress.Loopback, alias, false, "KP115(EU)", "", "", "",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, archived, null, PlugProtocols.Legacy, unsupported);

    private static PlugDailyKwh Row(Plug p, int day, long wh) => new(p.Id, new DailyKwh(2026, 10, day, wh));

    [Fact]
    public void SumsPerPlugAndDay_AlignsDays_AndTotals()
    {
        var birou = MakePlug("Birou");
        var ups = MakePlug("Ups");
        var rows = new[]
        {
            Row(birou, 1, 1000), Row(birou, 2, 1500), Row(birou, 2, 500), // duplicate day row is summed
            Row(ups, 2, 300), Row(ups, 3, 700),
        };

        var r = AllPlugsDailyEnergy.Build(new[] { ups, birou }, rows);

        r.Days.Should().Equal(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 3));
        r.Series.Select(s => s.Alias).Should().Equal("Birou", "Ups");               // alias order
        r.Series[0].DailyWh.Should().Equal(1000, 2000, 0);
        r.Series[1].DailyWh.Should().Equal(0, 300, 700);                          // missing days are 0
        r.Series[0].TotalWh.Should().Be(3000);
        r.Series[1].TotalWh.Should().Be(1000);
        r.DayTotalsWh.Should().Equal(1000, 2300, 700);
        r.TotalWh.Should().Be(4000);
    }

    [Fact]
    public void WithdrawnAndUnsupportedPlugs_AreExcluded()
    {
        var active = MakePlug("Active");
        var withdrawn = MakePlug("Withdrawn", archived: true);
        var unsupported = MakePlug("Tpap", unsupported: "protocol TPAP");
        var unknownId = new PlugDailyKwh(Guid.NewGuid(), new DailyKwh(2026, 10, 5, 999));

        var r = AllPlugsDailyEnergy.Build(new[] { active, withdrawn, unsupported },
            new[] { Row(active, 1, 100), Row(withdrawn, 1, 5000), Row(unsupported, 4, 5000), unknownId });

        r.Series.Should().ContainSingle().Which.Alias.Should().Be("Active");
        r.Days.Should().Equal(new DateOnly(2026, 10, 1));   // days of excluded plugs do not appear
        r.TotalWh.Should().Be(100);
    }

    [Fact]
    public void PlugWithoutRows_HasNoSeries_ButKeepsColorSlot()
    {
        var a = MakePlug("A");
        var b = MakePlug("B");
        var c = MakePlug("C");

        var r = AllPlugsDailyEnergy.Build(new[] { a, b, c }, new[] { Row(a, 1, 10), Row(c, 1, 10) });

        r.Series.Select(s => s.Alias).Should().Equal("A", "C");
        r.Series[1].Color.Should().Be(PlugColors.Palette[2]); // C is still the 3rd plug in alias order
    }

    [Fact]
    public void Colors_AreStableAndDistinct_InAliasOrder()
    {
        var plugs = new[] { MakePlug("Zeta"), MakePlug("Alfa"), MakePlug("Mijloc") };

        var first = PlugColors.Assign(plugs);
        var second = PlugColors.Assign(plugs.Reverse().ToArray());

        first.Should().BeEquivalentTo(second);
        first[plugs[1].Id].Should().Be(PlugColors.Palette[0]); // Alfa
        first[plugs[2].Id].Should().Be(PlugColors.Palette[1]); // Mijloc
        first[plugs[0].Id].Should().Be(PlugColors.Palette[2]); // Zeta
        first.Values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Empty_HasNoDays()
    {
        var r = AllPlugsDailyEnergy.Build(new[] { MakePlug("A") }, Array.Empty<PlugDailyKwh>());
        r.Days.Should().BeEmpty();
        r.Series.Should().BeEmpty();
        r.TotalWh.Should().Be(0);
    }
}
