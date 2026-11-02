using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Miele;

/// <summary>A state change of an appliance kept in memory for the ground truth: from <see cref="FromUtc"/> on.</summary>
public sealed record MieleStatusPoint(DateTimeOffset FromUtc, int? StatusRaw, string? PhaseText);

/// <summary>An appliance with a linked plug, its alias and its recent states (oldest first).</summary>
public sealed record MieleLinkedAppliance(Guid PlugId, string Alias, IReadOnlyList<MieleStatusPoint> Timeline);

/// <summary>
/// Ground truth for the appliance detection: a meter event already matched to a plug (<c>LabelPlugId</c>) whose plug
/// feeds a Miele appliance that was Running at the event's time is labelled „&lt;alias&gt; · &lt;faza&gt;”. Never for an
/// event without the plug's confirmation (an appliance running is not proof that a step of the house meter is it —
/// see the condensate pump mislabel in observatii-consumatori.md), never from a state older than the timeline.
/// </summary>
public static class MieleGroundTruthRule
{
    public const string Separator = " · ";

    /// <summary>The label, or null (no plug, no appliance on that plug, not Running then, phase unknown).</summary>
    public static string? Label(Guid? labelPlugId, DateTimeOffset tsUtc, IEnumerable<MieleLinkedAppliance> appliances)
    {
        if (labelPlugId is not { } plug) return null;
        foreach (var a in appliances)
        {
            if (a.PlugId != plug) continue;
            var at = a.Timeline.LastOrDefault(p => p.FromUtc <= tsUtc);
            if (at is { StatusRaw: MieleTexts.StatusRunning, PhaseText: { Length: > 0 } phase } && !MieleTexts.IsIdlePhase(phase))
                return Name(a.Alias, phase);
        }
        return null;
    }

    public static string Name(string alias, string phase) => alias.Trim() + Separator + phase.Trim();

    /// <summary>A name given by this rule (kept by the plug naming of signatures instead of the plug's alias).</summary>
    public static bool IsGroundTruthName(string? name) =>
        name is not null && name.Contains(Separator, StringComparison.Ordinal);

    /// <summary>The phase part of a ground-truth name („Mașina · Centrifugare” → „Centrifugare”).</summary>
    public static string? PhaseOf(string? name)
    {
        if (!IsGroundTruthName(name)) return null;
        var i = name!.LastIndexOf(Separator, StringComparison.Ordinal);
        return name[(i + Separator.Length)..];
    }
}

/// <summary>Energy a plug measured over a cycle: Σ of the live minute aggregates, else the 10 s polls held until the next one.</summary>
public static class MielePlugEnergy
{
    /// <summary>A poll is held at most this long (a gap longer than that is not counted).</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(5);

    public static double? FromMinutes(IReadOnlyList<LiveMinuteAggregate> minutes, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var inRange = minutes.Where(m => m.MinuteUtc >= fromUtc.AddSeconds(-59) && m.MinuteUtc < toUtc).ToList();
        if (inRange.Count == 0) return null;
        double wh = 0;
        foreach (var m in inRange)
        {
            // Partial first/last minute: the share of the minute inside the cycle.
            var start = m.MinuteUtc < fromUtc ? fromUtc : m.MinuteUtc;
            var end = m.MinuteUtc.AddMinutes(1) > toUtc ? toUtc : m.MinuteUtc.AddMinutes(1);
            var share = Math.Clamp((end - start).TotalSeconds / 60.0, 0, 1);
            wh += m.EnergyWh * share;
        }
        return wh;
    }

    public static double? FromSamples(IReadOnlyList<Sample> samples, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var sorted = samples.Where(s => s.Online && s.PowerMw is not null).OrderBy(s => s.TimestampUtc).ToList();
        if (sorted.Count == 0 || toUtc <= fromUtc) return null;
        double wh = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i].TimestampUtc;
            var end = i + 1 < sorted.Count ? sorted[i + 1].TimestampUtc : toUtc;
            if (end - start > MaxHold) end = start + MaxHold;
            var s = start > fromUtc ? start : fromUtc;
            var e = end < toUtc ? end : toUtc;
            if (e <= s) continue;
            wh += sorted[i].PowerMw!.Value / 1000.0 * (e - s).TotalHours;
        }
        return wh;
    }
}
