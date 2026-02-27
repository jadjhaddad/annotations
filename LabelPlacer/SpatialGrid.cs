using System;
using System.Collections.Generic;

namespace LabelPlacer;

/// <summary>
/// Uniform-grid spatial index over label bounding rectangles.
///
/// Cell size is set once at construction (typically max(labelWidth, labelHeight) across
/// all labels, or a caller-supplied override).  Each label is registered in every cell
/// its bounding rectangle overlaps, so a neighbor query for label i returns every label
/// whose bounding rectangle touches the same cells — i.e. every candidate that could
/// possibly overlap with i.
///
/// Insert / Remove / Update are O(cells touched) ≈ O(1) for typical label sizes.
/// QueryNeighbors is O(cells touched + candidates returned).
/// </summary>
public sealed class SpatialGrid
{
    // -------------------------------------------------------------------------
    // Grid geometry
    // -------------------------------------------------------------------------

    private readonly double _cellSize;
    private readonly double _invCell;   // 1 / _cellSize, cached

    // Dictionary<cellKey, set of label indices>
    // cellKey = encoded (col, row) pair
    private readonly Dictionary<long, HashSet<int>> _cells = new();

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    /// <param name="cellSize">
    /// Side length of each grid cell in drawing units.
    /// Recommended: use <see cref="RecommendCellSize"/> on the full label list.
    /// </param>
    public SpatialGrid(double cellSize)
    {
        if (cellSize <= 0) throw new ArgumentOutOfRangeException(nameof(cellSize));
        _cellSize = cellSize;
        _invCell = 1.0 / cellSize;
    }

    /// <summary>
    /// Recommended cell size: max(width, height) across all labels, with a floor
    /// so degenerate zero-size labels don't produce an infinitely fine grid.
    /// </summary>
    public static double RecommendCellSize(IReadOnlyList<LabelState> labels, double floor = 1e-6)
    {
        double best = floor;
        foreach (LabelState ls in labels)
        {
            double m = Math.Max(ls.Width, ls.Height);
            if (m > best) best = m;
        }
        return best;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>Insert label <paramref name="idx"/> at its current bounding rect.</summary>
    public void Insert(int idx, LabelState label)
        => AddToRect(idx, label.GetBoundingRect());

    /// <summary>Remove label <paramref name="idx"/> from its current bounding rect.</summary>
    public void Remove(int idx, LabelState label)
        => RemoveFromRect(idx, label.GetBoundingRect());

    /// <summary>
    /// Move label <paramref name="idx"/> from <paramref name="oldRect"/> to
    /// <paramref name="newRect"/> without touching unaffected cells.
    /// Call this after committing a move; pass the rect before and after.
    /// </summary>
    public void Update(int idx, Rect2D oldRect, Rect2D newRect)
    {
        // Fast path: same cell coverage → nothing to do.
        (int c0o, int r0o, int c1o, int r1o) = CellRange(oldRect);
        (int c0n, int r0n, int c1n, int r1n) = CellRange(newRect);

        if (c0o == c0n && r0o == r0n && c1o == c1n && r1o == r1n)
            return;

        RemoveFromRect(idx, oldRect);
        AddToRect(idx, newRect);
    }

    /// <summary>
    /// Returns all label indices whose registered cell coverage overlaps the
    /// cells touched by <paramref name="rect"/>, excluding <paramref name="selfIdx"/>.
    ///
    /// The result may contain duplicates; the caller (EnergyDelta) deduplicates
    /// via a HashSet when needed, or tolerates duplicates if the loop guards
    /// against double-counting.
    /// </summary>
    public List<int> QueryNeighbors(int selfIdx, Rect2D rect)
    {
        (int c0, int r0, int c1, int r1) = CellRange(rect);

        // Estimate initial capacity to avoid repeated resizing.
        int estCells = (c1 - c0 + 1) * (r1 - r0 + 1);
        var result = new List<int>(estCells * 4);

        for (int c = c0; c <= c1; c++)
            for (int r = r0; r <= r1; r++)
            {
                long key = EncodeCell(c, r);
                if (_cells.TryGetValue(key, out HashSet<int>? bucket))
                {
                    foreach (int idx in bucket)
                        if (idx != selfIdx) result.Add(idx);
                }
            }

        return result;
    }

    /// <summary>
    /// Convenience overload: query neighbors using label's current bounding rect.
    /// </summary>
    public List<int> QueryNeighbors(int selfIdx, LabelState label)
        => QueryNeighbors(selfIdx, label.GetBoundingRect());

    /// <summary>Total number of (cell, label) registrations — useful for diagnostics.</summary>
    public int RegistrationCount
    {
        get
        {
            int total = 0;
            foreach (var bucket in _cells.Values) total += bucket.Count;
            return total;
        }
    }

    /// <summary>Number of non-empty cells.</summary>
    public int OccupiedCellCount => _cells.Count;

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private void AddToRect(int idx, Rect2D rect)
    {
        (int c0, int r0, int c1, int r1) = CellRange(rect);
        for (int c = c0; c <= c1; c++)
            for (int r = r0; r <= r1; r++)
            {
                long key = EncodeCell(c, r);
                if (!_cells.TryGetValue(key, out HashSet<int>? bucket))
                {
                    bucket = new HashSet<int>();
                    _cells[key] = bucket;
                }
                bucket.Add(idx);
            }
    }

    private void RemoveFromRect(int idx, Rect2D rect)
    {
        (int c0, int r0, int c1, int r1) = CellRange(rect);
        for (int c = c0; c <= c1; c++)
            for (int r = r0; r <= r1; r++)
            {
                long key = EncodeCell(c, r);
                if (_cells.TryGetValue(key, out HashSet<int>? bucket))
                {
                    bucket.Remove(idx);
                    if (bucket.Count == 0) _cells.Remove(key);
                }
            }
    }

    /// <summary>
    /// Returns the inclusive cell column/row range that a rectangle covers.
    /// </summary>
    private (int c0, int r0, int c1, int r1) CellRange(Rect2D rect)
    {
        int c0 = (int)Math.Floor(rect.Left * _invCell);
        int r0 = (int)Math.Floor(rect.Bottom * _invCell);
        int c1 = (int)Math.Floor(rect.Right * _invCell);
        int r1 = (int)Math.Floor(rect.Top * _invCell);
        return (c0, r0, c1, r1);
    }

    /// <summary>
    /// Encodes a (col, row) pair into a single long key.
    /// Supports col/row values in the range [-1M, +1M], well beyond any real drawing.
    /// </summary>
    private static long EncodeCell(int col, int row)
    {
        // Shift row into the upper 32 bits.
        return ((long)(uint)col) | ((long)(uint)row << 32);
    }
}
