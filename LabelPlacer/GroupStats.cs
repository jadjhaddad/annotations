using System;
using System.Collections.Generic;

namespace LabelPlacer;

/// <summary>
/// O(1) incremental variance tracker for one coincident group.
///
/// Maintains running sums so that the spread (varX + varY) of label offsets
/// within the group can be updated in O(1) when a single label moves,
/// without iterating over all group members.
///
/// Spread formula:
///   varX   = (sumX2 / n) - (sumX / n)²
///   varY   = (sumY2 / n) - (sumY / n)²
///   spread = varX + varY
/// </summary>
public sealed class GroupStats
{
    public int N { get; private set; }
    public double SumX { get; private set; }
    public double SumY { get; private set; }
    public double SumX2 { get; private set; }
    public double SumY2 { get; private set; }

    // -------------------------------------------------------------------------
    // Mutation
    // -------------------------------------------------------------------------

    /// <summary>Add a label offset to the running stats (call during initialization).</summary>
    public void Add(double x, double y)
    {
        N++;
        SumX += x;
        SumY += y;
        SumX2 += x * x;
        SumY2 += y * y;
    }

    /// <summary>
    /// Update running stats when label i changes offset from
    /// <paramref name="oldX"/>,<paramref name="oldY"/> to
    /// <paramref name="newX"/>,<paramref name="newY"/>.
    /// O(1) — no iteration over group members.
    /// </summary>
    public void Replace(double oldX, double oldY, double newX, double newY)
    {
        SumX += newX - oldX;
        SumY += newY - oldY;
        SumX2 += newX * newX - oldX * oldX;
        SumY2 += newY * newY - oldY * oldY;
    }

    // -------------------------------------------------------------------------
    // Query
    // -------------------------------------------------------------------------

    /// <summary>
    /// Current spread = varX + varY.
    /// Returns 0 for groups with 0 or 1 member (no spread possible).
    /// </summary>
    public double Spread()
    {
        if (N <= 1) return 0.0;
        double meanX = SumX / N;
        double meanY = SumY / N;
        double varX = SumX2 / N - meanX * meanX;
        double varY = SumY2 / N - meanY * meanY;
        // Clamp tiny negatives from floating-point cancellation.
        return Math.Max(0.0, varX) + Math.Max(0.0, varY);
    }

    /// <summary>
    /// Computes the hypothetical spread if one member were replaced —
    /// without mutating state. Used by EnergyDelta to compute dE_group.
    /// </summary>
    public double SpreadIfReplaced(double oldX, double oldY, double newX, double newY)
    {
        if (N <= 1) return 0.0;
        double sX = SumX + (newX - oldX);
        double sY = SumY + (newY - oldY);
        double sX2 = SumX2 + (newX * newX - oldX * oldX);
        double sY2 = SumY2 + (newY * newY - oldY * oldY);
        double meanX = sX / N;
        double meanY = sY / N;
        double varX = sX2 / N - meanX * meanX;
        double varY = sY2 / N - meanY * meanY;
        return Math.Max(0.0, varX) + Math.Max(0.0, varY);
    }
}

/// <summary>
/// Registry of all coincident groups, indexed by GroupId.
///
/// Also handles:
///  - Computing coincident groups from a label list (scale-aware tolerance)
///  - Initializing running stats from initial offsets (always zero at cold start)
/// </summary>
public sealed class GroupRegistry
{
    private readonly GroupStats[] _groups;
    private readonly List<int>[] _members;  // groupId → label indices

    public int GroupCount => _groups.Length;

    public GroupStats this[int groupId] => _groups[groupId];
    public IReadOnlyList<int> GetMembers(int gid) => _members[gid];

    private GroupRegistry(GroupStats[] groups, List<int>[] members)
    {
        _groups = groups;
        _members = members;
    }

    // -------------------------------------------------------------------------
    // Factory: assign group IDs and build registry in one pass
    // -------------------------------------------------------------------------

    /// <summary>
    /// Assigns <see cref="LabelState.GroupId"/> to every label and returns a
    /// populated <see cref="GroupRegistry"/>.
    ///
    /// Two labels are coincident when their anchor XY are within
    ///   tolerance = config.CoincidenceFactor × medianLabelHeight
    ///
    /// Labels that are the sole occupant of their bucket receive GroupId = -1
    /// and are not tracked in the registry (no spread cost possible).
    ///
    /// Call this after cold-start reset (all offsets are zero) so that the
    /// initial running stats are trivially zero.
    /// </summary>
    public static GroupRegistry Build(IReadOnlyList<LabelState> labels, PlacerConfig cfg)
    {
        double tol = ComputeTolerance(labels, cfg);

        // Group anchors by rounded bucket key.
        var buckets = new Dictionary<long, List<int>>();
        for (int i = 0; i < labels.Count; i++)
        {
            LabelState ls = labels[i];
            // Avoid division by zero if tolerance is degenerate.
            long kx = tol > 0 ? (long)Math.Round(ls.Anchor.X / tol) : (long)ls.Anchor.X;
            long ky = tol > 0 ? (long)Math.Round(ls.Anchor.Y / tol) : (long)ls.Anchor.Y;
            // Pack two 32-bit values into one long key (same trick as SpatialGrid).
            long key = ((long)(uint)kx) | ((long)(uint)ky << 32);
            if (!buckets.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                buckets[key] = list;
            }
            list.Add(i);
        }

        // Assign group IDs only to buckets with 2+ members.
        int nextGroupId = 0;
        var groupStats = new List<GroupStats>();
        var groupMembers = new List<List<int>>();

        foreach (var kvp in buckets)
        {
            List<int> members = kvp.Value;
            if (members.Count < 2)
            {
                // Singleton — no group.
                labels[members[0]].GroupId = -1;
                continue;
            }

            int gid = nextGroupId++;
            var stats = new GroupStats();

            foreach (int idx in members)
            {
                labels[idx].GroupId = gid;
                // At cold start offsets are zero — stats are all zero.
                stats.Add(labels[idx].CurrentOffset.X, labels[idx].CurrentOffset.Y);
            }

            groupStats.Add(stats);
            groupMembers.Add(new List<int>(members));
        }

        return new GroupRegistry(groupStats.ToArray(), groupMembers.ToArray());
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static double ComputeTolerance(IReadOnlyList<LabelState> labels, PlacerConfig cfg)
    {
        if (labels.Count == 0) return 1e-6;

        // Collect heights and find median.
        var heights = new double[labels.Count];
        for (int i = 0; i < labels.Count; i++) heights[i] = labels[i].Height;
        Array.Sort(heights);
        double median = heights[labels.Count / 2];

        return cfg.CoincidenceFactor * median;
    }
}
