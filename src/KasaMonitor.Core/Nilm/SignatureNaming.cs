using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Nilm;

/// <summary>Naming of signatures from the plug labels of their events, and the house power over a run.</summary>
public static class SignatureNaming
{
    /// <summary>Share of a signature's events that must be matched to the same plug for the signature to take its name.</summary>
    public const double PlugShare = 0.6;

    /// <summary>At least this many events labelled by the plug before a signature is linked to it (one coincidence is not enough).</summary>
    public const int MinPlugEvents = 3;

    /// <summary>The plug rule: ≥ <see cref="MinPlugEvents"/> events from the plug and ≥ 60 % of the signature's events.</summary>
    public static bool LinksToPlug(int plugEvents, int signatureEvents) =>
        signatureEvents > 0 && plugEvents >= MinPlugEvents && plugEvents >= PlugShare * signatureEvents;

    /// <summary>
    /// The plug rule with „un singur aparat pe priză”: a signature already linked to a single-appliance plug stays linked
    /// while any of its events in the window comes from that plug (no 60 % / <see cref="MinPlugEvents"/> threshold: the
    /// plug explains its events completely); otherwise <see cref="LinksToPlug(int, int)"/>.
    /// </summary>
    public static bool LinksToPlug(int plugEvents, int signatureEvents, bool singleAppliancePlug, bool linkedToThatPlug) =>
        LinksToPlug(plugEvents, signatureEvents) || singleAppliancePlug && linkedToThatPlug && plugEvents > 0;

    /// <summary>The plug that labelled ≥ 60 % of the events (with its alias), or null.</summary>
    public static (Guid PlugId, string Alias)? FromPlugs(IReadOnlyCollection<MeterEvent> events)
    {
        if (events.Count == 0) return null;
        var top = events.Where(e => e.Source == EventLabelSource.AutoPlug && e.LabelPlugId is not null && !string.IsNullOrEmpty(e.Label))
            .GroupBy(e => e.LabelPlugId!.Value)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (top is null || !LinksToPlug(top.Count(), events.Count)) return null;
        return (top.Key, top.OrderByDescending(e => e.TsUtc).First().Label!);
    }

    /// <summary>Longest name a user can give a signature or an event.</summary>
    public const int MaxNameLength = 64;

    /// <summary>A user-entered name: trimmed, inner whitespace collapsed, at most <see cref="MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name)
    {
        var clean = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= MaxNameLength ? clean : clean[..MaxNameLength].TrimEnd();
    }

    /// <summary>The default name of a signature found by clustering.</summary>
    public static string Unknown(long id) => $"Necunoscut #{id}";

    /// <summary>
    /// Mean power over [fromUtc, toUtc) of a series stored on change: each sample holds until the next one (for at most
    /// <see cref="MeterEventDetector.MaxFillSeconds"/>); null when the samples cover less than half of the range.
    /// </summary>
    public static double? MeanPower(IReadOnlyList<LiveSample> samples, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc || samples.Count == 0) return null;
        double ws = 0, covered = 0;
        var sorted = samples.OrderBy(s => s.TsUtc).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var start = sorted[i].TsUtc;
            var end = i + 1 < sorted.Count ? sorted[i + 1].TsUtc : toUtc;
            var maxEnd = start.AddSeconds(MeterEventDetector.MaxFillSeconds);
            if (end > maxEnd) end = maxEnd;
            var s = start > fromUtc ? start : fromUtc;
            var e = end < toUtc ? end : toUtc;
            if (e <= s) continue;
            var sec = (e - s).TotalSeconds;
            ws += sorted[i].PowerW * sec;
            covered += sec;
        }
        return covered >= 0.5 * (toUtc - fromUtc).TotalSeconds ? ws / covered : null;
    }
}
