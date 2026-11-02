using System.Globalization;
using KasaMonitor.Core.Home;
using KasaMonitor.Core.Reports;

namespace KasaMonitor.Web.Ui;

/// <summary>
/// Geometry of the hand-drawn SVG charts of the home page (bars, Sankey ribbons). Coordinates are written with the
/// invariant culture (an SVG attribute never takes a decimal comma).
/// </summary>
public static class HomeSvg
{
    /// <summary>A coordinate for an SVG attribute: at most 2 decimals, invariant culture.</summary>
    public static string N(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The smallest "nice" axis maximum (1, 2, 2.5, 5 × 10^k) at or above <paramref name="max"/>.</summary>
    public static double NiceMax(double max)
    {
        if (max <= 0 || double.IsNaN(max)) return 1;
        var exp = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var m in new[] { 1, 2, 2.5, 5, 10 })
            if (m * exp >= max - 1e-12) return m * exp;
        return 10 * exp;
    }

    /// <summary>The decimals of an axis whose ticks are multiples of <paramref name="step"/>, the same for every tick (0,25 · 0,50 · 0,75).</summary>
    public static int AxisDecimals(double step)
    {
        for (var d = 0; d <= 3; d++)
        {
            var scaled = step * Math.Pow(10, d);
            if (Math.Abs(scaled - Math.Round(scaled)) < 1e-9) return d;
        }
        return 3;
    }

    /// <summary>
    /// The previous period's values aligned on the current buckets: a day by local hour (and the repeated 03:00 of the autumn
    /// DST day), so on a 23 h or 25 h day the pale bar stays on its own hour; other periods by index.
    /// </summary>
    public static IReadOnlyList<double?> AlignPrevious(IReadOnlyList<ReportBucket> buckets, IReadOnlyList<ReportBucket>? previousBuckets,
        IReadOnlyList<double?>? previous, ReportGranularity kind)
    {
        if (previous is null) return buckets.Select(_ => (double?)null).ToList();
        if (kind != ReportGranularity.Day || previousBuckets is null || previousBuckets.Count != previous.Count)
            return buckets.Select((_, i) => i < previous.Count ? previous[i] : null).ToList();
        var byHour = new Dictionary<(TimeSpan, bool), double?>();
        for (var i = 0; i < previousBuckets.Count; i++)
            byHour[(previousBuckets[i].LocalStart.TimeOfDay, previousBuckets[i].RepeatedHour)] = previous[i];
        return buckets.Select(b => byHour.TryGetValue((b.LocalStart.TimeOfDay, b.RepeatedHour), out var v) ? v : null).ToList();
    }

    /// <summary>A bar with rounded top corners, from <paramref name="baseY"/> up to <paramref name="y"/>.</summary>
    public static string TopRoundedBar(double x, double y, double w, double baseY, double r = 4)
    {
        var h = baseY - y;
        r = Math.Max(0, Math.Min(r, Math.Min(w / 2, h)));
        return $"M{N(x)},{N(baseY)}V{N(y + r)}Q{N(x)},{N(y)} {N(x + r)},{N(y)}H{N(x + w - r)}Q{N(x + w)},{N(y)} {N(x + w)},{N(y + r)}V{N(baseY)}Z";
    }

    /// <summary>A horizontal bar with rounded right corners.</summary>
    public static string RightRoundedBar(double x, double y, double w, double h, double r = 4)
    {
        r = Math.Max(0, Math.Min(r, Math.Min(w, h / 2)));
        return $"M{N(x)},{N(y)}H{N(x + w - r)}Q{N(x + w)},{N(y)} {N(x + w)},{N(y + r)}V{N(y + h - r)}Q{N(x + w)},{N(y + h)} {N(x + w - r)},{N(y + h)}H{N(x)}Z";
    }

    /// <summary>A Sankey ribbon (cubic Bézier edges) from [y0a, y0b] at x0 to [y1a, y1b] at x1.</summary>
    public static string Ribbon(double x0, double y0a, double y0b, double x1, double y1a, double y1b)
    {
        var mx = (x0 + x1) / 2;
        return $"M{N(x0)},{N(y0a)}C{N(mx)},{N(y0a)} {N(mx)},{N(y1a)} {N(x1)},{N(y1a)}V{N(y1b)}C{N(mx)},{N(y1b)} {N(mx)},{N(y0b)} {N(x0)},{N(y0b)}Z";
    }

    /// <summary>One right-hand node of a Sankey: its slot (Y, H) and where its ribbon leaves the source node (SourceY).</summary>
    public sealed record SankeyNode(FlowItem Item, double Y, double H, double SourceY);

    /// <summary>The Sankey geometry: the source node and the right-hand nodes, heights ∝ kWh.</summary>
    public sealed record SankeyGeometry(double SourceY, double SourceH, IReadOnlyList<SankeyNode> Nodes);

    /// <summary>
    /// Layout of a 2-column Sankey in a box of <paramref name="height"/>: every right-hand node gets at least
    /// <paramref name="minSlot"/> px (a readable label), the space so used is taken from the scale; the source node is
    /// centred and the ribbons leave it in order.
    /// </summary>
    public static SankeyGeometry Sankey(IReadOnlyList<FlowItem> flows, double sourceKwh, double height, double top = 6, double bottom = 6,
        double gap = 6, double minSlot = 15)
    {
        var total = Math.Max(sourceKwh, flows.Sum(f => f.Kwh));
        var avail = Math.Max(10, height - top - bottom - gap * Math.Max(0, flows.Count - 1));
        var k = total > 0 ? avail / total : 0;
        for (var it = 0; it < 3 && total > 0; it++)
        {
            var extra = flows.Sum(f => Math.Max(0, minSlot - f.Kwh * k));
            k = Math.Max(0, (avail - extra) / total);
        }
        var sourceH = total * k;
        var sy = top + (height - top - bottom - sourceH) / 2;
        var yL = sy;
        var yR = top;
        var nodes = new List<SankeyNode>();
        foreach (var f in flows)
        {
            var h = f.Kwh * k;
            nodes.Add(new SankeyNode(f, yR, h, yL));
            yL += h;
            yR += Math.Max(h, minSlot) + gap;
        }
        return new SankeyGeometry(sy, sourceH, nodes);
    }
}
