namespace KasaMonitor.Core.Home;

/// <summary>One placed bubble: centre and slot radius (px, in the layout's viewBox); <see cref="Side"/> = small one in the side column.</summary>
public sealed record PlacedBubble(string Key, double X, double Y, double SlotR, bool Side);

/// <summary>
/// The positions of the bubbles for one set and order. <see cref="K"/> = px per √W (area ∝ W); a bubble is drawn with
/// radius max(<see cref="MinR"/>, K·√W) and must stay within its slot (<see cref="Fits"/>), otherwise the layout is
/// recomputed. <see cref="LabelX"/>: x of the labels of the side column.
/// </summary>
public sealed record BubbleLayoutResult(IReadOnlyList<string> Order, IReadOnlyList<PlacedBubble> Bubbles, double K, double MinR,
    double Width, double Height, double LabelX)
{
    public double Radius(double watts) => Math.Max(MinR, K * Math.Sqrt(Math.Max(0, watts)));

    /// <summary>Every value still fits its slot (no overlap possible).</summary>
    public bool Fits(IReadOnlyDictionary<string, double> watts) =>
        Bubbles.All(b => !watts.TryGetValue(b.Key, out var w) || Radius(w) <= b.SlotR + 1e-6);
}

/// <summary>
/// Deterministic greedy packing of the "Acum în casă" bubbles (the algorithm of the approved sketch): bubbles in order of
/// size, each one touching an already placed one, at the free position closest to the centre under an elliptical metric (a
/// wide group); small bubbles (radius under <see cref="SmallR"/>) go in a labelled column to the right. Positions are
/// recomputed only when the set or the order changes or a value outgrows its slot (<see cref="BubbleLayoutResult.Fits"/>),
/// not every second.
/// </summary>
public static class BubbleLayout
{
    public const double Gap = 3;
    public const double SmallR = 30;
    public const double MinRadius = 8;
    /// <summary>Slots are this much larger than the bubble at layout time, so small changes keep the positions.</summary>
    public const double Slack = 1.15;
    public const int Angles = 72;
    public const double SideLabelWidth = 170;
    /// <summary>Order hysteresis: a later bubble moves up only above this factor of the earlier one…</summary>
    public const double SwapFactor = 1.1;
    /// <summary>…plus this many watts (small values wobble by a few W).</summary>
    public const double SwapMarginW = 2;
    /// <summary>Horizontal stretch of the packing metric: the group is wider than tall (the card is wide).</summary>
    public const double Stretch = 1.9;
    /// <summary>Largest bubble radius: at most this many px…</summary>
    public const double MaxRadius = 78;
    /// <summary>…and at most this share of the height and of the width.</summary>
    public const double MaxRadiusOfHeight = 0.27;
    public const double MaxRadiusOfWidth = 0.13;
    /// <summary>Gap between the big group and the side column, and between a side bubble and its label.</summary>
    public const double ColumnGap = 22;
    public const double LabelGap = 8;

    /// <summary>
    /// The order of the bubbles, largest first, with hysteresis: starting from <paramref name="previous"/>, two neighbours
    /// swap only when the later one is clearly larger (more than 10 % + 2 W), so values that wobble around each other do not
    /// move the bubbles. New keys are appended (largest first), missing ones dropped. Without a previous order: by W
    /// descending, then key.
    /// </summary>
    public static IReadOnlyList<string> Order(IReadOnlyList<string>? previous, IReadOnlyDictionary<string, double> watts)
    {
        var list = previous is null
            ? watts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key).ToList()
            : previous.Where(watts.ContainsKey)
                .Concat(watts.Keys.Where(k => !previous.Contains(k)).OrderByDescending(k => watts[k]).ThenBy(k => k, StringComparer.Ordinal))
                .ToList();
        bool swapped;
        var guard = 0;
        do
        {
            swapped = false;
            for (var i = 0; i + 1 < list.Count; i++)
            {
                if (watts[list[i + 1]] > watts[list[i]] * SwapFactor + SwapMarginW)
                {
                    (list[i], list[i + 1]) = (list[i + 1], list[i]);
                    swapped = true;
                }
            }
        } while (swapped && ++guard < 10_000);
        return list;
    }

    /// <summary>Greedy packing of circles of the given radii (in order); the first one at (0, 0).</summary>
    public static IReadOnlyList<(double X, double Y)> Pack(IReadOnlyList<double> radii, double gap = Gap)
    {
        var placed = new List<(double X, double Y, double R)>();
        foreach (var r in radii)
        {
            if (placed.Count == 0) { placed.Add((0, 0, r)); continue; }
            (double X, double Y)? best = null;
            var bestScore = double.MaxValue;
            foreach (var p in placed)
            {
                for (var a = 0; a < Angles; a++)
                {
                    var t = a / (double)Angles * Math.PI * 2;
                    var d = p.R + r + gap;
                    var x = p.X + d * Math.Cos(t);
                    var y = p.Y + d * Math.Sin(t);
                    var ok = true;
                    foreach (var q in placed)
                    {
                        var dx = q.X - x;
                        var dy = q.Y - y;
                        if (Math.Sqrt(dx * dx + dy * dy) < q.R + r + gap - 0.01) { ok = false; break; }
                    }
                    if (!ok) continue;
                    var score = x / Stretch * (x / Stretch) + y * y;
                    if (score < bestScore - 1e-9) { bestScore = score; best = (x, y); }
                }
            }
            var (bx, by) = best ?? (placed.Max(q => q.X + q.R) + r + gap, 0);
            placed.Add((bx, by, r));
        }
        return placed.Select(p => (p.X, p.Y)).ToList();
    }

    /// <summary>Lays out the bubbles in <paramref name="order"/> into a <paramref name="width"/> × <paramref name="height"/> viewBox.</summary>
    public static BubbleLayoutResult Layout(IReadOnlyList<string> order, IReadOnlyDictionary<string, double> watts, double width, double height)
    {
        if (order.Count == 0) return new BubbleLayoutResult(order, Array.Empty<PlacedBubble>(), 1, MinRadius, width, height, 0);
        var maxW = Math.Max(1, order.Max(k => Math.Max(0, watts[k])));
        var rMax = Math.Min(MaxRadius, Math.Min(height * MaxRadiusOfHeight, width * MaxRadiusOfWidth));
        var k = rMax / Math.Sqrt(maxW);
        var slots = order.Select(key => (Key: key, R: Math.Max(MinRadius, k * Math.Sqrt(Math.Max(0, watts[key]))) * Slack)).ToList();
        var big = slots.Where(s => s.R >= SmallR).ToList();
        var small = slots.Where(s => s.R < SmallR).ToList();
        if (big.Count == 0) { big = small.Take(1).ToList(); small = small.Skip(1).ToList(); }

        var pos = Pack(big.Select(b => b.R).ToList());
        var centres = new Dictionary<string, (double X, double Y, double R, bool Side)>();
        for (var i = 0; i < big.Count; i++) centres[big[i].Key] = (pos[i].X, pos[i].Y, big[i].R, false);

        var minX = big.Select((b, i) => pos[i].X - b.R).Min();
        var maxX = big.Select((b, i) => pos[i].X + b.R).Max();
        var minY = big.Select((b, i) => pos[i].Y - b.R - 4).Min();
        var maxY = big.Select((b, i) => pos[i].Y + b.R + 4).Max();
        double labelX = 0;
        if (small.Count > 0)
        {
            var rS = small.Max(s => s.R);
            var colX = maxX + ColumnGap + rS;
            var colH = small.Sum(s => Math.Max(22, 2 * s.R + 8));
            var mid = (minY + maxY) / 2;
            var y = mid - colH / 2;
            foreach (var s in small)
            {
                var hh = Math.Max(22, 2 * s.R + 8);
                centres[s.Key] = (colX, y + hh / 2, s.R, true);
                y += hh;
            }
            labelX = colX + rS + LabelGap;
            maxX = labelX + SideLabelWidth;
            minY = Math.Min(minY, mid - colH / 2);
            maxY = Math.Max(maxY, mid + colH / 2);
        }
        var bw = maxX - minX;
        var bh = maxY - minY;
        var scale = Math.Min(1, Math.Min(width / bw, height / bh));
        var cx0 = width / 2 - (minX + bw / 2) * scale;
        var cy0 = height / 2 - (minY + bh / 2) * scale;
        var placed = order.Select(key =>
        {
            var c = centres[key];
            return new PlacedBubble(key, Math.Round(cx0 + c.X * scale, 2), Math.Round(cy0 + c.Y * scale, 2), c.R * scale, c.Side);
        }).ToList();
        return new BubbleLayoutResult(order, placed, k * scale, MinRadius * scale, width, height, Math.Round(cx0 + labelX * scale, 2));
    }
}
