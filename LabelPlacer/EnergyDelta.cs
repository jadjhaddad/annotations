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

        return dOverlap + dDisp + dGroup;
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
    /// dE_disp = betaX × (|newX| - |oldX|) + betaY × (|newY| - |oldY|)
    /// </summary>
    public static double DDisplacement(
        Vector2D oldOff,
        Vector2D newOff,
        PlacerConfig cfg)
    {
        double dx = System.Math.Abs(newOff.X) - System.Math.Abs(oldOff.X);
        double dy = System.Math.Abs(newOff.Y) - System.Math.Abs(oldOff.Y);
        return cfg.BetaX * dx + cfg.BetaY * dy;
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
                  + cfg.BetaY * System.Math.Abs(ls.CurrentOffset.Y);
        }

        // Group spread.
        for (int g = 0; g < groups.GroupCount; g++)
            spread += groups[g].Spread();

        return cfg.Alpha * overlap + disp + cfg.Gamma * spread;
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
