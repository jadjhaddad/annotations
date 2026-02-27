# Civil 3D Label Placement — Implementation Plan

## Problem Statement

Civil 3D point annotations overlap in plan view because multiple points may share the same XY (often differing only in elevation). We need an algorithm that repositions labels only (never point geometry) so no two label rectangles overlap, while keeping labels as close as possible to their anchor points and producing a draftsman-natural layout.

---

## Critical Constraint: Labels Only (Enforced by Architecture)

- Point positions are read-only anchors
- The algorithm never writes point coordinates
- Only label offset properties are written back at the end

Hard architectural enforcement:

- `ReadPointsAndLabels()` reads point XY as anchors only
- `WriteOffsets()` writes only label offsets
- `LabelPlacer` has no API access to points or drawing objects — pure geometry only

---

## Key Insight: Coincident Labels

The placer does not care why overlaps occur. Every label is a rectangle with an anchor. Coincident labels (same or near-same anchor XY) are handled by a soft preference — they prefer to remain together (stack-like behavior) but can loosen slightly if it reduces overlap. This is expressed via an intragroup spread penalty in the energy function.

---

## Algorithm: Simulated Annealing

SA is the right choice because:

- Quality matters more than speed — this is not run in real time
- Dense clusters create nasty local minima that greedy and force-directed approaches cannot escape
- SA escapes local minima by accepting uphill moves early in the run
- No ML needed — the geometry is well-defined and formalizable

---

## Label Dimensions

Labels are not globally fixed-size.

**Preferred** — use actual extents from Civil 3D label/text components if the API allows.

**Fallback estimator with safety margins:**

```
height = lineCount × textHeight × lineSpacingFactor × sizeSafetyFactorY
width  = longestLineCharCount × textHeight × charWidthFactor × sizeSafetyFactorX
```

Safety margins (`sizeSafetyFactorX` ~1.10, `sizeSafetyFactorY` ~1.15) account for font metrics variance and ensure labels don't visually touch even when mathematically non-overlapping. All factors are configurable.

---

## Geometry Model: Anchor → Rectangle Convention

Civil 3D label offsets may reference different internal points depending on style (center, corner, leader landing, etc.). To keep `LabelPlacer` clean:

- The wrapper defines a consistent internal convention for `LabelState` — the offset is always the rectangle center
- The wrapper is responsible for mapping between Civil 3D label offset semantics and this convention
- If exact mapping is not obtainable, use conservative approximations with safety margins

---

## Re-run Behavior: Cold Start

When the algorithm is run on a drawing where labels have already been moved by a previous run, all label offsets are reset to zero before the SA loop begins. This means every run starts from the same initial state (all labels at their anchors) regardless of prior runs. This avoids path dependency from previous runs and ensures reproducible results. Warm-starting from prior offsets may converge faster but produces layout quality that depends on run history, which is harder to reason about and validate.

---

## Energy Function (Incremental)

Global form (conceptual reference):

```
E = alpha  × Σ overlap_area(i,j)       for all label pairs (i,j)
  + beta_x × Σ |offset_x(i)|           for all labels i
  + beta_y × Σ |offset_y(i)|           for all labels i
  + gamma  × Σ intragroup_spread(g)    for all coincident groups g
```

Weight hierarchy: `alpha >> gamma >> beta_y >> beta_x`

The global sum is never recomputed per iteration. All energy evaluation uses incremental delta computation (see below).

**Term 1 — Overlap Area:**

```
overlapX    = max(0, min(r1.right, r2.right) - max(r1.left, r2.left))
overlapY    = max(0, min(r1.top,   r2.top)   - max(r1.bottom, r2.bottom))
overlapArea = overlapX × overlapY
```

Continuous overlap area gives the algorithm a gradient — barely touching labels have near-zero cost, heavily overlapping ones have high cost.

**Term 2/3 — Displacement:**

```
beta_x × |offset_x|
beta_y × |offset_y|    // beta_y much larger than beta_x
```

**Term 4 — Intragroup Spread:**

```
spread(g) = variance(offset_x of labels in g) + variance(offset_y of labels in g)
```

Computed via O(1) running stats — no full recompute per move (see Incremental dE).

---

## Core Performance: Incremental dE

The global energy sum is never recomputed per iteration. When proposing a move for label i, only the affected terms are recomputed:

**Overlap delta** — spatial grid returns neighbors N(i) only:

```
dE_overlap = alpha × Σ_{j in N(i)} ( overlap(new_i, j) - overlap(old_i, j) )
```

**Displacement delta:**

```
dE_disp = beta_x × (|x_new| - |x_old|) + beta_y × (|y_new| - |y_old|)
```

**Group spread delta** — per-group running stats updated in O(1):

Maintain per group: `n, sumX, sumX2, sumY, sumY2`

```
varX   = (sumX2 / n) - (sumX / n)²
varY   = (sumY2 / n) - (sumY / n)²
spread = varX + varY
```

When label i changes offset, update group sums in O(1), compute old vs new spread:

```
dE_group = gamma × (spread_new(g) - spread_old(g))
```

Total incremental delta:

```
dE = dE_overlap + dE_disp + dE_group
```

---

## Movement Constraints

**Absolute Y clamp** — after proposing a move, clamp the resulting absolute Y offset:

```
offset_y = clamp(offset_y, -maxVerticalDisplacement, +maxVerticalDisplacement)
```

This prevents vertical random walk over many iterations. Clamping only the delta would still allow slow drift to arbitrary vertical positions over many steps — clamping the absolute offset prevents that entirely.

**Vertical bias** — `beta_y >> beta_x` (e.g. 10×) so the algorithm actively prefers horizontal solutions.

---

## Coincident Grouping (Scale-Aware)

Replace fixed tolerance with a scale-aware value:

```
coincidenceTolerance = coincidenceFactor × medianLabelHeight
```

This is robust across Civil 3D drawings with varying annotation scales and drawing units. A fixed value like 0.001 would be meaningless in many real drawings.

Robust hash key for grouping:

```
key = (round(x / tol), round(y / tol))
```

Coincident groups are computed in preprocessing and stored as `GroupId` on each `LabelState`.

---

## Spatial Index: Uniform Grid

- Cell size: approximately `max(labelWidth, labelHeight)` or a tuned multiple
- Each label occupies one or more cells depending on rectangle extents
- Neighbor queries return candidates from cells overlapped by label i
- Enables near O(1) neighbor lookup per iteration in typical drawing densities

---

## Algorithm Steps

### 1. Preprocessing

- Reset all label offsets to zero (cold start)
- Read labels and anchors from Civil 3D
- Compute label dimensions (API extents preferred, fallback estimator)
- Assign coincident group IDs (scale-aware tolerance)
- Build spatial grid occupancy
- Initialize per-group running stats (n, sumX, sumX2, sumY, sumY2)

### 2. Warmup Pass (Determine T₀)

- Apply N random perturbations, record uphill ΔE values
- Set T₀ so that `exp(-avgΔE⁺ / T₀) ≈ 0.8`
- Ensures ~80% of uphill moves are accepted at start

### 3. Main SA Loop

Each iteration:

1. Pick a random label i
2. Propose a move using a weighted mix of move types:
   - **Random small** — baseline random perturbation scaled to temperature
   - **Directional** — nudge away from the worst overlapping neighbor (efficiency)
   - **Swap-within-group** — swap offsets of two labels in the same coincident group
3. Apply absolute Y clamp to proposed offset
4. Compute dE incrementally (neighbors + group stats)
5. Accept if `dE < 0`, or with probability `exp(-dE / T)` if `dE > 0`
6. If accepted: update label offset, spatial grid occupancy, group running stats
7. Save as best state if current energy is lowest seen so far
8. `T = T × coolingRate`

### 4. Stopping Criteria

Stop at whichever comes first:

- `T < minTemp`
- No improvement in best energy for `stagnationIterations` consecutive iterations
- (Optional) Overlap area reaches zero and displacement stabilizes over a window

### 5. Two-Stage Objective

**Stage A — Eliminate overlaps:**
Run with high `alpha`. Continue until overlap area reaches zero or plateaus.

**Stage B — Beautify:**
Reduce `alpha` slightly, increase displacement and group spread influence. Refine layout tightness.

Still SA throughout — just change weights after the Stage A milestone. This separates the hard constraint (no overlaps) from the soft preference (tight, natural layout).

### 6. Return Best State

Return the lowest-energy state seen across the entire run, not the final state. The final state may have drifted slightly uphill due to late accepted moves.

---

## Write Back (Labels Only)

- Apply best-state offsets to Civil 3D label objects only
- Never modify point coordinates
- Report residual overlaps if any remain after the run (count, total area, worst pair) — Civil 3D highlighting deferred to a later phase

---

## Diagnostics Output

Report at completion:

- Total overlap area
- Count of overlapping label pairs
- Maximum single-pair overlap area
- Number of labels with nonzero residual overlap

Civil 3D visual highlighting of worst offenders (select, zoom-to) is deferred — implement after the core algorithm is validated.

---

## Architecture

```
LabelPlacer (pure geometry, no Civil 3D dependency)
├── LabelState
│   ├── AnchorPosition (fixed, read-only)
│   ├── GroupId
│   ├── CurrentOffset
│   ├── ComputedWidth, ComputedHeight (with safety margins)
│   └── GetBoundingRect()
├── SpatialGrid
│   ├── Insert / Remove / Update(label rect)
│   └── QueryNeighbors(label rect) → candidates
├── GroupStats
│   ├── n, sumX, sumX2, sumY, sumY2
│   └── Spread()
├── EnergyDelta
│   ├── dOverlap(label, neighbors)
│   ├── dDisplacement(label, old, new)
│   └── dGroupSpread(group, old offset, new offset)
└── SALoop
    ├── Warmup pass
    ├── Move proposals (random / directional / swap)
    ├── Absolute Y clamp
    ├── Incremental accept/reject
    ├── Best-state tracking
    ├── Two-stage weight transition
    └── Stagnation stopping

Civil3DWrapper (thin layer, Civil 3D API calls only here)
├── ReadPointsAndLabels() → List<LabelState>
│   ├── Reads anchor XY from points (read-only)
│   ├── Resets all offsets to zero (cold start)
│   ├── Extracts label text and style info
│   ├── Derives extents (API preferred, fallback estimator)
│   └── Maps Civil 3D offset semantics to internal rectangle-center convention
└── WriteOffsets(List<LabelState>)
    └── Writes label offsets only, never point coordinates
```

---

## Implementation Phases

| Phase | Description | Estimated Time |
|-------|-------------|----------------|
| 1 | `LabelState`, rect convention, safety margins | 1–2 hrs |
| 2 | `SpatialGrid` — insert, remove, update, neighbor query | 1–2 hrs |
| 3 | `GroupStats` — running variance, O(1) incremental updates | 1 hr |
| 4 | `EnergyDelta` — incremental overlap, displacement, group spread | 1–2 hrs |
| 5 | `SALoop` — warmup, move mix, cooling, best tracking, stagnation stop, two-stage weights | 2–4 hrs |
| 6 | Console test harness — stress tests, convergence diagnostics, print energy per 500 iterations | 2–4 hrs |
| 7 | Civil 3D wrapper — read label content, extents, anchor mapping, cold start reset | 2–4 hrs |
| 8 | Write offsets, real drawing validation, residual overlap reporting | 2–4 hrs |
| 9 | Civil 3D visual diagnostics (highlight worst offenders, zoom-to) | 1–2 hrs |
| 10 | WPF ribbon UI — run button, progress, config sliders, residual report | 2–4 hrs |
| **Total** | | **15–29 hrs** |

> **Phase 10 UI note:** Must follow the DAR design system. Full reference: `/mnt/c/Users/jjhaddad/Documents/Work/DAR_UI_STANDARDS.md`

Phase 9 is deliberately last — only after the core algorithm is validated in a real drawing.

---

## Expected Results

- **Sparse regions** — labels stay at or very near anchors, sub-label-width displacement
- **Dense clusters** — tight non-overlapping arrangement, labels spread primarily horizontally
- **Coincident labels** — naturally stay grouped, may spread slightly if needed, never scatter arbitrarily
- **Pathologically dense areas** — overlap minimized as much as physically possible, residual reported
- **Re-runs** — fully reproducible, cold start every time
- Overall layout reads naturally to a human draftsman

---

## Configurable Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `alpha` | 1000 | Overlap penalty weight |
| `beta_x` | 1 | Horizontal displacement penalty |
| `beta_y` | 10 | Vertical displacement penalty |
| `gamma` | 100 | Intragroup spread penalty |
| `coolingRate` | 0.9995 | Geometric cooling multiplier |
| `minTemp` | 0.01 | Termination threshold |
| `stagnationIterations` | 20000 | Stop if no best improvement for this many iterations |
| `maxVerticalDisplacement` | 2 × labelHeight | Absolute Y offset clamp |
| `lineSpacingFactor` | 1.2 | Label height line multiplier |
| `charWidthFactor` | 0.6 | Per-character width multiplier |
| `sizeSafetyFactorX` | 1.10 | Width safety margin |
| `sizeSafetyFactorY` | 1.15 | Height safety margin |
| `coincidenceFactor` | 0.1 | Tolerance = factor × medianLabelHeight |
| `moveMix` | weighted | Relative weights of random / directional / swap move types |
