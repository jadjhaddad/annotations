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
// Detailed progress report (fired every ProgressInterval iterations)
// ---------------------------------------------------------------------------

public readonly struct SAProgressReport
{
    public int Iteration { get; }
    public double Temperature { get; }
    public double Energy { get; }
    public double OverlapComponent { get; }      // alpha × total overlap area
    public double DisplacementComponent { get; }
    public double SpreadComponent { get; }
    public double OverlapArea { get; }
    public int OverlapPairs { get; }
    public int AcceptedInWindow { get; }
    public int WindowSize { get; }
    public bool InStageB { get; }

    public double AcceptanceRate => WindowSize > 0 ? (double)AcceptedInWindow / WindowSize : 0.0;

    public SAProgressReport(
        int iteration, double temperature, double energy,
        double overlapComponent, double displacementComponent, double spreadComponent,
        double overlapArea, int overlapPairs,
        int acceptedInWindow, int windowSize, bool inStageB)
    {
        Iteration = iteration;
        Temperature = temperature;
        Energy = energy;
        OverlapComponent = overlapComponent;
        DisplacementComponent = displacementComponent;
        SpreadComponent = spreadComponent;
        OverlapArea = overlapArea;
        OverlapPairs = overlapPairs;
        AcceptedInWindow = acceptedInWindow;
        WindowSize = windowSize;
        InStageB = inStageB;
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

    /// <summary>
    /// Called every <see cref="ProgressInterval"/> iterations with full energy breakdown
    /// and window acceptance rate. Slightly more expensive than <see cref="OnProgress"/>
    /// (two extra O(n) passes for disp and spread components).
    /// </summary>
    public Action<SAProgressReport>? OnProgressDetailed { get; set; }

    /// <summary>How often to invoke <see cref="OnProgress"/> (and check Stage B transition).</summary>
    public int ProgressInterval { get; set; } = 500;

    /// <summary>T₀ computed by the last warmup pass. Readable after Run() returns.</summary>
    public double LastT0 { get; private set; }

    /// <summary>
    /// Fired immediately after the warmup pass, before the main loop.
    /// Args: (t0, warmupTargetAcceptance) — use to validate ~80% initial acceptance.
    /// </summary>
    public Action<double, double>? OnWarmupComplete { get; set; }

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
        if (cfg.StackLabelsByAnchor)
            return RunStackedByAnchor(labels, cfg, rng);

        return RunInternal(labels, cfg, rng);
    }

    private SAResult RunInternal(
        List<LabelState> labels,
        PlacerConfig cfg,
        Random? rng = null,
        double[]? maxAbsY = null,
        int[]? anchorOrder = null,
        int[]? nextConstraintRank = null)
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

        // --- 3. Initial energy (ordering term is zero at cold start since all offsets = 0) ---
        double energy = EnergyDelta.ComputeGlobal(labels, groups, cfg);
        double initialEnergy = energy;

        // --- 4. Warmup → T₀ ---
        double[] maxAbsYLocal = maxAbsY ?? BuildMaxAbsY(labels, cfg);

        double t0 = DetermineT0(labels, grid, groups, cfg, rng, maxAbsYLocal);
        LastT0 = t0;
        OnWarmupComplete?.Invoke(t0, cfg.WarmupTargetAcceptance);
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
        int windowAccepted = 0;  // accepted moves since last progress checkpoint
        int windowIter    = 0;  // total iterations since last progress checkpoint

        // Precompute reverse rank lookup for soft ordering penalty (stacked mode).
        // rankOf[labelIndex] = position in anchorOrder array, used for O(1) pair lookup.
        int[]? rankOf = null;
        if (anchorOrder != null && anchorOrder.Length > 1)
        {
            rankOf = new int[labels.Count];
            for (int k = 0; k < anchorOrder.Length; k++)
                rankOf[anchorOrder[k]] = k;
        }

        // nextConstraintRank[k] = first rank j > k where anchor Y differs from rank k.
        // This ensures blocks with same-Y neighbours (skipped pairs in old code) still get
        // an ordering constraint, fixing the "orphaned block" bug.
        int[]? ncr = nextConstraintRank;

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
                    newOffI = new Vector2D(0.0, labels[swapJ].CurrentOffset.Y);  // i takes j's Y offset
                    newOffJ = new Vector2D(0.0, label.CurrentOffset.Y);          // j takes i's Y offset
                }
            }

            // --- Clamp absolute Y ---
            newOffI = ClampY(new Vector2D(0.0, newOffI.Y), maxAbsYLocal[idx]);
            if (isSwap) newOffJ = ClampY(new Vector2D(0.0, newOffJ.Y), maxAbsYLocal[swapJ]);

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

            // --- Soft ordering penalty (stacked blocks only) ---
            // Penalises moves that would cause block-center crossing (lower-anchor block
            // drifting above higher-anchor block). SA can still accept such moves at high
            // temperature (escapes deadlocks), but avoids them at low temperature.
            if (rankOf != null)
                dE += OrderingDelta(anchorOrder!, ncr!, rankOf, labels, idx, newOffI, isSwap, swapJ, newOffJ, activeCfg);

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

                windowAccepted++;

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
            windowIter++;

            // --- Periodic checks ---
            if (iter % ProgressInterval == 0)
            {
                var (overlapArea, overlapPairs, _) = EnergyDelta.OverlapDiagnostics(labels);

                // Stage B transition
                if (!inStageB && overlapArea <= 0.0)
                {
                    inStageB = true;
                    zeroOverlapReached = true;
                    stageTransitionedThisIter = true;
                    activeCfg = stageBCfg;
                    // Recompute energy with Stage B weights so bestEnergy is on the same scale.
                    energy = EnergyDelta.ComputeGlobal(labels, groups, activeCfg);
                    if (rankOf != null)
                        energy += OrderingEnergy(anchorOrder!, ncr!, labels, activeCfg);
                    bestEnergy = energy;
                    bestOffsets = Snapshot(labels);
                    itersSinceBest = 0;
                    // Reheat so Stage B has thermal energy to pull labels back toward anchors.
                    if (cfg.StageBReheatFraction > 0.0)
                    {
                        double stageBTemp = t0 * cfg.StageBReheatFraction;
                        if (stageBTemp > temp) temp = stageBTemp;
                    }
                }

                OnProgress?.Invoke(iter, temp, energy, overlapArea);

                if (OnProgressDetailed != null)
                {
                    double dispComp   = EnergyDelta.ComputeDispComponent(labels, activeCfg);
                    double spreadComp = EnergyDelta.ComputeSpreadComponent(groups, activeCfg);
                    OnProgressDetailed(new SAProgressReport(
                        iter, temp, energy,
                        activeCfg.Alpha * overlapArea, dispComp, spreadComp,
                        overlapArea, overlapPairs,
                        windowAccepted, windowIter, inStageB));
                }

                windowAccepted = 0;
                windowIter = 0;
            }

            // --- Energy audit ---
            if (AuditInterval > 0 && OnEnergyAudit != null && iter % AuditInterval == 0)
            {
                double trueEnergy = EnergyDelta.ComputeGlobal(labels, groups, activeCfg);
                if (rankOf != null)
                    trueEnergy += OrderingEnergy(anchorOrder!, ncr!, labels, activeCfg);
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

    private readonly struct LabelBlock
    {
        public LabelState Block { get; }
        public List<int> Members { get; }

        public LabelBlock(LabelState block, List<int> members)
        {
            Block = block;
            Members = members;
        }
    }

    private SAResult RunStackedByAnchor(List<LabelState> labels, PlacerConfig cfg, Random? rng = null)
    {
        if (labels.Count == 0)
            return new SAResult(Array.Empty<Vector2D>(), 0, 0, 0, true, false, 0, 0, 0);

        // Cold start: all offsets to zero, then pre-stack labels per anchor.
        foreach (LabelState ls in labels) ls.CurrentOffset = Vector2D.Zero;
        Vector2D[] baseOffsets = ApplyAnchorStacks(labels);

        List<LabelBlock> blocks = BuildAnchorBlocks(labels);
        var blockLabels = new List<LabelState>(blocks.Count);
        foreach (LabelBlock b in blocks) blockLabels.Add(b.Block);

        // Always build anchor order — soft ordering penalty applies regardless of EnforceAnchorOrder.
        int[] order = BuildAnchorOrder(blockLabels);
        // nextConstraintRank[k] = first rank j > k where anchor Y differs from rank k.
        // This ensures every block has an ordering constraint even when consecutive
        // blocks share the same anchor Y.
        int[] nextConstraintRank = BuildNextConstraintRank(order, blockLabels);

        double blockMaxAbsY = ComputeBlockMaxAbsY(blockLabels, cfg);
        var blockMaxAbsYs = new double[blockLabels.Count];
        for (int i = 0; i < blockLabels.Count; i++) blockMaxAbsYs[i] = blockMaxAbsY;

        SAResult blockResult = RunInternal(blockLabels, cfg, rng, blockMaxAbsYs, order, nextConstraintRank);

        // Apply block offsets to all member labels (stacked at the same anchor/offset).
        foreach (LabelBlock block in blocks)
        {
            Vector2D off = block.Block.CurrentOffset;
            foreach (int idx in block.Members)
                labels[idx].CurrentOffset = new Vector2D(baseOffsets[idx].X + off.X, baseOffsets[idx].Y + off.Y);
        }

        Vector2D[] bestOffsets = Snapshot(labels);
        var (finalArea, finalPairs, finalMax) = EnergyDelta.OverlapDiagnosticsIgnoreSameAnchor(labels);

        return new SAResult(
            bestOffsets, blockResult.BestEnergy, blockResult.InitialEnergy, blockResult.TotalIterations,
            blockResult.ZeroOverlapReached, blockResult.StagnationStop,
            finalArea, finalPairs, finalMax);
    }

    private static List<LabelBlock> BuildAnchorBlocks(List<LabelState> labels)
    {
        var buckets = new Dictionary<(double X, double Y), List<int>>();
        for (int i = 0; i < labels.Count; i++)
        {
            LabelState ls = labels[i];
            var key = (ls.Anchor.X, ls.Anchor.Y);
            if (!buckets.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                buckets[key] = list;
            }
            list.Add(i);
        }

        var blocks = new List<LabelBlock>(buckets.Count);
        int blockId = 0;
        foreach (List<int> members in buckets.Values)
        {
            double width = 0.0;
            double height = 0.0;
            LabelState first = labels[members[0]];
            foreach (int idx in members)
            {
                LabelState ls = labels[idx];
                if (ls.Width > width) width = ls.Width;
                height += ls.Height;
            }

            var block = new LabelState(
                $"Block-{blockId++}",
                first.Anchor,
                width,
                height,
                first.SizeSource);

            blocks.Add(new LabelBlock(block, members));
        }

        return blocks;
    }

    private static int[] BuildAnchorOrder(List<LabelState> blockLabels)
    {
        int n = blockLabels.Count;
        int[] order = new int[n];
        for (int i = 0; i < n; i++) order[i] = i;
        Array.Sort(order, (a, b) => blockLabels[a].Anchor.Y.CompareTo(blockLabels[b].Anchor.Y));
        return order;
    }

    private static double ComputeBlockMaxAbsY(List<LabelState> blockLabels, PlacerConfig cfg)
    {
        var ys = new List<double>(blockLabels.Count);
        double maxBlockH = 0.0;
        double totalBlockH = 0.0;
        foreach (LabelState ls in blockLabels)
        {
            ys.Add(ls.Anchor.Y);
            totalBlockH += ls.Height;
            if (ls.Height > maxBlockH) maxBlockH = ls.Height;
        }
        ys.Sort();

        double minSpacing = 0.0;
        for (int i = 1; i < ys.Count; i++)
        {
            double d = Math.Abs(ys[i] - ys[i - 1]);
            if (d <= 0.0) continue;
            if (minSpacing <= 0.0 || d < minSpacing) minSpacing = d;
        }

        double avgBlockH = blockLabels.Count > 0 ? totalBlockH / blockLabels.Count : maxBlockH;

        // When anchor spacing is smaller than label height (labels denser than
        // their own footprint), the spacing-based clamp is insufficient to let
        // blocks clear each other. Fall back to the height-based clamp instead.
        if (minSpacing <= 0.0 || minSpacing < avgBlockH * 0.5)
            return cfg.MaxVerticalDisplacementFactor * maxBlockH;

        // Normal case: anchors are well-separated. Use the larger of the
        // anchor spacing and the block height as the unit, scaled by the factor.
        return cfg.MaxBlockDisplacementFactor * Math.Max(minSpacing, avgBlockH);
    }

    private static double[] BuildMaxAbsY(List<LabelState> labels, PlacerConfig cfg)
    {
        var maxAbsY = new double[labels.Count];
        for (int i = 0; i < labels.Count; i++)
            maxAbsY[i] = labels[i].MaxAbsoluteY(cfg);
        return maxAbsY;
    }

    private static Vector2D[] ApplyAnchorStacks(List<LabelState> labels)
    {
        var baseOffsets = new Vector2D[labels.Count];
        var buckets = new Dictionary<(double X, double Y), List<int>>();
        for (int i = 0; i < labels.Count; i++)
        {
            LabelState ls = labels[i];
            var key = (ls.Anchor.X, ls.Anchor.Y);
            if (!buckets.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>();
                buckets[key] = list;
            }
            list.Add(i);
        }

        foreach (List<int> members in buckets.Values)
        {
            // Deterministic order.
            members.Sort();

            double totalHeight = 0.0;
            foreach (int idx in members) totalHeight += labels[idx].Height;

            double y = totalHeight * 0.5;
            foreach (int idx in members)
            {
                double h = labels[idx].Height;
                y -= h * 0.5;
                baseOffsets[idx] = new Vector2D(0.0, y);
                y -= h * 0.5;
            }
        }

        // Apply base offsets to labels.
        for (int i = 0; i < labels.Count; i++)
            labels[i].CurrentOffset = baseOffsets[i];

        return baseOffsets;
    }

    // -------------------------------------------------------------------------
    // Warmup — determine T₀
    // -------------------------------------------------------------------------

    private static double DetermineT0(
        List<LabelState> labels, SpatialGrid grid, GroupRegistry groups,
        PlacerConfig cfg, Random rng, double[] maxAbsY)
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
            double dy = (rng.NextDouble() * 2 - 1) * diag;
            var off = new Vector2D(0.0, label.CurrentOffset.Y + dy);
            off = ClampY(off, maxAbsY[idx]);

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
        double dy = (rng.NextDouble() * 2 - 1) * scale;
        return new Vector2D(0.0, label.CurrentOffset.Y + dy);
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
        double dirY = iRect.CenterY - jRect.CenterY;
        if (Math.Abs(dirY) < 1e-12)
        {
            // Centers are aligned in Y — choose a random vertical direction.
            dirY = rng.NextDouble() < 0.5 ? -1.0 : 1.0;
        }
        else
        {
            dirY = dirY > 0.0 ? 1.0 : -1.0;
        }

        double step = (temp / t0) * LabelDiag(label) * 2.0;
        return new Vector2D(0.0, label.CurrentOffset.Y + dirY * step);
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

    private static bool IsOrderPreserved(
        int[] order,
        List<LabelState> labels,
        int idx,
        Vector2D newOffI,
        bool isSwap,
        int swapJ,
        Vector2D newOffJ)
    {
        int n = order.Length;
        if (n < 2) return true;

        for (int k = 0; k < n - 1; k++)
        {
            int a = order[k];
            int b = order[k + 1];

            double ay = (a == idx) ? (labels[a].Anchor.Y + newOffI.Y) : labels[a].GetBoundingRect().CenterY;
            if (isSwap && a == swapJ) ay = labels[a].Anchor.Y + newOffJ.Y;

            double by = (b == idx) ? (labels[b].Anchor.Y + newOffI.Y) : labels[b].GetBoundingRect().CenterY;
            if (isSwap && b == swapJ) by = labels[b].Anchor.Y + newOffJ.Y;

            if (ay > by) return false;
        }

        return true;
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
        double dLeader = EnergyDelta.DLeaderAwarenessSwap(idx, jdx, newOffI, newOffJ, labels, cfg);
        // dGroup = 0: swapping two members leaves group sums invariant.

        return dOverlap + dDispI + dDispJ + dLeader;
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
            BetaX2 = cfg.BetaX2StageB,
            BetaY2 = cfg.BetaY2StageB,
            Gamma = cfg.GammaStageB,

            // Stage B weights (same as source — unused in stage B config itself)
            AlphaStageB = cfg.AlphaStageB,
            BetaXStageB = cfg.BetaXStageB,
            BetaYStageB = cfg.BetaYStageB,
            BetaX2StageB = cfg.BetaX2StageB,
            BetaY2StageB = cfg.BetaY2StageB,
            GammaStageB = cfg.GammaStageB,

            CoolingRate = cfg.CoolingRate,
            MinTemp = cfg.MinTemp,
            StagnationIterations = cfg.StagnationIterations,
            MaxVerticalDisplacementFactor = cfg.MaxVerticalDisplacementFactor,
            MaxBlockDisplacementFactor = cfg.MaxBlockDisplacementFactor,
            StackLabelsByAnchor = cfg.StackLabelsByAnchor,
            EnforceAnchorOrder = cfg.EnforceAnchorOrder,
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
            StageBReheatFraction = cfg.StageBReheatFraction,
            // In Stage B, activate the ordering constraint so that leader-line crossings
            // are penalised once overlaps have been cleared.
            OrderingPenaltyWeight = cfg.OrderingPenaltyWeightStageB,
            OrderingPenaltyWeightStageB = cfg.OrderingPenaltyWeightStageB,
            LeaderLabelPenaltyWeight = cfg.LeaderLabelPenaltyWeightStageB,
            LeaderLabelPenaltyWeightStageB = cfg.LeaderLabelPenaltyWeightStageB,
            LeaderLeaderPenaltyWeight = cfg.LeaderLeaderPenaltyWeightStageB,
            LeaderLeaderPenaltyWeightStageB = cfg.LeaderLeaderPenaltyWeightStageB,
        };
    }

    // -------------------------------------------------------------------------
    // Soft ordering helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the total ordering-violation energy for the current block positions.
    /// For each block k, checks the pair (k, nextConstraintRank[k]) where
    /// nextConstraintRank[k] is the first rank after k with a different anchor Y.
    /// This ensures every block has an ordering constraint, even when adjacent pairs
    /// share the same anchor Y (which would be skipped under naive adjacent-pair logic).
    /// O(n) in the number of blocks.
    /// </summary>
    private static double OrderingEnergy(int[] order, int[] nextConstraintRank, List<LabelState> labels, PlacerConfig cfg)
    {
        double total = 0.0;
        for (int k = 0; k < order.Length; k++)
        {
            int nextK = nextConstraintRank[k];
            if (nextK < 0) continue;
            LabelState a = labels[order[k]];
            LabelState b = labels[order[nextK]];
            double cy_a = a.Anchor.Y + a.CurrentOffset.Y;
            double cy_b = b.Anchor.Y + b.CurrentOffset.Y;
            total += Math.Max(0.0, cy_a - cy_b);
        }
        return cfg.OrderingPenaltyWeight * total;
    }

    /// <summary>
    /// Incremental ordering energy delta for a proposed move of block <paramref name="idx"/>
    /// (and optionally a swap partner <paramref name="swapJ"/>).
    /// Iterates over all constraint pairs (k, nextConstraintRank[k]) and recomputes only
    /// those that involve the moved block(s).
    /// O(n) per call — acceptable since block count is small.
    /// </summary>
    private static double OrderingDelta(
        int[] order,
        int[] nextConstraintRank,
        int[] rankOf,
        List<LabelState> labels,
        int idx,
        Vector2D newOffI,
        bool isSwap,
        int swapJ,
        Vector2D newOffJ,
        PlacerConfig cfg)
    {
        double delta = 0.0;
        for (int k = 0; k < order.Length; k++)
        {
            int nextK = nextConstraintRank[k];
            if (nextK < 0) continue;

            int a = order[k];
            int b = order[nextK];

            bool aChanged = (a == idx) || (isSwap && a == swapJ);
            bool bChanged = (b == idx) || (isSwap && b == swapJ);
            if (!aChanged && !bChanged) continue;

            double oldCY_a = labels[a].Anchor.Y + labels[a].CurrentOffset.Y;
            double oldCY_b = labels[b].Anchor.Y + labels[b].CurrentOffset.Y;
            double oldViol = Math.Max(0.0, oldCY_a - oldCY_b);

            double newCY_a = oldCY_a;
            double newCY_b = oldCY_b;
            if (a == idx)             newCY_a = labels[a].Anchor.Y + newOffI.Y;
            if (isSwap && a == swapJ) newCY_a = labels[a].Anchor.Y + newOffJ.Y;
            if (b == idx)             newCY_b = labels[b].Anchor.Y + newOffI.Y;
            if (isSwap && b == swapJ) newCY_b = labels[b].Anchor.Y + newOffJ.Y;
            double newViol = Math.Max(0.0, newCY_a - newCY_b);

            delta += newViol - oldViol;
        }
        return cfg.OrderingPenaltyWeight * delta;
    }

    /// <summary>
    /// For each rank k, finds the first rank j &gt; k where the anchor Y of order[j]
    /// differs from the anchor Y of order[k].
    /// Returns -1 when no such rank exists.
    /// This ensures every block has an ordering constraint, even when consecutive
    /// blocks share the same anchor Y (which would otherwise be skipped).
    /// </summary>
    private static int[] BuildNextConstraintRank(int[] order, List<LabelState> labels)
    {
        int n = order.Length;
        int[] next = new int[n];
        for (int k = 0; k < n; k++)
        {
            next[k] = -1;
            double anchorY = labels[order[k]].Anchor.Y;
            for (int j = k + 1; j < n; j++)
            {
                if (Math.Abs(labels[order[j]].Anchor.Y - anchorY) >= 1e-9)
                {
                    next[k] = j;
                    break;
                }
            }
        }
        return next;
    }
}
