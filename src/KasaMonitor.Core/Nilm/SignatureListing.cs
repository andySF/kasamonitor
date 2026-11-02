namespace KasaMonitor.Core.Nilm;

/// <summary>
/// Which detected signatures /consumatori lists by default, and which ones „Curăță semnăturile rare” deletes. After a few
/// hours the clustering leaves many „Necunoscut #n” with one or two events (mostly small steps of 15–50 W); they stay
/// available behind a switch and can be cleaned once they are old.
/// </summary>
public static class SignatureListing
{
    /// <summary>A signature with at least this many events is listed by default.</summary>
    public const int MinEventsShown = 3;

    /// <summary>A rare signature has at most this many events…</summary>
    public const int RareMaxEvents = 2;

    /// <summary>…and its newest event is older than this before it can be cleaned.</summary>
    public static readonly TimeSpan RareMinAge = TimeSpan.FromDays(7);

    /// <summary>A name other than the default „Necunoscut #n” (renamed, labelled, named after a plug).</summary>
    public static bool HasOwnName(ApplianceSignature s) =>
        s.LabelSource == SignatureLabelSource.User || !string.Equals(s.Name, SignatureNaming.Unknown(s.Id), StringComparison.Ordinal);

    /// <summary>Listed by default: ≥ 3 events, or a name of its own, or a „posibil &lt;priză&gt;” badge.</summary>
    public static bool ShownByDefault(ApplianceSignature s, bool possiblePlug) =>
        s.Count >= MinEventsShown || HasOwnName(s) || possiblePlug;

    /// <summary>
    /// Deleted by the cleaning: at most 2 events, the newest older than 7 days, not measured by a plug, without a name of
    /// its own and without a „posibil &lt;priză&gt;” badge (<paramref name="possiblePlug"/>). A signature without any
    /// event time is kept.
    /// </summary>
    public static bool IsRareAndOld(ApplianceSignature s, DateTimeOffset nowUtc, bool possiblePlug) =>
        !possiblePlug && !s.ConfirmedByPlug && !HasOwnName(s) && s.Count <= RareMaxEvents
        && s.LastSeenUtc is { } last && nowUtc - last > RareMinAge;

    /// <summary>Order of the list: energy over the last 7 days (largest first), then events, then id.</summary>
    public static IEnumerable<T> Ordered<T>(IEnumerable<T> rows, Func<T, ApplianceSignature> signature, Func<T, double> last7DaysWh) =>
        rows.OrderByDescending(last7DaysWh).ThenByDescending(r => signature(r).Count).ThenBy(r => signature(r).Id);
}
