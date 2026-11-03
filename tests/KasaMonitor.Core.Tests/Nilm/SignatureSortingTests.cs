using FluentAssertions;
using KasaMonitor.Core.Nilm;
using Xunit;

namespace KasaMonitor.Core.Tests.Nilm;

public class SignatureSortingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private sealed record Row(ApplianceSignature Signature, double Today, double Days7, double Days30);

    private static Row R(long id, string name, string category, double dp, int count, double dq, double transient, DateTimeOffset? last,
        SignatureLabelSource source, double today, double days7, double days30) =>
        new(new ApplianceSignature(id, name, "bi-question-circle", category, dp, dq, transient, count, last, source, null, false), today, days7, days30);

    // Every key orders them differently; id 4 has no last event and no known transient (both shown „—”).
    private static readonly Row[] Rows =
    {
        R(1, "boiler", "boiler", 2000, 9, 5, 1.0, Now.AddHours(-3), SignatureLabelSource.User, 300, 900, 4000),
        R(2, "Aer", "hvac", 800, 3, 400, 4.0, Now.AddMinutes(-5), SignatureLabelSource.Cluster, 50, 2000, 2500),
        R(3, "Cuptor", "cooking", 1500, 20, 10, 1.2, Now.AddDays(-2), SignatureLabelSource.Plug, 0, 100, 6000),
        R(4, "Necunoscut #4", "other", 40, 5, -20, 0, null, SignatureLabelSource.Cluster, 10, 5, 7),
    };

    private static long[] Ids(SignatureSort sort, bool descending) =>
        SignatureSorting.Sorted(Rows, r => r.Signature, r => r.Today, r => r.Days7, r => r.Days30, sort, descending)
            .Select(r => r.Signature.Id).ToArray();

    [Theory]
    [InlineData(SignatureSort.LastSeen, new long[] { 2, 1, 3, 4 })]
    [InlineData(SignatureSort.DeltaP, new long[] { 1, 3, 2, 4 })]
    [InlineData(SignatureSort.Count, new long[] { 3, 1, 4, 2 })]
    [InlineData(SignatureSort.DeltaQ, new long[] { 2, 3, 1, 4 })]
    [InlineData(SignatureSort.Transient, new long[] { 2, 3, 1, 4 })]
    [InlineData(SignatureSort.Today, new long[] { 1, 2, 4, 3 })]
    [InlineData(SignatureSort.Days7, new long[] { 2, 1, 3, 4 })]
    [InlineData(SignatureSort.Days30, new long[] { 3, 1, 2, 4 })]
    public void Numbers_LargestFirstDescending_ReversedAscending(SignatureSort sort, long[] descending)
    {
        Ids(sort, descending: true).Should().Equal(descending);
        var ascending = sort is SignatureSort.LastSeen or SignatureSort.Transient
            ? descending.Take(3).Reverse().Append(4L).ToArray()   // no value: still last
            : descending.Reverse().ToArray();
        Ids(sort, descending: false).Should().Equal(ascending);
    }

    [Theory]
    // Name: „Aer” < „boiler” < „Cuptor” < „Necunoscut #4” (case ignored).
    [InlineData(SignatureSort.Name, new long[] { 2, 1, 3, 4 }, new long[] { 4, 3, 1, 2 })]
    // Category by its Romanian name: „AC / pompă de căldură” < „altele” < „boiler” < „plită / cuptor”.
    [InlineData(SignatureSort.Category, new long[] { 2, 4, 1, 3 }, new long[] { 3, 1, 4, 2 })]
    // Source by its Romanian name: „confirmat din priză” < „grupare automată” (2 and 4, tie by id) < „utilizator”.
    [InlineData(SignatureSort.Source, new long[] { 3, 2, 4, 1 }, new long[] { 1, 2, 4, 3 })]
    public void Texts_AToZAscending_CaseIgnored_TiesById(SignatureSort sort, long[] ascending, long[] descending)
    {
        Ids(sort, descending: false).Should().Equal(ascending);
        Ids(sort, descending: true).Should().Equal(descending);
    }

    [Fact]
    public void WithoutALastEvent_LastInBothDirections_TiesById()
    {
        var rows = new[] { Rows[3], Rows[0] with { Signature = Rows[0].Signature with { Id = 9, LastSeenUtc = null } }, Rows[1] };
        foreach (var descending in new[] { true, false })
            SignatureSorting.Sorted(rows, r => r.Signature, r => r.Today, r => r.Days7, r => r.Days30, SignatureSort.LastSeen, descending)
                .Select(r => r.Signature.Id).Should().Equal(2, 4, 9);
    }

    [Fact]
    public void Default_IsTheNewestEventFirst()
    {
        SignatureSorting.Default.Should().Be(SignatureSort.LastSeen);
        SignatureSorting.DefaultDescending.Should().BeTrue();
        SignatureSorting.Parse(null, null).Should().Be((SignatureSort.LastSeen, true));
    }

    [Theory]
    [InlineData("last", "desc", SignatureSort.LastSeen, true)]
    [InlineData("DP", "ASC", SignatureSort.DeltaP, false)]
    [InlineData(" name ", null, SignatureSort.Name, false)]        // a text starts A→Z
    [InlineData("7d", "", SignatureSort.Days7, true)]              // a number starts largest first
    [InlineData("30d", "sideways", SignatureSort.Days30, true)]
    [InlineData("source", "desc", SignatureSort.Source, true)]
    [InlineData("nimic", "asc", SignatureSort.LastSeen, true)]     // unknown column: the default, whatever the direction
    [InlineData("", "asc", SignatureSort.LastSeen, true)]
    [InlineData("<script>", "desc", SignatureSort.LastSeen, true)]
    [InlineData("1", "desc", SignatureSort.LastSeen, true)]        // not an enum number
    [InlineData("LastSeen", "asc", SignatureSort.LastSeen, true)]  // nor an enum name: only the address keys
    public void Parse_TheAddress_WrongValuesFallBackWithoutThrowing(string? sort, string? dir, SignatureSort expected, bool descending)
    {
        SignatureSorting.Parse(sort, dir).Should().Be((expected, descending));
    }

    [Fact]
    public void Keys_RoundTripForEveryColumn()
    {
        foreach (var sort in Enum.GetValues<SignatureSort>())
            foreach (var descending in new[] { true, false })
                SignatureSorting.Parse(SignatureSorting.Key(sort), SignatureSorting.Direction(descending)).Should().Be((sort, descending));
        Enum.GetValues<SignatureSort>().Select(SignatureSorting.Key).Should().OnlyHaveUniqueItems();
        SignatureSorting.Key(SignatureSort.LastSeen).Should().Be("last");
        SignatureSorting.Direction(true).Should().Be("desc");
    }

    [Fact]
    public void Click_SameColumnReverses_AnotherStartsInItsOwnDirection_AndThePageGoesBackToTheFirst()
    {
        SignatureSorting.Click(SignatureSort.LastSeen, true, SignatureSort.LastSeen).Should().Be((SignatureSort.LastSeen, false));
        SignatureSorting.Click(SignatureSort.LastSeen, false, SignatureSort.LastSeen).Should().Be((SignatureSort.LastSeen, true));
        SignatureSorting.Click(SignatureSort.LastSeen, false, SignatureSort.Name).Should().Be((SignatureSort.Name, false));
        SignatureSorting.Click(SignatureSort.Name, false, SignatureSort.DeltaP).Should().Be((SignatureSort.DeltaP, true));

        SignatureSorting.PageAfter(3, (SignatureSort.LastSeen, true), (SignatureSort.LastSeen, true)).Should().Be(3, "same order: the page stays");
        SignatureSorting.PageAfter(3, (SignatureSort.LastSeen, true), (SignatureSort.LastSeen, false)).Should().Be(0, "reversed");
        SignatureSorting.PageAfter(3, (SignatureSort.LastSeen, true), (SignatureSort.Count, true)).Should().Be(0, "another column");
    }
}
