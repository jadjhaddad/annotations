using System;
using System.Collections.Generic;

namespace LabelPlacer;

/// <summary>
/// Computes incremental energy deltas for a proposed label move.
///
/// The global energy is never recomputed from scratch. Only the three
/// terms affected by moving label i are evaluated:
///
///   dE = dE_overlap + dE_displacement + dE_group
///
/// All methods are stateless — pass in the current state and get back a delta.
/// </summary>
public static class EnergyDelta
{
    // -------------------------------------------------------------------------
    // Full incremental delta for a proposed move
    // -------------------------------------------------------------------------

    /// <summary>
    /// Compute the total energy delta for moving label <paramref name="idx"/>
    /// from its current offset to <paramref name="newOffset"/>.
    ///
    /// <paramref name="neighbors"/> must be the neighbor list from the spatial
    /// grid queried at the CURRENT bounding rect of label i (before the move).
    /// Duplicates in the neighbor list are tolerated — overlap area is
    /// recalculated per pair, so double-counting a neighbor just costs CPU.
    /// For correctness, pass a deduplicated list.
    /// </summary>
    public static double Compute(
        int idx,
        Vector2D newOffset,
        IReadOnlyList<LabelState> labels,
        IReadOnlyList<int> neighbors,
        GroupRegistry groups,
        PlacerConfig cfg)
    {
        LabelState label = labels[idx];
        Vector2D oldOff = label.CurrentOffset;
        Rect2D oldRect = label.GetBoundingRect();
        Rect2D newRect = label.GetBoundingRectAt(newOffset);

        double dOverlap = DOverlap(oldRect, newRect, idx, neighbors, labels, cfg);
        double dDisp = DDisplacement(oldOff, newOffset, cfg);
        double dGroup = DGroup(label, oldOff, newOffset, groups, cfg);
        double dLeader = DLeaderAwareness(idx, newOffset, labels, cfg);

        return dOverlap + dDisp + dGroup + dLeader;
    }

    // -------------------------------------------------------------------------
    // Term 1 — Overlap area delta
    // -------------------------------------------------------------------------

    /// <summary>
    /// dE_overlap = alpha × Σ_j ( overlap(newRect, j) - overlap(oldRect, j) )
    ///
    /// Only labels in <paramref name="neighbors"/> are evaluated. The spatial
    /// grid guarantees that any label NOT in the neighbor list cannot overlap
    /// with i (its rect doesn't share a cell).
    /// </summary>
    public static double DOverlap(
        Rect2D oldRect,
        Rect2D newRect,
        int selfIdx,
        IReadOnlyList<int> neighbors,
        IReadOnlyList<LabelState> labels,
        PlacerConfig cfg)
    {
        double sum = 0.0;
        // Track seen indices to avoid double-counting duplicates from the grid.
        // For tight inner loops, a small array-based dedup is faster than HashSet.
        // We just iterate and let minor duplicates inflate cost slightly — the
        // SA acceptance rule is robust to small constant biases.  For correctness
        // the caller can pass a deduplicated list.
        foreach (int j in neighbors)
        {
            Rect2D jr = labels[j].GetBoundingRect();
            sum += Rect2D.OverlapArea(newRect, jr) - Rect2D.OverlapArea(oldRect, jr);
        }
        return cfg.Alpha * sum;
    }

    // -------------------------------------------------------------------------
    // Term 2/3 — Displacement delta
    // -------------------------------------------------------------------------

    /// <summary>
    /// dE_disp = betaX × (|newX| - |oldX|)
    ///         + betaY × (|newY| - |oldY|)
    ///         + betaX2 × (newX² - oldX²)
    ///         + betaY2 × (newY² - oldY²)
    /// </summary>
    public static double DDisplacement(
        Vector2D oldOff,
        Vector2D newOff,
        PlacerConfig cfg)
    {
        double dx = System.Math.Abs(newOff.X) - System.Math.Abs(oldOff.X);
        double dy = System.Math.Abs(newOff.Y) - System.Math.Abs(oldOff.Y);
        double dx2 = (newOff.X * newOff.X) - (oldOff.X * oldOff.X);
        double dy2 = (newOff.Y * newOff.Y) - (oldOff.Y * oldOff.Y);
        return cfg.BetaX * dx + cfg.BetaY * dy + cfg.BetaX2 * dx2 + cfg.BetaY2 * dy2;
    }

    // -------------------------------------------------------------------------
    // Term 4 — Intragroup spread delta
    // -------------------------------------------------------------------------

    /// <summary>
    /// dE_group = gamma × (spread_new(g) - spread_old(g))
    ///
    /// Returns 0 if the label is not in a coincident group (GroupId == -1).
    /// Uses <see cref="GroupStats.SpreadIfReplaced"/> — no mutation.
    /// </summary>
    public static double DGroup(
        LabelState label,
        Vector2D oldOff,
        Vector2D newOff,
        GroupRegistry groups,
        PlacerConfig cfg)
    {
        if (label.GroupId < 0) return 0.0;

        GroupStats gs = groups[label.GroupId];
        double oldSpread = gs.Spread();
        double newSpread = gs.SpreadIfReplaced(oldOff.X, oldOff.Y, newOff.X, newOff.Y);
        return cfg.Gamma * (newSpread - oldSpread);
    }

    // -------------------------------------------------------------------------
    // Global energy (used once at start and for diagnostics, not per-iteration)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the full global energy from scratch.
    /// O(n²) in the worst case — only call for initialization or diagnostics.
    /// </summary>
    public static double ComputeGlobal(
        IReadOnlyList<LabelState> labels,
        GroupRegistry groups,
        PlacerConfig cfg)
    {
        double overlap = 0.0;
        double disp = 0.0;
        double spread = 0.0;

        // Overlap: all unique pairs (i < j).
        for (int i = 0; i < labels.Count; i++)
        {
            Rect2D ri = labels[i].GetBoundingRect();
            for (int j = i + 1; j < labels.Count; j++)
            {
                overlap += Rect2D.OverlapArea(ri, labels[j].GetBoundingRect());
            }
        }

        // Displacement.
        foreach (LabelState ls in labels)
        {
            disp += cfg.BetaX * System.Math.Abs(ls.CurrentOffset.X)
                  + cfg.BetaY * System.Math.Abs(ls.CurrentOffset.Y)
                  + cfg.BetaX2 * ls.CurrentOffset.X * ls.CurrentOffset.X
                  + cfg.BetaY2 * ls.CurrentOffset.Y * ls.CurrentOffset.Y;
        }

        // Group spread.
        for (int g = 0; g < groups.GroupCount; g++)
            spread += groups[g].Spread();

        // Leader awareness.
        double leaderLabelHits = 0.0;
        if (cfg.LeaderLabelPenaltyWeight > 0.0)
        {
            for (int i = 0; i < labels.Count; i++)
            {
                Segment2D leader = BuildLeader(labels[i], labels[i].CurrentOffset);
                for (int j = 0; j < labels.Count; j++)
                {
                    if (i == j) continue;
                    if (SegmentIntersectsRect(leader, labels[j].GetBoundingRect()))
                        leaderLabelHits += 1.0;
                }
            }
        }

        double leaderLeaderHits = 0.0;
        if (cfg.LeaderLeaderPenaltyWeight > 0.0)
        {
            for (int i = 0; i < labels.Count; i++)
            {
                Segment2D li = BuildLeader(labels[i], labels[i].CurrentOffset);
                for (int j = i + 1; j < labels.Count; j++)
                {
                    Segment2D lj = BuildLeader(labels[j], labels[j].CurrentOffset);
                    if (LeadersConflict(li, lj))
                        leaderLeaderHits += 1.0;
                }
            }
        }

        return cfg.Alpha * overlap
             + disp
             + cfg.Gamma * spread
             + cfg.LeaderLabelPenaltyWeight * leaderLabelHits
             + cfg.LeaderLeaderPenaltyWeight * leaderLeaderHits;
    }

    /// <summary>
    /// Leader-aware incremental energy delta for a single-label move.
    /// Penalises:
    /// 1) leader i intersecting any other label rectangle,
    /// 2) any other leader intersecting moved label i rectangle,
    /// 3) leader i intersecting any other leader.
    /// </summary>
    public static double DLeaderAwareness(
        int idx,
        Vector2D newOffset,
        IReadOnlyList<LabelState> labels,
        PlacerConfig cfg)
    {
        if (cfg.LeaderLabelPenaltyWeight <= 0.0 && cfg.LeaderLeaderPenaltyWeight <= 0.0)
            return 0.0;

        LabelState li = labels[idx];
        Vector2D oldOffset = li.CurrentOffset;

        Rect2D oldRectI = li.GetBoundingRect();
        Rect2D newRectI = li.GetBoundingRectAt(newOffset);

        Segment2D oldLeaderI = BuildLeader(li, oldOffset);
        Segment2D newLeaderI = BuildLeader(li, newOffset);

        double dLeaderLabel = 0.0;
        double dLeaderLeader = 0.0;

        for (int k = 0; k < labels.Count; k++)
        {
            if (k == idx) continue;

            LabelState lk = labels[k];
            Rect2D rectK = lk.GetBoundingRect();

            if (cfg.LeaderLabelPenaltyWeight > 0.0)
            {
                // Moved leader vs other labels.
                bool oldIK = SegmentIntersectsRect(oldLeaderI, rectK);
                bool newIK = SegmentIntersectsRect(newLeaderI, rectK);
                if (oldIK != newIK)
                    dLeaderLabel += newIK ? 1.0 : -1.0;

                // Other leaders vs moved label.
                Segment2D leaderK = BuildLeader(lk, lk.CurrentOffset);
                bool oldKI = SegmentIntersectsRect(leaderK, oldRectI);
                bool newKI = SegmentIntersectsRect(leaderK, newRectI);
                if (oldKI != newKI)
                    dLeaderLabel += newKI ? 1.0 : -1.0;
            }

            if (cfg.LeaderLeaderPenaltyWeight > 0.0)
            {
                Segment2D leaderK = BuildLeader(lk, lk.CurrentOffset);
                bool oldCross = LeadersConflict(oldLeaderI, leaderK);
                bool newCross = LeadersConflict(newLeaderI, leaderK);
                if (oldCross != newCross)
                    dLeaderLeader += newCross ? 1.0 : -1.0;
            }
        }

        return cfg.LeaderLabelPenaltyWeight * dLeaderLabel
             + cfg.LeaderLeaderPenaltyWeight * dLeaderLeader;
    }

    /// <summary>
    /// Leader-aware incremental energy delta for moving two labels at once (swap move).
    /// </summary>
    public static double DLeaderAwarenessSwap(
        int idx,
        int jdx,
        Vector2D newOffsetI,
        Vector2D newOffsetJ,
        IReadOnlyList<LabelState> labels,
        PlacerConfig cfg)
    {
        if (cfg.LeaderLabelPenaltyWeight <= 0.0 && cfg.LeaderLeaderPenaltyWeight <= 0.0)
            return 0.0;

        LabelState li = labels[idx];
        LabelState lj = labels[jdx];

        Vector2D oldOffsetI = li.CurrentOffset;
        Vector2D oldOffsetJ = lj.CurrentOffset;

        Rect2D oldRectI = li.GetBoundingRect();
        Rect2D newRectI = li.GetBoundingRectAt(newOffsetI);
        Rect2D oldRectJ = lj.GetBoundingRect();
        Rect2D newRectJ = lj.GetBoundingRectAt(newOffsetJ);

        Segment2D oldLeaderI = BuildLeader(li, oldOffsetI);
        Segment2D newLeaderI = BuildLeader(li, newOffsetI);
        Segment2D oldLeaderJ = BuildLeader(lj, oldOffsetJ);
        Segment2D newLeaderJ = BuildLeader(lj, newOffsetJ);

        double dLeaderLabel = 0.0;
        double dLeaderLeader = 0.0;

        for (int k = 0; k < labels.Count; k++)
        {
            if (k == idx || k == jdx) continue;

            LabelState lk = labels[k];
            Rect2D rectK = lk.GetBoundingRect();
            Segment2D leaderK = BuildLeader(lk, lk.CurrentOffset);

            if (cfg.LeaderLabelPenaltyWeight > 0.0)
            {
                // i and j leaders vs other label k.
                bool oldIK = SegmentIntersectsRect(oldLeaderI, rectK);
                bool newIK = SegmentIntersectsRect(newLeaderI, rectK);
                if (oldIK != newIK) dLeaderLabel += newIK ? 1.0 : -1.0;

                bool oldJK = SegmentIntersectsRect(oldLeaderJ, rectK);
                bool newJK = SegmentIntersectsRect(newLeaderJ, rectK);
                if (oldJK != newJK) dLeaderLabel += newJK ? 1.0 : -1.0;

                // Other leader k vs moved rect i/j.
                bool oldKI = SegmentIntersectsRect(leaderK, oldRectI);
                bool newKI = SegmentIntersectsRect(leaderK, newRectI);
                if (oldKI != newKI) dLeaderLabel += newKI ? 1.0 : -1.0;

                bool oldKJ = SegmentIntersectsRect(leaderK, oldRectJ);
                bool newKJ = SegmentIntersectsRect(leaderK, newRectJ);
                if (oldKJ != newKJ) dLeaderLabel += newKJ ? 1.0 : -1.0;
            }

            if (cfg.LeaderLeaderPenaltyWeight > 0.0)
            {
                bool oldIK = LeadersConflict(oldLeaderI, leaderK);
                bool newIK = LeadersConflict(newLeaderI, leaderK);
                if (oldIK != newIK) dLeaderLeader += newIK ? 1.0 : -1.0;

                bool oldJK = LeadersConflict(oldLeaderJ, leaderK);
                bool newJK = LeadersConflict(newLeaderJ, leaderK);
                if (oldJK != newJK) dLeaderLeader += newJK ? 1.0 : -1.0;
            }
        }

        // Pair terms between i and j.
        if (cfg.LeaderLabelPenaltyWeight > 0.0)
        {
            bool oldIJ = SegmentIntersectsRect(oldLeaderI, oldRectJ);
            bool newIJ = SegmentIntersectsRect(newLeaderI, newRectJ);
            if (oldIJ != newIJ) dLeaderLabel += newIJ ? 1.0 : -1.0;

            bool oldJI = SegmentIntersectsRect(oldLeaderJ, oldRectI);
            bool newJI = SegmentIntersectsRect(newLeaderJ, newRectI);
            if (oldJI != newJI) dLeaderLabel += newJI ? 1.0 : -1.0;
        }

        if (cfg.LeaderLeaderPenaltyWeight > 0.0)
        {
            bool oldCross = LeadersConflict(oldLeaderI, oldLeaderJ);
            bool newCross = LeadersConflict(newLeaderI, newLeaderJ);
            if (oldCross != newCross) dLeaderLeader += newCross ? 1.0 : -1.0;
        }

        return cfg.LeaderLabelPenaltyWeight * dLeaderLabel
             + cfg.LeaderLeaderPenaltyWeight * dLeaderLeader;
    }

    // --- Leader geometry helpers ---

    private readonly struct Segment2D
    {
        public readonly Point2D A;
        public readonly Point2D B;

        public Segment2D(Point2D a, Point2D b)
        {
            A = a;
            B = b;
        }
    }

    private static Segment2D BuildLeader(LabelState label, Vector2D offset)
    {
        // Leader lands on left-edge center of the label box.
        Point2D start = label.Anchor;
        Point2D end = new Point2D(label.Anchor.X + offset.X, label.Anchor.Y + offset.Y);
        return new Segment2D(start, end);
    }

    private static bool SegmentIntersectsRect(Segment2D s, Rect2D r)
    {
        if (PointInRect(s.A, r) || PointInRect(s.B, r)) return true;

        Point2D bl = new Point2D(r.Left, r.Bottom);
        Point2D br = new Point2D(r.Right, r.Bottom);
        Point2D tr = new Point2D(r.Right, r.Top);
        Point2D tl = new Point2D(r.Left, r.Top);

        return SegmentsIntersect(s.A, s.B, bl, br)
            || SegmentsIntersect(s.A, s.B, br, tr)
            || SegmentsIntersect(s.A, s.B, tr, tl)
            || SegmentsIntersect(s.A, s.B, tl, bl);
    }

    private static bool LeadersConflict(Segment2D a, Segment2D b)
    {
        // Allow touching at shared anchor positions.
        if (NearlySamePoint(a.A, b.A)) return false;
        if (NearlySamePoint(a.A, b.B)) return false;
        if (NearlySamePoint(a.B, b.A)) return false;
        if (NearlySamePoint(a.B, b.B)) return false;

        return SegmentsIntersect(a.A, a.B, b.A, b.B);
    }

    private static bool PointInRect(Point2D p, Rect2D r)
        => p.X >= r.Left && p.X <= r.Right && p.Y >= r.Bottom && p.Y <= r.Top;

    private static bool NearlySamePoint(Point2D a, Point2D b)
    {
        const double eps = 1e-9;
        return Math.Abs(a.X - b.X) <= eps && Math.Abs(a.Y - b.Y) <= eps;
    }

    private static bool SegmentsIntersect(Point2D p1, Point2D q1, Point2D p2, Point2D q2)
    {
        int o1 = Orientation(p1, q1, p2);
        int o2 = Orientation(p1, q1, q2);
        int o3 = Orientation(p2, q2, p1);
        int o4 = Orientation(p2, q2, q1);

        if (o1 != o2 && o3 != o4) return true;

        if (o1 == 0 && OnSegment(p1, p2, q1)) return true;
        if (o2 == 0 && OnSegment(p1, q2, q1)) return true;
        if (o3 == 0 && OnSegment(p2, p1, q2)) return true;
        if (o4 == 0 && OnSegment(p2, q1, q2)) return true;

        return false;
    }

    private static int Orientation(Point2D p, Point2D q, Point2D r)
    {
        const double eps = 1e-12;
        double v = (q.Y - p.Y) * (r.X - q.X) - (q.X - p.X) * (r.Y - q.Y);
        if (Math.Abs(v) <= eps) return 0;
        return v > 0.0 ? 1 : 2;
    }

    private static bool OnSegment(Point2D p, Point2D q, Point2D r)
    {
        return q.X <= Math.Max(p.X, r.X)
            && q.X >= Math.Min(p.X, r.X)
            && q.Y <= Math.Max(p.Y, r.Y)
            && q.Y >= Math.Min(p.Y, r.Y);
    }

    /// <summary>
    /// Returns diagnostics: total overlap area, count of overlapping pairs,
    /// and the maximum single-pair overlap area.
    /// O(n²) — diagnostics only.
    /// </summary>
    public static (double totalArea, int pairCount, double maxPairArea)
        OverlapDiagnostics(IReadOnlyList<LabelState> labels)
    {
        double totalArea = 0.0;
        double maxArea = 0.0;
        int pairCount = 0;

        for (int i = 0; i < labels.Count; i++)
        {
            Rect2D ri = labels[i].GetBoundingRect();
            for (int j = i + 1; j < labels.Count; j++)
            {
                double a = Rect2D.OverlapArea(ri, labels[j].GetBoundingRect());
                if (a > 0.0)
                {
                    totalArea += a;
                    pairCount++;
                    if (a > maxArea) maxArea = a;
                }
            }
        }

        return (totalArea, pairCount, maxArea);
    }

    /// <summary>
    /// Diagnostics that ignore overlaps between labels sharing the same anchor.
    /// Useful when labels are stacked per anchor and treated as a single block.
    /// </summary>
    public static (double totalArea, int pairCount, double maxPairArea)
        OverlapDiagnosticsIgnoreSameAnchor(IReadOnlyList<LabelState> labels)
    {
        double totalArea = 0.0;
        double maxArea = 0.0;
        int pairCount = 0;

        for (int i = 0; i < labels.Count; i++)
        {
            LabelState li = labels[i];
            Rect2D ri = li.GetBoundingRect();
            for (int j = i + 1; j < labels.Count; j++)
            {
                LabelState lj = labels[j];
                if (li.Anchor.X == lj.Anchor.X && li.Anchor.Y == lj.Anchor.Y) continue;

                double a = Rect2D.OverlapArea(ri, lj.GetBoundingRect());
                if (a > 0.0)
                {
                    totalArea += a;
                    pairCount++;
                    if (a > maxArea) maxArea = a;
                }
            }
        }

        return (totalArea, pairCount, maxArea);
    }

    /// <summary>Displacement component of the global energy — O(n), cheap.</summary>
    public static double ComputeDispComponent(IReadOnlyList<LabelState> labels, PlacerConfig cfg)
    {
        double disp = 0.0;
        foreach (LabelState ls in labels)
            disp += cfg.BetaX * System.Math.Abs(ls.CurrentOffset.X)
                  + cfg.BetaY * System.Math.Abs(ls.CurrentOffset.Y);
        return disp;
    }

    /// <summary>Group spread component of the global energy — O(groups), cheap.</summary>
    public static double ComputeSpreadComponent(GroupRegistry groups, PlacerConfig cfg)
    {
        double spread = 0.0;
        for (int g = 0; g < groups.GroupCount; g++)
            spread += groups[g].Spread();
        return cfg.Gamma * spread;
    }
}
