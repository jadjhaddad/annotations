using System;
using System.Collections.Generic;

namespace LabelPlacer;

/// <summary>Diagnostics returned by <see cref="GreedyPlacer.Place"/>.</summary>
public sealed class GreedyResult
{
    public int    UnplacedCount       { get; set; }
    public double FinalOverlapArea    { get; set; }
    public int    FinalOverlapPairs   { get; set; }
    public double FinalMaxPairOverlap { get; set; }
    public int    RefinementSweeps    { get; set; }
}

/// <summary>
/// Greedy label placement using discrete candidate positions, followed by
/// iterative leader-cross refinement.
///
/// ── Initial pass ──────────────────────────────────────────────────────────
/// Candidates are scored with a composite that jointly minimises:
///   1. Rectangle-rectangle overlap area   (×1e6 — hard constraint)
///   2. Outgoing leader-label crossings    (×1000 — label's own leader)
///   3. Candidate preference order         (×1    — tiebreaker)
///
/// ── Refinement sweeps ─────────────────────────────────────────────────────
/// One-pass greedy can't see future labels, so later leaders may still cross
/// earlier label boxes.  After placement, we sweep all labels repeatedly:
///   For each label, re-score every candidate using the full two-way check:
///     • Outgoing crosses  — this label's leader through other label rects
///     • Incoming crosses  — other labels' leaders through this label's rect
///   If a better candidate exists, move there. Repeat until no improvement.
/// </summary>
public static class GreedyPlacer
{
    /// <summary>
    /// Maximum refinement sweeps after the initial greedy pass.
    /// Each sweep is O(n² × candidates); typically converges in 2–4 sweeps.
    /// </summary>
    public static int MaxRefinementSweeps { get; set; } = 8;

    /// <summary>
    /// Score penalty added when a candidate lands on the non-preferred side.
    /// Must be larger than the candidate-count range (21) but much smaller than
    /// a single unit of overlap area (1e6) so it never forces an overlap.
    /// </summary>
    public static double WrongSidePenalty { get; set; } = 200.0;

    public static GreedyResult Place(List<LabelState> labels)
    {
        foreach (LabelState ls in labels) ls.CurrentOffset = Vector2D.Zero;

        if (labels.Count == 0) return new GreedyResult();

        bool[] preferLeft = ComputePreferLeft(labels);

        LabelCandidate[][] allCandidates = new LabelCandidate[labels.Count][];
        for (int i = 0; i < labels.Count; i++)
            allCandidates[i] = CandidateGenerator.Generate(labels[i], preferLeft[i]);

        int[] order = ComputePriorityOrder(labels);

        double cellSize = SpatialGrid.RecommendCellSize(labels);
        var    grid     = new SpatialGrid(cellSize);
        var    seen     = new HashSet<int>();

        int unplaced = 0;

        // ── Initial greedy pass ──────────────────────────────────────────────
        foreach (int i in order)
        {
            LabelState       ls         = labels[i];
            LabelCandidate[] candidates = allCandidates[i];
            double ax = ls.Anchor.X, ay = ls.Anchor.Y;

            double bestScore    = double.MaxValue;
            int    bestCandiIdx = 0;

            for (int c = 0; c < candidates.Length; c++)
            {
                Vector2D offset = candidates[c].Offset;
                Rect2D   rect   = ls.GetBoundingRectAt(offset);

                double overlap = OverlapAgainstGrid(grid, i, rect, labels, seen);

                int crosses = 0;
                if (overlap < 1e-9)
                {
                    double lx = ax + offset.X, ly = ay + offset.Y;
                    if (Math.Abs(lx - ax) > 1e-9 || Math.Abs(ly - ay) > 1e-9)
                        crosses = CountLeaderCrossesGrid(grid, i, ax, ay, lx, ly, labels, seen);
                }

                double sideCost = (preferLeft[i]  && offset.X > 1e-9) ||
                                  (!preferLeft[i] && offset.X < -1e-9)
                                  ? WrongSidePenalty : 0.0;
                double score = overlap * 1e6 + crosses * 1000.0 + sideCost + c;
                if (score < bestScore) { bestScore = score; bestCandiIdx = c; }
                if (bestScore < 0.5) break;   // perfect candidate — stop early
            }

            ls.CurrentOffset = candidates[bestCandiIdx].Offset;
            if (bestScore >= 1e6 - 0.5) unplaced++;
            grid.Insert(i, ls);
        }

        // ── Refinement sweeps (full two-way leader check) ────────────────────
        int sweeps = Refine(labels, allCandidates, preferLeft);

        var (area, pairs, maxP) = EnergyDelta.OverlapDiagnostics(labels);
        return new GreedyResult
        {
            UnplacedCount       = unplaced,
            FinalOverlapArea    = area,
            FinalOverlapPairs   = pairs,
            FinalMaxPairOverlap = maxP,
            RefinementSweeps    = sweeps,
        };
    }

    // =========================================================================
    // Refinement
    // =========================================================================

    /// <summary>
    /// Iterative local-search refinement.  For each label, tries every candidate
    /// using the two-way leader score; moves to the best if it improves things.
    /// Returns the number of sweeps performed.
    /// </summary>
    private static int Refine(List<LabelState> labels, LabelCandidate[][] allCandidates, bool[] preferLeft)
    {
        int n = labels.Count;
        int sweeps = 0;

        for (int iter = 0; iter < MaxRefinementSweeps; iter++)
        {
            bool improved = false;

            for (int i = 0; i < n; i++)
            {
                LabelState       ls         = labels[i];
                LabelCandidate[] candidates = allCandidates[i];

                double currentScore = LabelScore(i, ls.CurrentOffset, candidates, labels, preferLeft[i]);

                double bestScore    = currentScore;
                int    bestCandiIdx = -1;

                for (int c = 0; c < candidates.Length; c++)
                {
                    // Skip if this is already the current position.
                    Vector2D off = candidates[c].Offset;
                    if (Math.Abs(off.X - ls.CurrentOffset.X) < 1e-9 &&
                        Math.Abs(off.Y - ls.CurrentOffset.Y) < 1e-9) continue;

                    double score = LabelScore(i, off, candidates, labels, preferLeft[i]);
                    if (score < bestScore - 1e-9)
                    {
                        bestScore    = score;
                        bestCandiIdx = c;
                    }
                }

                if (bestCandiIdx >= 0)
                {
                    ls.CurrentOffset = candidates[bestCandiIdx].Offset;
                    improved = true;
                }
            }

            sweeps++;
            if (!improved) break;
        }

        return sweeps;
    }

    /// <summary>
    /// Full two-way score for label <paramref name="i"/> at <paramref name="offset"/>:
    ///   overlap × 1e6 + (outgoing_crosses + incoming_crosses) × 1000 + preference
    /// where:
    ///   outgoing = this label's leader crosses other label rects
    ///   incoming = other labels' leaders cross this label's rect
    /// </summary>
    private static double LabelScore(
        int i, Vector2D offset, LabelCandidate[] candidates, List<LabelState> labels, bool preferLeft)
    {
        LabelState ls   = labels[i];
        double     ax   = ls.Anchor.X, ay = ls.Anchor.Y;
        double     lx   = ax + offset.X, ly = ay + offset.Y;
        Rect2D     rect = ls.GetBoundingRectAt(offset);

        double totalOverlap = 0.0;
        int    outgoing     = 0;
        int    incoming     = 0;
        bool   leaderNZ     = Math.Abs(lx - ax) > 1e-9 || Math.Abs(ly - ay) > 1e-9;

        for (int j = 0; j < labels.Count; j++)
        {
            if (j == i) continue;
            LabelState other = labels[j];
            Rect2D     orect = other.GetBoundingRect();

            // Rect overlap.
            totalOverlap += Rect2D.OverlapArea(rect, orect);

            if (totalOverlap < 1e-9)  // only count leader crosses when overlap-free
            {
                // Outgoing: label i's leader → other label's rect.
                if (leaderNZ && SegmentIntersectsRect(ax, ay, lx, ly, orect))
                    outgoing++;

                // Incoming: other label's leader → label i's rect.
                double jax = other.Anchor.X, jay = other.Anchor.Y;
                double jlx = jax + other.CurrentOffset.X;
                double jly = jay + other.CurrentOffset.Y;
                bool   jNZ = Math.Abs(jlx - jax) > 1e-9 || Math.Abs(jly - jay) > 1e-9;
                if (jNZ && SegmentIntersectsRect(jax, jay, jlx, jly, rect))
                    incoming++;
            }
        }

        // Preference score: index of this offset in the candidate list.
        double pref = candidates.Length;
        for (int c = 0; c < candidates.Length; c++)
        {
            if (Math.Abs(candidates[c].Offset.X - offset.X) < 1e-9 &&
                Math.Abs(candidates[c].Offset.Y - offset.Y) < 1e-9)
            { pref = c; break; }
        }

        double sideCost = (preferLeft  && offset.X > 1e-9) ||
                          (!preferLeft && offset.X < -1e-9)
                          ? WrongSidePenalty : 0.0;

        return totalOverlap * 1e6 + (outgoing + incoming) * 1000.0 + sideCost + pref;
    }

    // =========================================================================
    // Neighbour-side bias  (cluster-based)
    // =========================================================================

    /// <summary>
    /// Groups labels whose anchors are within <c>3 × medianLabelHeight</c> of each
    /// other (union-find), then decides preferLeft once per cluster by counting
    /// how many external-cluster anchors lie to the right vs left within a search
    /// radius of <c>6 × medianLabelHeight</c>.  All labels in a cluster share the
    /// same preferLeft flag so they consistently go to the same side.
    /// </summary>
    private static bool[] ComputePreferLeft(List<LabelState> labels)
    {
        int n = labels.Count;

        // Median label height → thresholds.
        double[] hs = new double[n];
        for (int i = 0; i < n; i++) hs[i] = labels[i].Height;
        Array.Sort(hs);
        double medH      = hs[n / 2];
        double clusterR2 = Math.Pow(3.0 * medH, 2);
        double searchR2  = Math.Pow(6.0 * medH, 2);

        // Union-Find.
        int[] par = new int[n];
        for (int i = 0; i < n; i++) par[i] = i;

        int Find(int x)
        {
            while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; }
            return x;
        }

        for (int i = 0; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            double dx = labels[i].Anchor.X - labels[j].Anchor.X;
            double dy = labels[i].Anchor.Y - labels[j].Anchor.Y;
            if (dx * dx + dy * dy <= clusterR2)
            {
                int ri = Find(i), rj = Find(j);
                if (ri != rj) par[rj] = ri;
            }
        }

        // Cluster centroid (sum of anchor positions, keyed by root).
        var sumX = new Dictionary<int, double>();
        var sumY = new Dictionary<int, double>();
        var cnt  = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            if (!cnt.ContainsKey(r)) { sumX[r] = 0; sumY[r] = 0; cnt[r] = 0; }
            sumX[r] += labels[i].Anchor.X;
            sumY[r] += labels[i].Anchor.Y;
            cnt[r]  += 1;
        }
        var cxMap = new Dictionary<int, double>();
        var cyMap = new Dictionary<int, double>();
        foreach (int r in cnt.Keys)
        {
            cxMap[r] = sumX[r] / cnt[r];
            cyMap[r] = sumY[r] / cnt[r];
        }

        // Vote: for each cluster root, count external labels to left / right.
        var leftVote  = new Dictionary<int, int>();
        var rightVote = new Dictionary<int, int>();
        foreach (int r in cnt.Keys) { leftVote[r] = 0; rightVote[r] = 0; }

        for (int j = 0; j < n; j++)
        {
            int rj = Find(j);
            foreach (int r in cnt.Keys)
            {
                if (r == rj) continue;
                double dx = labels[j].Anchor.X - cxMap[r];
                double dy = labels[j].Anchor.Y - cyMap[r];
                if (dx * dx + dy * dy <= searchR2)
                {
                    if      (dx > 0) rightVote[r]++;
                    else if (dx < 0) leftVote[r]++;
                }
            }
        }

        bool[] result = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int r = Find(i);
            result[i] = rightVote[r] > leftVote[r];
        }
        return result;
    }

    // =========================================================================
    // Priority ordering
    // =========================================================================

    private static int[] ComputePriorityOrder(List<LabelState> labels)
    {
        int      n          = labels.Count;
        double[] constraint = new double[n];

        for (int i = 0; i < n; i++)
        {
            LabelState a  = labels[i];
            double     r2 = Math.Pow(2.0 * Math.Max(a.Width, a.Height), 2);
            int        ct = 0;
            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                LabelState b = labels[j];
                double dx = a.Anchor.X - b.Anchor.X, dy = a.Anchor.Y - b.Anchor.Y;
                if (dx * dx + dy * dy <= r2) ct++;
            }
            constraint[i] = ct;
        }

        int[] order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => constraint[b].CompareTo(constraint[a]));
        return order;
    }

    // =========================================================================
    // Spatial-grid overlap (initial pass only)
    // =========================================================================

    private static double OverlapAgainstGrid(
        SpatialGrid grid, int selfIdx, Rect2D rect,
        List<LabelState> labels, HashSet<int> seen)
    {
        List<int> neighbors = grid.QueryNeighbors(selfIdx, rect);
        if (neighbors.Count == 0) return 0.0;

        seen.Clear();
        double total = 0.0;
        foreach (int j in neighbors)
        {
            if (!seen.Add(j)) continue;
            total += Rect2D.OverlapArea(rect, labels[j].GetBoundingRect());
        }
        return total;
    }

    private static int CountLeaderCrossesGrid(
        SpatialGrid grid, int selfIdx,
        double ax, double ay, double lx, double ly,
        List<LabelState> labels, HashSet<int> seen)
    {
        double minX = Math.Min(ax, lx) - 1e-6, maxX = Math.Max(ax, lx) + 1e-6;
        double minY = Math.Min(ay, ly) - 1e-6, maxY = Math.Max(ay, ly) + 1e-6;
        Rect2D bounds = new Rect2D(minX, minY, maxX, maxY);

        List<int> neighbors = grid.QueryNeighbors(selfIdx, bounds);
        if (neighbors.Count == 0) return 0;

        seen.Clear();
        int count = 0;
        foreach (int j in neighbors)
        {
            if (!seen.Add(j)) continue;
            if (SegmentIntersectsRect(ax, ay, lx, ly, labels[j].GetBoundingRect()))
                count++;
        }
        return count;
    }

    // =========================================================================
    // Geometry helpers
    // =========================================================================

    private static bool SegmentIntersectsRect(
        double x0, double y0, double x1, double y1, Rect2D r)
    {
        double dx = x1 - x0, dy = y1 - y0;
        double t0 = 0.0, t1 = 1.0;
        return Clip(-dx, x0 - r.Left,   ref t0, ref t1)
            && Clip( dx, r.Right - x0,  ref t0, ref t1)
            && Clip(-dy, y0 - r.Bottom, ref t0, ref t1)
            && Clip( dy, r.Top - y0,    ref t0, ref t1);
    }

    private static bool Clip(double p, double q, ref double t0, ref double t1)
    {
        if (Math.Abs(p) < 1e-12) return q >= 0.0;
        double t = q / p;
        if (p < 0.0) { if (t > t1) return false; if (t > t0) t0 = t; }
        else         { if (t < t0) return false; if (t < t1) t1 = t; }
        return true;
    }
}
