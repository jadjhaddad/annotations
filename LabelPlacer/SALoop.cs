using System;
using System.Collections.Generic;

namespace LabelPlacer;

// ---------------------------------------------------------------------------
// Result
// ---------------------------------------------------------------------------

public sealed class SAResult
{
    /// <summary>Best offsets found during the run, indexed parallel to the input label list.</summary>
    public Vector2D[] BestOffsets { get; }

    public double BestEnergy { get; }
    public double InitialEnergy { get; }
    public int TotalIterations { get; }

    /// <summary>True if overlap area reached zero during Stage A.</summary>
    public bool ZeroOverlapReached { get; }

    /// <summary>True if the loop exited due to stagnation rather than temperature floor.</summary>
    public bool StagnationStop { get; }

    // Diagnostics computed on the best state after restoration.
    public double FinalOverlapArea { get; }
    public int FinalOverlapPairs { get; }
    public double FinalMaxPairOverlap { get; }

    public SAResult(
        Vector2D[] bestOffsets, double bestEnergy, double initialEnergy,
        int totalIterations, bool zeroOverlapReached, bool stagnationStop,
        double finalOverlapArea, int finalOverlapPairs, double finalMaxPairOverlap)
    {
        BestOffsets = bestOffsets;
        BestEnergy = bestEnergy;
        InitialEnergy = initialEnergy;
        TotalIterations = totalIterations;
        ZeroOverlapReached = zeroOverlapReached;
        StagnationStop = stagnationStop;
        FinalOverlapArea = finalOverlapArea;
        FinalOverlapPairs = finalOverlapPairs;
        FinalMaxPairOverlap = finalMaxPairOverlap;
    }
}

public enum SAMoveKind
{
    Random,
    Directional,
    Swap,
    SwapFallbackRandom,
}

public readonly struct SAIterationLog
{
    public int Iteration { get; }
    public double Temperature { get; }
    public double Energy { get; }
    public double DeltaEnergy { get; }
    public bool Accepted { get; }
    public int LabelIndex { get; }
    public int SwapPartnerIndex { get; }
    public SAMoveKind MoveKind { get; }
    public bool InStageB { get; }
    public bool StageTransitioned { get; }

    public SAIterationLog(
        int iteration,
        double temperature,
        double energy,
        double deltaEnergy,
        bool accepted,
        int labelIndex,
        int swapPartnerIndex,
        SAMoveKind moveKind,
        bool inStageB,
        bool stageTransitioned)
    {
        Iteration = iteration;
        Temperature = temperature;
        Energy = energy;
        DeltaEnergy = deltaEnergy;
        Accepted = accepted;
        LabelIndex = labelIndex;
        SwapPartnerIndex = swapPartnerIndex;
        MoveKind = moveKind;
        InStageB = inStageB;
        StageTransitioned = stageTransitioned;
    }
}

// ---------------------------------------------------------------------------
// SA loop
// ---------------------------------------------------------------------------

public sealed class SALoop
{
    /// <summary>
    /// Called every <see cref="ProgressInterval"/> iterations.
    /// Args: (iteration, temperature, energy, overlapArea).
    /// </summary>
    public Action<int, double, double, double>? OnProgress { get; set; }

    /// <summary>How often to invoke <see cref="OnProgress"/> (and check Stage B transition).</summary>
    public int ProgressInterval { get; set; } = 500;

    /// <summary>
    /// When > 0, recomputes global energy from scratch every this many iterations and
    /// compares it to the running tracker. Fires <see cref="OnEnergyAudit"/> with the result.
    /// Expensive (O(n²)) — use only for diagnostics.
    /// </summary>
    public int AuditInterval { get; set; } = 0;

    /// <summary>
    /// Called on each audit tick.
    /// Args: (iteration, runningEnergy, trueEnergy, drift = running - true).
    /// </summary>
    public Action<int, double, double, double>? OnEnergyAudit { get; set; }

    /// <summary>
    /// Called every <see cref="IterationCallbackInterval"/> iterations.
    /// Designed for cheap per-iteration tracing (no O(n²) diagnostics).
    /// </summary>
    public Action<SAIterationLog>? OnIteration { get; set; }

    /// <summary>
    /// How often to invoke <see cref="OnIteration"/>.
    /// Set to 1 to trace every iteration. 0 disables tracing.
    /// </summary>
    public int IterationCallbackInterval { get; set; } = 0;

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------

    public SAResult Run(List<LabelState> labels, PlacerConfig cfg, Random? rng = null)
    {
        if (labels.Count == 0)
            return new SAResult(Array.Empty<Vector2D>(), 0, 0, 0, true, false, 0, 0, 0);

        rng ??= new Random(42);

        // --- 1. Cold start: all offsets to zero ---
        foreach (LabelState ls in labels) ls.CurrentOffset = Vector2D.Zero;

        // --- 2. Build structures ---
        GroupRegistry groups = GroupRegistry.Build(labels, cfg);
        double cellSize = SpatialGrid.RecommendCellSize(labels);
        SpatialGrid grid = new SpatialGrid(cellSize);
        for (int i = 0; i < labels.Count; i++) grid.Insert(i, labels[i]);

        // --- 3. Initial energy ---
        double energy = EnergyDelta.ComputeGlobal(labels, groups, cfg);
        double initialEnergy = energy;

        // --- 4. Warmup → T₀ ---
        double t0 = DetermineT0(labels, grid, groups, cfg, rng);
        double temp = t0;

        // --- 5. Stage B config (different weights, same everything else) ---
        PlacerConfig stageBCfg = BuildStageBConfig(cfg);
        PlacerConfig activeCfg = cfg;

        // --- 6. Best-state tracking ---
        Vector2D[] bestOffsets = Snapshot(labels);
        double bestEnergy = energy;

        // --- 7. Move-mix thresholds (normalized) ---
        double totalW = cfg.MoveWeightRandom + cfg.MoveWeightDirectional + cfg.MoveWeightSwap;
        double thrRandom = cfg.MoveWeightRandom / totalW;
        double thrDir = thrRandom + cfg.MoveWeightDirectional / totalW;
        // roll < thrRandom  → Random
        // roll < thrDir     → Directional
        // roll >= thrDir    → Swap (or fallback to Random if singleton)

        // --- 8. Scratch collections (reused per iteration to reduce allocation) ---
        HashSet<int> seenNeighbors = new HashSet<int>();
        List<int> neighbors = new List<int>(64);

        // --- 9. Loop state ---
        bool inStageB = false;
        bool zeroOverlapReached = false;
        bool stagnationStop = false;
        int itersSinceBest = 0;
        int iter = 0;

        // -----------------------------------------------------------------------
        // Main SA loop
        // -----------------------------------------------------------------------
        while (temp > cfg.MinTemp)
        {
            int idx = rng.Next(labels.Count);
            LabelState label = labels[idx];

            // --- Propose move ---
            double roll = rng.NextDouble();
            bool isSwap = false;
            int swapJ = -1;
            SAMoveKind moveKind = SAMoveKind.Random;
            Vector2D newOffI = default;
            Vector2D newOffJ = default;
            bool stageTransitionedThisIter = false;

            if (roll < thrRandom || label.GroupId < 0)
            {
                moveKind = SAMoveKind.Random;
                newOffI = ProposeRandom(label, temp, t0, rng);
            }
            else if (roll < thrDir)
            {
                moveKind = SAMoveKind.Directional;
                newOffI = ProposeDirectional(idx, label, temp, t0, grid, labels, rng);
            }
            else
            {
                // Swap within coincident group
                swapJ = PickSwapPartner(idx, label, groups, rng);
                if (swapJ < 0)
                {
                    moveKind = SAMoveKind.SwapFallbackRandom;
                    newOffI = ProposeRandom(label, temp, t0, rng);  // fallback
                }
                else
                {
                    isSwap = true;
                    moveKind = SAMoveKind.Swap;
                    newOffI = labels[swapJ].CurrentOffset;  // i takes j's offset
                    newOffJ = label.CurrentOffset;           // j takes i's offset
                }
            }

            // --- Clamp absolute Y ---
            newOffI = ClampY(newOffI, label.MaxAbsoluteY(cfg));
            if (isSwap) newOffJ = ClampY(newOffJ, labels[swapJ].MaxAbsoluteY(cfg));

            // --- Build deduplicated neighbor list ---
            Rect2D oldRectI = label.GetBoundingRect();
            Rect2D newRectI = label.GetBoundingRectAt(newOffI);
            Rect2D queryI = Rect2D.Union(oldRectI, newRectI);

            seenNeighbors.Clear();
            neighbors.Clear();
            foreach (int n in grid.QueryNeighbors(idx, queryI))
                if (seenNeighbors.Add(n)) neighbors.Add(n);

            // For swap, also query around j and merge.
            if (isSwap)
            {
                Rect2D oldRectJ = labels[swapJ].GetBoundingRect();
                Rect2D newRectJ = labels[swapJ].GetBoundingRectAt(newOffJ);
                Rect2D queryJ = Rect2D.Union(oldRectJ, newRectJ);
                foreach (int n in grid.QueryNeighbors(swapJ, queryJ))
                    if (seenNeighbors.Add(n)) neighbors.Add(n);
            }

            // --- Compute dE ---
            double dE;
            if (!isSwap)
            {
                dE = EnergyDelta.Compute(idx, newOffI, labels, neighbors, groups, activeCfg);
            }
            else
            {
                // Swap: approximate dE as sum of independent deltas, excluding the i-j pair
                // from each other's neighbor lists (they're both moving, interaction cancels).
                dE = SwapDelta(idx, swapJ, newOffI, newOffJ, labels, neighbors, groups, activeCfg);
            }

            // --- Metropolis accept/reject ---
            bool accept = dE < 0.0 || rng.NextDouble() < Math.Exp(-dE / temp);

            if (accept)
            {
                if (!isSwap)
                {
                    CommitMove(idx, newOffI, oldRectI, newRectI, label, grid, groups);
                    energy += dE;
                }
                else
                {
                    CommitSwap(idx, swapJ, newOffI, newOffJ, labels, grid, groups);
                    energy += dE;
                }

                if (energy < bestEnergy)
                {
                    bestEnergy = energy;
                    bestOffsets = Snapshot(labels);
                    itersSinceBest = 0;
                }
            }

            itersSinceBest++;
            temp *= cfg.CoolingRate;
            if (temp < cfg.MinTemp) temp = cfg.MinTemp;
            iter++;

            // --- Periodic checks ---
            if (iter % ProgressInterval == 0)
            {
                var (overlapArea, _, _) = EnergyDelta.OverlapDiagnostics(labels);

                // Stage B transition
                if (!inStageB && overlapArea <= 0.0)
                {
                    inStageB = true;
                    zeroOverlapReached = true;
                    stageTransitionedThisIter = true;
                    activeCfg = stageBCfg;
                    // Recompute energy with Stage B weights so bestEnergy is on the same scale.
                    energy = EnergyDelta.ComputeGlobal(labels, groups, activeCfg);
                    bestEnergy = energy;
                    bestOffsets = Snapshot(labels);
                    itersSinceBest = 0;
                }

                OnProgress?.Invoke(iter, temp, energy, overlapArea);
            }

            // --- Energy audit ---
            if (AuditInterval > 0 && OnEnergyAudit != null && iter % AuditInterval == 0)
            {
                double trueEnergy = EnergyDelta.ComputeGlobal(labels, groups, activeCfg);
                OnEnergyAudit(iter, energy, trueEnergy, energy - trueEnergy);
            }

            if (IterationCallbackInterval > 0 && OnIteration != null && iter % IterationCallbackInterval == 0)
            {
                OnIteration(new SAIterationLog(
                    iter,
                    temp,
                    energy,
                    dE,
                    accept,
                    idx,
                    isSwap ? swapJ : -1,
                    moveKind,
                    inStageB,
                    stageTransitionedThisIter));
            }

            // --- Stagnation check ---
            if (itersSinceBest >= cfg.StagnationIterations)
            {
                stagnationStop = true;
                break;
            }
        }

        // --- Restore best state (offsets only; grid/stats no longer needed) ---
        for (int i = 0; i < labels.Count; i++)
            labels[i].CurrentOffset = bestOffsets[i];

        var (finalArea, finalPairs, finalMax) = EnergyDelta.OverlapDiagnostics(labels);

        return new SAResult(
            bestOffsets, bestEnergy, initialEnergy, iter,
            zeroOverlapReached, stagnationStop,
            finalArea, finalPairs, finalMax);
    }

    // -------------------------------------------------------------------------
    // Warmup — determine T₀
    // -------------------------------------------------------------------------

    private static double DetermineT0(
        List<LabelState> labels, SpatialGrid grid, GroupRegistry groups,
        PlacerConfig cfg, Random rng)
    {
        var uphillDeltas = new List<double>(cfg.WarmupSamples);
        var allAbsDeltas = new List<double>(cfg.WarmupSamples);
        var scratch = new List<int>(32);
        var seen = new HashSet<int>();

        for (int s = 0; s < cfg.WarmupSamples; s++)
        {
            int idx = rng.Next(labels.Count);
            LabelState label = labels[idx];

            // Use one full label diagonal as warmup step amplitude.
            double diag = LabelDiag(label);
            double dx = (rng.NextDouble() * 2 - 1) * diag;
            double dy = (rng.NextDouble() * 2 - 1) * diag;
            var off = new Vector2D(label.CurrentOffset.X + dx,
                                       label.CurrentOffset.Y + dy);
            off = ClampY(off, label.MaxAbsoluteY(cfg));

            Rect2D old = label.GetBoundingRect();
            Rect2D nw = label.GetBoundingRectAt(off);

            seen.Clear(); scratch.Clear();
            foreach (int n in grid.QueryNeighbors(idx, Rect2D.Union(old, nw)))
                if (seen.Add(n)) scratch.Add(n);

            double dE = EnergyDelta.Compute(idx, off, labels, scratch, groups, cfg);
            double absDE = Math.Abs(dE);
            if (absDE > 0.0) allAbsDeltas.Add(absDE);
            if (dE > 0.0) uphillDeltas.Add(dE);
        }

        // Use uphill deltas when available (standard calibration).
        // Fall back to the downhill magnitude when all warmup moves are downhill
        // (e.g. fully coincident labels where any spread reduces the huge initial overlap).
        // This prevents T₀ collapsing to 1.0 when the energy scale is in the millions.
        IReadOnlyList<double> calibrationDeltas =
            uphillDeltas.Count > 0 ? (IReadOnlyList<double>)uphillDeltas : allAbsDeltas;

        if (calibrationDeltas.Count == 0) return cfg.MinTemp * 2;

        double avgDelta = 0.0;
        foreach (double d in calibrationDeltas) avgDelta += d;
        avgDelta /= calibrationDeltas.Count;

        // exp(-avgDelta / T0) = targetAcceptance  →  T0 = -avgDelta / ln(target)
        double t0 = -avgDelta / Math.Log(cfg.WarmupTargetAcceptance);
        return Math.Max(t0, cfg.MinTemp * 2);  // floor so we don't start below minTemp
    }

    // -------------------------------------------------------------------------
    // Move proposals
    // -------------------------------------------------------------------------

    private static Vector2D ProposeRandom(LabelState label, double temp, double t0, Random rng)
    {
        double scale = (temp / t0) * LabelDiag(label) * 2.0;
        double dx = (rng.NextDouble() * 2 - 1) * scale;
        double dy = (rng.NextDouble() * 2 - 1) * scale;
        return new Vector2D(label.CurrentOffset.X + dx, label.CurrentOffset.Y + dy);
    }

    private static Vector2D ProposeDirectional(
        int idx, LabelState label, double temp, double t0,
        SpatialGrid grid, List<LabelState> labels, Random rng)
    {
        Rect2D iRect = label.GetBoundingRect();

        // Find the neighbor with the most overlap.
        double maxOverlap = 0.0;
        int worstJ = -1;
        foreach (int j in grid.QueryNeighbors(idx, iRect))
        {
            double ov = Rect2D.OverlapArea(iRect, labels[j].GetBoundingRect());
            if (ov > maxOverlap) { maxOverlap = ov; worstJ = j; }
        }

        if (worstJ < 0)
            return ProposeRandom(label, temp, t0, rng);  // no overlap — random

        // Direction: from worst neighbor center toward i center.
        Rect2D jRect = labels[worstJ].GetBoundingRect();
        double dirX = iRect.CenterX - jRect.CenterX;
        double dirY = iRect.CenterY - jRect.CenterY;
        double len = Math.Sqrt(dirX * dirX + dirY * dirY);

        if (len < 1e-12)
        {
            // Centers are coincident — push in a random direction.
            double angle = rng.NextDouble() * 2 * Math.PI;
            dirX = Math.Cos(angle); dirY = Math.Sin(angle);
        }
        else
        {
            dirX /= len; dirY /= len;
        }

        double step = (temp / t0) * LabelDiag(label) * 2.0;
        return new Vector2D(
            label.CurrentOffset.X + dirX * step,
            label.CurrentOffset.Y + dirY * step);
    }

    /// <summary>
    /// Returns the index of a randomly chosen partner in the same group, or -1 if none.
    /// </summary>
    private static int PickSwapPartner(
        int idx, LabelState label, GroupRegistry groups, Random rng)
    {
        if (label.GroupId < 0) return -1;
        IReadOnlyList<int> members = groups.GetMembers(label.GroupId);
        if (members.Count < 2) return -1;

        // Pick a random member that is not idx.
        int pick = rng.Next(members.Count - 1);
        for (int i = 0; i < members.Count; i++)
        {
            if (members[i] == idx) continue;
            if (pick == 0) return members[i];
            pick--;
        }
        return -1;  // unreachable
    }

    // -------------------------------------------------------------------------
    // Commit helpers
    // -------------------------------------------------------------------------

    private static void CommitMove(
        int idx, Vector2D newOff, Rect2D oldRect, Rect2D newRect,
        LabelState label, SpatialGrid grid, GroupRegistry groups)
    {
        grid.Update(idx, oldRect, newRect);
        if (label.GroupId >= 0)
            groups[label.GroupId].Replace(
                label.CurrentOffset.X, label.CurrentOffset.Y,
                newOff.X, newOff.Y);
        label.CurrentOffset = newOff;
    }

    private static void CommitSwap(
        int idx, int jdx, Vector2D newOffI, Vector2D newOffJ,
        List<LabelState> labels, SpatialGrid grid, GroupRegistry groups)
    {
        LabelState li = labels[idx];
        LabelState lj = labels[jdx];
        Rect2D oldI = li.GetBoundingRect();
        Rect2D oldJ = lj.GetBoundingRect();
        Rect2D newI = li.GetBoundingRectAt(newOffI);
        Rect2D newJ = lj.GetBoundingRectAt(newOffJ);

        grid.Update(idx, oldI, newI);
        grid.Update(jdx, oldJ, newJ);

        // Group stats: swap leaves sums unchanged (same values, just reassigned).
        // No call to Replace needed — the group spread is identical after a pure swap.

        li.CurrentOffset = newOffI;
        lj.CurrentOffset = newOffJ;
    }

    // -------------------------------------------------------------------------
    // Swap energy delta (approximate)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes the approximate combined dE for swapping offsets of i and j.
    /// Each label's delta is computed independently, with the other label excluded
    /// from their neighbor lists (both are moving, so their mutual interaction
    /// cancels to first order).
    /// Group spread delta is zero for a pure swap (sums are invariant).
    /// </summary>
    private static double SwapDelta(
        int idx, int jdx,
        Vector2D newOffI, Vector2D newOffJ,
        List<LabelState> labels,
        List<int> combinedNeighbors,
        GroupRegistry groups,
        PlacerConfig cfg)
    {
        LabelState li = labels[idx];
        LabelState lj = labels[jdx];

        Rect2D oldI = li.GetBoundingRect();
        Rect2D newI = li.GetBoundingRectAt(newOffI);
        Rect2D oldJ = lj.GetBoundingRect();
        Rect2D newJ = lj.GetBoundingRectAt(newOffJ);

        // Overlap delta for i: use neighbors excluding both moved labels (idx/jdx).
        double dOverlapI = 0.0;
        foreach (int n in combinedNeighbors)
        {
            if (n == idx || n == jdx) continue;
            Rect2D nr = labels[n].GetBoundingRect();
            dOverlapI += Rect2D.OverlapArea(newI, nr) - Rect2D.OverlapArea(oldI, nr);
        }

        // Overlap delta for j: use neighbors excluding both moved labels (idx/jdx).
        double dOverlapJ = 0.0;
        foreach (int n in combinedNeighbors)
        {
            if (n == idx || n == jdx) continue;
            Rect2D nr = labels[n].GetBoundingRect();
            dOverlapJ += Rect2D.OverlapArea(newJ, nr) - Rect2D.OverlapArea(oldJ, nr);
        }

        double dOverlap = cfg.Alpha * (dOverlapI + dOverlapJ);
        double dDispI = EnergyDelta.DDisplacement(li.CurrentOffset, newOffI, cfg);
        double dDispJ = EnergyDelta.DDisplacement(lj.CurrentOffset, newOffJ, cfg);
        // dGroup = 0: swapping two members leaves group sums invariant.

        return dOverlap + dDispI + dDispJ;
    }

    // -------------------------------------------------------------------------
    // Utilities
    // -------------------------------------------------------------------------

    private static double LabelDiag(LabelState ls)
        => Math.Sqrt(ls.Width * ls.Width + ls.Height * ls.Height);

    private static Vector2D ClampY(Vector2D off, double maxAbsY)
    {
        if (off.Y > maxAbsY) off.Y = maxAbsY;
        if (off.Y < -maxAbsY) off.Y = -maxAbsY;
        return off;
    }

    private static Vector2D[] Snapshot(List<LabelState> labels)
    {
        var snap = new Vector2D[labels.Count];
        for (int i = 0; i < labels.Count; i++) snap[i] = labels[i].CurrentOffset;
        return snap;
    }

    private static PlacerConfig BuildStageBConfig(PlacerConfig cfg)
    {
        // Copy all fields; override only the four weight properties.
        return new PlacerConfig
        {
            Alpha = cfg.AlphaStageB,
            BetaX = cfg.BetaXStageB,
            BetaY = cfg.BetaYStageB,
            Gamma = cfg.GammaStageB,

            // Stage B weights (same as source — unused in stage B config itself)
            AlphaStageB = cfg.AlphaStageB,
            BetaXStageB = cfg.BetaXStageB,
            BetaYStageB = cfg.BetaYStageB,
            GammaStageB = cfg.GammaStageB,

            CoolingRate = cfg.CoolingRate,
            MinTemp = cfg.MinTemp,
            StagnationIterations = cfg.StagnationIterations,
            MaxVerticalDisplacementFactor = cfg.MaxVerticalDisplacementFactor,
            LineSpacingFactor = cfg.LineSpacingFactor,
            CharWidthFactor = cfg.CharWidthFactor,
            SizeSafetyFactorX = cfg.SizeSafetyFactorX,
            SizeSafetyFactorY = cfg.SizeSafetyFactorY,
            CoincidenceFactor = cfg.CoincidenceFactor,
            MoveWeightRandom = cfg.MoveWeightRandom,
            MoveWeightDirectional = cfg.MoveWeightDirectional,
            MoveWeightSwap = cfg.MoveWeightSwap,
            WarmupSamples = cfg.WarmupSamples,
            WarmupTargetAcceptance = cfg.WarmupTargetAcceptance,
        };
    }
}
