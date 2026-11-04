using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Nilm;

/// <summary>
/// A signature linked to a plug with a single appliance (<see cref="Plug.Appliance"/>) is <b>exclusive</b>: it holds only
/// what that plug confirmed and what the user put there by hand. Nothing else enters it on any path (live, backfill,
/// re-clustering, the model, the duration rules, the automatic merge), because the plug measures the appliance and a
/// centre fed by whatever happens to lie near it drifts away from it. Measured on 2026-11-04: a fridge of ≈ 60 W
/// with 86 real transitions in 24 h had 1,969 events in its signature, with a mean step of 39 W.
/// </summary>
public static class ExclusiveSignatures
{
    /// <summary>The plugs that make their signatures exclusive: those with a single appliance.</summary>
    public static IReadOnlySet<Guid> Plugs(IEnumerable<Plug> plugs) => plugs.Where(p => p.Appliance is not null).Select(p => p.Id).ToHashSet();

    public static bool IsExclusive(ApplianceSignature s, IReadOnlySet<Guid> plugs) => s.PlugId is { } plug && plugs.Contains(plug);

    /// <summary>
    /// Confirmed by the plug: labelled by it, possibly moved since by the duration of its run
    /// (<see cref="EventLabelSource.DurationRule"/> keeps the plug's label).
    /// </summary>
    public static bool ConfirmedBy(MeterEvent e, Guid plug) =>
        e.LabelPlugId == plug && e.Source is EventLabelSource.AutoPlug or EventLabelSource.DurationRule;

    /// <summary>
    /// What stays in a signature exclusive to <paramref name="plug"/>: what the plug confirmed, what the user labelled, and
    /// what a duration rule put there. That rule refuses an unconfirmed event since 0.13.1 (<see cref="DurationBands.Target"/>),
    /// so the unconfirmed ones left come from „împarte după durată”, a decision of the user.
    /// </summary>
    public static bool Belongs(MeterEvent e, Guid plug) =>
        e.Source is EventLabelSource.User or EventLabelSource.DurationRule || ConfirmedBy(e, plug);

    /// <summary>
    /// The signature an event waits in (<see cref="DurationBands.Canonical"/>), unless that is an exclusive one the event
    /// was not confirmed for: it then stays in <paramref name="signatureId"/>.
    /// </summary>
    public static long Canonical(long signatureId, MeterEvent e, IReadOnlyCollection<ApplianceSignature> all, IReadOnlySet<Guid> plugs)
    {
        var waits = DurationBands.Canonical(signatureId, EventFeatures.Of(e), all);
        if (waits == signatureId) return signatureId;
        return all.First(s => s.Id == waits).PlugId is { } plug && plugs.Contains(plug) && !ConfirmedBy(e, plug) ? signatureId : waits;
    }

    /// <summary>
    /// What a prediction of the classifier means for the signature of an event. <c>SignatureId</c>: the signature with that
    /// name, never an exclusive one; null = none, the event goes through the anchors like any other. <c>KeepsLabel</c>
    /// false: the name points only at exclusive signatures, so the event is treated as not labelled. A name without any
    /// signature keeps its label.
    /// </summary>
    public static (long? SignatureId, bool KeepsLabel) ModelTarget(string? label, IEnumerable<ApplianceSignature> all, IReadOnlySet<Guid> plugs)
    {
        var named = all.Where(s => string.Equals(s.Name, label, StringComparison.Ordinal)).ToList();
        var open = named.Where(s => !IsExclusive(s, plugs)).OrderByDescending(s => s.LabelSource).ThenByDescending(s => s.Count).FirstOrDefault();
        return (open?.Id, open is not null || named.Count == 0);
    }

    /// <summary>The centre of what the plug confirmed and the user labelled in a signature; null when there is nothing of that kind.</summary>
    public static SignatureCentre? ConfirmedCentre(long signatureId, Guid plug, IEnumerable<MeterEvent> events)
    {
        var confirmed = events.Where(e => e.SignatureId == signatureId && (e.Source == EventLabelSource.User || ConfirmedBy(e, plug)))
            .Select(EventFeatures.Of).ToList();
        if (confirmed.Count == 0) return null;
        var (p, q, t) = SignatureClusterer.Means(confirmed);
        return new SignatureCentre(signatureId, p, q, t, t > 0);
    }

    /// <summary>
    /// The signature of a confirmed event that matched no stored centre of its plug: a stored centre may have been fed by
    /// other events, so a new signature must not come from that alone. First the nearest one whose confirmed events
    /// (<see cref="ConfirmedCentre"/>) lie within the anchor radius; otherwise one with no confirmed event at all, which takes
    /// the event as its first (<c>Adopted</c>; the nearest of them). Null: a new component, a new signature.
    /// </summary>
    /// <param name="e">The confirmed event.</param>
    /// <param name="plug">The plug that confirmed it.</param>
    /// <param name="own">The anchors linked to that plug.</param>
    /// <param name="events">Their stored events.</param>
    /// <param name="fresh">Signatures that got their first event in this batch, not stored yet: they are not empty.</param>
    public static (long SignatureId, bool Adopted)? PlugSignature(EventFeatures e, Guid plug, IReadOnlyCollection<ApplianceSignature> own,
        IReadOnlyCollection<MeterEvent> events, IReadOnlySet<long> fresh)
    {
        var centres = own.Select(s => ConfirmedCentre(s.Id, plug, events)).OfType<SignatureCentre>().ToList();
        if (SignatureClusterer.Nearest(e, centres, SignatureClusterer.AnchorScale) is { } near) return (near, false);
        var empty = own.Where(s => !fresh.Contains(s.Id) && centres.All(c => c.Id != s.Id))
            .OrderBy(s => Math.Abs(s.MeanDeltaP - e.Magnitude)).FirstOrDefault();
        return empty is null ? null : (empty.Id, true);
    }
}
