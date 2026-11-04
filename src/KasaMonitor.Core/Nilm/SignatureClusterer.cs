namespace KasaMonitor.Core.Nilm;

/// <summary>
/// The clustering features of one event. <see cref="Transient"/> is NaN when unknown: a switch-off (no inrush) or a
/// switch-on whose peak was not seen (<see cref="TransientRule"/>).
/// </summary>
public readonly record struct EventFeatures(long Id, double DeltaP, double DeltaQ, double Transient)
{
    public static EventFeatures Of(MeterEvent e) => new(e.Id, e.DeltaP, e.DeltaQ, e.TransientKnown ? e.TransientRatio : double.NaN);

    /// <summary>|ΔP|: a switch-on and the switch-off of the same appliance have the same magnitude.</summary>
    public double Magnitude => Math.Abs(DeltaP);

    /// <summary>ΔQ seen from the switch-on side (a switch-off's ΔQ is mirrored).</summary>
    public double OnSideQ => DeltaP >= 0 ? DeltaQ : -DeltaQ;

    /// <summary>A switch-on with a known transient ratio: the only events whose transient is compared.</summary>
    public bool HasTransient => DeltaP > 0 && !double.IsNaN(Transient);
}

/// <summary>
/// Running means of a cluster or signature (the matching "centre" of incremental attachment). <see cref="HasTransient"/>:
/// at least one of its switch-ons has a known transient (<see cref="MeanTransient"/> is their mean).
/// </summary>
public readonly record struct SignatureCentre(long Id, double MeanDeltaP, double MeanDeltaQ, double MeanTransient, bool HasTransient)
{
    /// <summary>A stored signature: a mean transient of 0 means none known (a known ratio is ≈ 1 or more: rise 0 or an inrush, see <see cref="TransientRule"/>).</summary>
    public static SignatureCentre Of(ApplianceSignature s) => new(s.Id, s.MeanDeltaP, s.MeanDeltaQ, s.MeanTransient, s.MeanTransient > 0);
}

/// <summary>
/// An anchor's centre and the plug it is linked to (an event labelled by another plug never goes to it).
/// <see cref="Exclusive"/>: only an event labelled by that plug goes to it (<see cref="ExclusiveSignatures"/>).
/// </summary>
public readonly record struct AnchorCentre(SignatureCentre Centre, Guid? PlugId, bool Exclusive = false)
{
    public static AnchorCentre Of(ApplianceSignature s, IReadOnlySet<Guid> exclusivePlugs) =>
        new(SignatureCentre.Of(s), s.PlugId, ExclusiveSignatures.IsExclusive(s, exclusivePlugs));
}

/// <summary>
/// DBSCAN over the events (own implementation): two events are neighbours when their |ΔP| (compared relatively, i.e. on
/// a log scale), their ΔQ (seen from the switch-on side) and, for two switch-ons, their transient ratio are all within
/// eps (a transient ratio that is unknown, see <see cref="TransientRule"/>, is not compared). eps(|ΔP|) = <see cref="EpsRelative"/> × |ΔP| + <see cref="EpsAbsoluteW"/> (8 % + 3 W); eps(ΔQ) = 8 % of |ΔP| +
/// <see cref="EpsAbsoluteVar"/> (ΔQ is a whole-house difference, noisier than ΔP); transient eps = 0.5 + 25 %. A
/// cluster needs <see cref="MinPoints"/> events, and is cut where it grows wider than <see cref="MaxSpan"/> (a dense band
/// of small loads would otherwise chain 30 ↔ 33 ↔ 36 W … into one cluster). Switch-ons and switch-offs of the same
/// magnitude end up in the same cluster (one appliance = one signature).
/// </summary>
public static class SignatureClusterer
{
    public const double EpsRelative = 0.08;
    public const double EpsAbsoluteW = 3;
    public const double EpsAbsoluteVar = 15;
    public const int MinPoints = 3;

    /// <summary>
    /// Tolerance scale of the anchors (<see cref="ApplianceSignature.IsAnchor"/>: labelled by the user or linked to a
    /// plug): 1.5 × eps ≈ 12 % of |ΔP|. A labelled appliance wins over an anonymous group that happens to be closer.
    /// </summary>
    public const double AnchorScale = 1.5;

    /// <summary>Most events clustered in one run (the newest); bounds time and memory of the nightly job.</summary>
    public const int MaxEvents = 20_000;

    /// <summary>Widest cluster: max |ΔP| ≤ (1 + 2 × 8 %) × min |ΔP| + 2 × 3 W.</summary>
    public static double MaxSpan(double minMagnitude) => (1 + 2 * EpsRelative) * minMagnitude + 2 * EpsAbsoluteW;

    /// <summary>The magnitude tolerance between two powers (also the on/off pairing tolerance of runs).</summary>
    public static double Eps(double magnitudeA, double magnitudeB, double scale = 1) =>
        scale * (EpsRelative * Math.Max(Math.Abs(magnitudeA), Math.Abs(magnitudeB)) + EpsAbsoluteW);

    public static double EpsQ(double magnitudeA, double magnitudeB, double scale = 1) =>
        scale * (EpsRelative * Math.Max(Math.Abs(magnitudeA), Math.Abs(magnitudeB)) + EpsAbsoluteVar);

    public static double TransientEps(double a, double b, double scale = 1) => scale * (0.5 + 0.25 * Math.Max(a, b));

    /// <summary>
    /// Normalised distance (≤ 1 = neighbours): the largest of the three feature differences divided by its eps.
    /// The transient ratio counts only when both are switch-ons with a known transient (a switch-off has no inrush).
    /// </summary>
    public static double Distance(EventFeatures a, EventFeatures b, double scale = 1) =>
        Distance(a.Magnitude, a.OnSideQ, a.HasTransient ? a.Transient : null, b.Magnitude, b.OnSideQ, b.HasTransient ? b.Transient : null, scale);

    /// <summary>Distance of an event to a signature centre (transient compared only for a switch-on and a centre that both have one).</summary>
    public static double Distance(EventFeatures e, SignatureCentre c, double scale = 1) =>
        Distance(e.Magnitude, e.OnSideQ, e.HasTransient ? e.Transient : null, c.MeanDeltaP, c.MeanDeltaQ, c.HasTransient ? c.MeanTransient : null, scale);

    /// <summary>Distance between two signature centres (the automatic merge, <see cref="SignatureMerger"/>).</summary>
    public static double Distance(SignatureCentre a, SignatureCentre b, double scale = 1) =>
        Distance(a.MeanDeltaP, a.MeanDeltaQ, a.HasTransient ? a.MeanTransient : null, b.MeanDeltaP, b.MeanDeltaQ, b.HasTransient ? b.MeanTransient : null, scale);

    private static double Distance(double pA, double qA, double? tA, double pB, double qB, double? tB, double scale)
    {
        var d = Math.Max(Math.Abs(pA - pB) / Eps(pA, pB, scale), Math.Abs(qA - qB) / EpsQ(pA, pB, scale));
        if (tA is { } ta && tB is { } tb)
            d = Math.Max(d, Math.Abs(ta - tb) / TransientEps(ta, tb, scale));
        return d;
    }

    /// <summary>
    /// DBSCAN: returns, for every input event, its cluster number (0, 1, …) or -1 = noise. Neighbour search runs on the
    /// events sorted by |ΔP| (only a band of ± eps can match); every point enters the expansion queue at most once, so
    /// memory stays O(n). Clusters wider than <see cref="MaxSpan"/> are then cut by |ΔP| (parts smaller than
    /// <paramref name="minPoints"/> become noise).
    /// </summary>
    public static int[] Cluster(IReadOnlyList<EventFeatures> events, double scale = 1, int minPoints = MinPoints)
    {
        var n = events.Count;
        var labels = Enumerable.Repeat(Unvisited, n).ToArray();
        var queued = new bool[n];
        var order = Enumerable.Range(0, n).OrderBy(i => events[i].Magnitude).ToArray();
        var rank = new int[n];
        for (var r = 0; r < n; r++) rank[order[r]] = r;
        var buffer = new List<int>();

        List<int> Neighbours(int i)
        {
            buffer.Clear();
            var m = events[i].Magnitude;
            // Any neighbour has |ΔP| within Eps(m, its |ΔP|) ≤ Eps of the larger one: walk the sorted band both ways.
            for (var r = rank[i]; r >= 0; r--)
            {
                var j = order[r];
                if (m - events[j].Magnitude > Eps(m, events[j].Magnitude, scale)) break;
                if (Distance(events[i], events[j], scale) <= 1) buffer.Add(j);
            }
            for (var r = rank[i] + 1; r < n; r++)
            {
                var j = order[r];
                if (events[j].Magnitude - m > Eps(m, events[j].Magnitude, scale)) break;
                if (Distance(events[i], events[j], scale) <= 1) buffer.Add(j);
            }
            return buffer;
        }

        var cluster = -1;
        var queue = new Queue<int>();
        for (var i = 0; i < n; i++)
        {
            if (labels[i] != Unvisited) continue;
            if (Neighbours(i).Count < minPoints) { labels[i] = Noise; continue; }
            cluster++;
            labels[i] = cluster;
            queued[i] = true;
            foreach (var j in buffer)
                if (!queued[j]) { queued[j] = true; queue.Enqueue(j); }
            while (queue.Count > 0)
            {
                var j = queue.Dequeue();
                var wasNoise = labels[j] == Noise;
                labels[j] = cluster;
                if (wasNoise) continue;                       // border point: joins, does not expand
                if (Neighbours(j).Count < minPoints) continue;
                foreach (var k in buffer)
                    if (!queued[k] && labels[k] is Unvisited or Noise) { queued[k] = true; queue.Enqueue(k); }
            }
        }
        return SplitWide(events, labels, minPoints);
    }

    /// <summary>Cuts every cluster wider than <see cref="MaxSpan"/> into consecutive |ΔP| bands.</summary>
    private static int[] SplitWide(IReadOnlyList<EventFeatures> events, int[] labels, int minPoints)
    {
        var result = Enumerable.Repeat(Noise, labels.Length).ToArray();
        var next = 0;
        foreach (var group in Enumerable.Range(0, labels.Length).Where(i => labels[i] >= 0).GroupBy(i => labels[i]).OrderBy(g => g.Key))
        {
            var sorted = group.OrderBy(i => events[i].Magnitude).ToList();
            var part = new List<int>();
            void Flush()
            {
                if (part.Count >= minPoints) { foreach (var i in part) result[i] = next; next++; }
                part.Clear();
            }
            foreach (var i in sorted)
            {
                if (part.Count > 0 && events[i].Magnitude > MaxSpan(events[part[0]].Magnitude)) Flush();
                part.Add(i);
            }
            Flush();
        }
        return result;
    }

    private const int Unvisited = -2;
    private const int Noise = -1;

    /// <summary>The nearest centre within eps (distance ≤ <paramref name="scale"/>-scaled eps), or null.</summary>
    public static long? Nearest(EventFeatures e, IEnumerable<SignatureCentre> centres, double scale = 1)
    {
        long? best = null;
        var bestD = double.MaxValue;
        foreach (var c in centres)
        {
            var d = Distance(e, c, scale);
            if (d <= 1 && d < bestD) { bestD = d; best = c.Id; }
        }
        return best;
    }

    /// <summary>
    /// The anchor of an event within <see cref="AnchorScale"/> × eps, or null. An event labelled by a plug
    /// (<paramref name="labelPlug"/>) goes first to an anchor linked to that plug, and never to an anchor linked to
    /// another plug. An exclusive anchor takes only the events labelled by its own plug.
    /// </summary>
    public static long? NearestAnchor(EventFeatures e, Guid? labelPlug, IEnumerable<AnchorCentre> anchors)
    {
        var list = anchors as IReadOnlyCollection<AnchorCentre> ?? anchors.ToList();
        if (labelPlug is { } plug && Nearest(e, list.Where(a => a.PlugId == plug).Select(a => a.Centre), AnchorScale) is { } own) return own;
        return Nearest(e, list.Where(a => (a.PlugId is null || labelPlug is null || a.PlugId == labelPlug) && (!a.Exclusive || a.PlugId == labelPlug))
            .Select(a => a.Centre), AnchorScale);
    }

    /// <summary>
    /// The classification of an event: its anchor first (<see cref="NearestAnchor"/>: labelled by the user or linked to a
    /// plug, 1.5 × eps), otherwise the nearest of the other centres within eps; null when none.
    /// </summary>
    public static long? Assign(EventFeatures e, Guid? labelPlug, IEnumerable<AnchorCentre> anchors, IEnumerable<SignatureCentre> others) =>
        NearestAnchor(e, labelPlug, anchors) ?? Nearest(e, others);

    /// <summary>Means of a group of events: |ΔP|, switch-on side ΔQ, transient of the switch-ons whose transient is known (0 when none).</summary>
    public static (double MeanDeltaP, double MeanDeltaQ, double MeanTransient) Means(IReadOnlyCollection<EventFeatures> events)
    {
        if (events.Count == 0) return (0, 0, 0);
        var known = events.Where(e => e.HasTransient).ToList();
        return (events.Average(e => e.Magnitude), events.Average(e => e.OnSideQ), known.Count > 0 ? known.Average(e => e.Transient) : 0);
    }
}
