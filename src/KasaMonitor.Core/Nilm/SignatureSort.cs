namespace KasaMonitor.Core.Nilm;

/// <summary>The columns the table of detected signatures on /consumatori can be ordered by.</summary>
public enum SignatureSort
{
    LastSeen,
    DeltaP,
    Count,
    Name,
    Category,
    DeltaQ,
    Transient,
    Today,
    Days7,
    Days30,
    Source,
}

/// <summary>
/// Order of the detected signatures on /consumatori (single owner: the service sorts every row with it before paging,
/// the page keeps the choice in its address as <c>?sort=last&amp;dir=desc</c>). Default: the newest event first.
/// Numbers and times start largest first, texts (name, category, source) A→Z; a second click on the same column reverses
/// it. Rows without a value (no last event, no known transient) stay at the end in both directions; ties by id.
/// </summary>
public static class SignatureSorting
{
    public const SignatureSort Default = SignatureSort.LastSeen;
    public const bool DefaultDescending = true;

    private static readonly IReadOnlyDictionary<SignatureSort, string> Keys = new Dictionary<SignatureSort, string>
    {
        [SignatureSort.LastSeen] = "last",
        [SignatureSort.DeltaP] = "dp",
        [SignatureSort.Count] = "count",
        [SignatureSort.Name] = "name",
        [SignatureSort.Category] = "category",
        [SignatureSort.DeltaQ] = "dq",
        [SignatureSort.Transient] = "transient",
        [SignatureSort.Today] = "today",
        [SignatureSort.Days7] = "7d",
        [SignatureSort.Days30] = "30d",
        [SignatureSort.Source] = "source",
    };

    /// <summary>The value of <c>sort=</c> in the address.</summary>
    public static string Key(SignatureSort sort) => Keys[sort];

    /// <summary>The value of <c>dir=</c> in the address.</summary>
    public static string Direction(bool descending) => descending ? "desc" : "asc";

    /// <summary>Texts start A→Z, numbers and times largest first.</summary>
    public static bool StartsDescending(SignatureSort sort) => sort is not (SignatureSort.Name or SignatureSort.Category or SignatureSort.Source);

    /// <summary>
    /// The choice in the address. An unknown or missing <c>sort</c> gives the default (newest first); a known one with an
    /// unknown or missing <c>dir</c> starts in its own direction. Never throws.
    /// </summary>
    public static (SignatureSort Sort, bool Descending) Parse(string? sort, string? dir)
    {
        var key = sort?.Trim();
        var found = Keys.FirstOrDefault(kv => string.Equals(kv.Value, key, StringComparison.OrdinalIgnoreCase));
        if (found.Value is null) return (Default, DefaultDescending);
        var d = dir?.Trim();
        var descending = string.Equals(d, "desc", StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(d, "asc", StringComparison.OrdinalIgnoreCase) ? false
            : StartsDescending(found.Key);
        return (found.Key, descending);
    }

    /// <summary>A click on a column header: the same column reverses its direction, another one starts in its own.</summary>
    public static (SignatureSort Sort, bool Descending) Click(SignatureSort current, bool descending, SignatureSort clicked) =>
        clicked == current ? (current, !descending) : (clicked, StartsDescending(clicked));

    /// <summary>The page to show after the order may have changed: back to the first one when it did.</summary>
    public static int PageAfter(int page, (SignatureSort Sort, bool Descending) before, (SignatureSort Sort, bool Descending) after) =>
        before == after ? page : 0;

    /// <summary>
    /// Every row in the chosen order (before paging). Name, category (its Romanian name) and source (its Romanian name)
    /// compare with <see cref="StringComparer.CurrentCultureIgnoreCase"/>; rows without a last event or without a known
    /// transient (<see cref="ApplianceSignature.MeanTransient"/> ≤ 0, shown „—”) are last in both directions.
    /// </summary>
    public static IReadOnlyList<T> Sorted<T>(IEnumerable<T> rows, Func<T, ApplianceSignature> signature, Func<T, double> todayWh,
        Func<T, double> last7DaysWh, Func<T, double> last30DaysWh, SignatureSort sort, bool descending)
    {
        Func<T, string>? text = sort switch
        {
            SignatureSort.Name => r => signature(r).Name,
            SignatureSort.Category => r => ApplianceCategories.Get(signature(r).Category).Name,
            SignatureSort.Source => r => NilmSources.Name(signature(r).LabelSource),
            _ => null,
        };
        Func<T, double?> number = sort switch
        {
            SignatureSort.LastSeen => r => signature(r).LastSeenUtc?.UtcTicks,
            SignatureSort.DeltaP => r => signature(r).MeanDeltaP,
            SignatureSort.Count => r => signature(r).Count,
            SignatureSort.DeltaQ => r => signature(r).MeanDeltaQ,
            SignatureSort.Transient => r => signature(r).MeanTransient > 0 ? signature(r).MeanTransient : null,
            SignatureSort.Today => r => todayWh(r),
            SignatureSort.Days7 => r => last7DaysWh(r),
            SignatureSort.Days30 => r => last30DaysWh(r),
            _ => _ => null,
        };
        var words = StringComparer.CurrentCultureIgnoreCase;

        int Primary(T a, T b)
        {
            if (text is not null)
            {
                var c = words.Compare(text(a), text(b));
                return descending ? -c : c;
            }
            var (x, y) = (number(a), number(b));
            if (x is null) return y is null ? 0 : 1;   // no value: last, whatever the direction
            if (y is null) return -1;
            var n = x.Value.CompareTo(y.Value);
            return descending ? -n : n;
        }

        var comparer = Comparer<T>.Create((a, b) =>
        {
            var c = Primary(a, b);
            return c != 0 ? c : signature(a).Id.CompareTo(signature(b).Id);
        });
        return rows.OrderBy(r => r, comparer).ToList();
    }
}
