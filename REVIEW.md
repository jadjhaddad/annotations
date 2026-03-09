# LabelPlacer — Code Review Checklist

> **Purpose:** COGO point label deconfliction plugin for Civil 3D.
> Given N survey anchor points, it automatically repositions their labels so they don't overlap each other, their leaders, or the anchor markers, while keeping leaders as short as possible.

---

## Repository Layout

```
Annotations/
├── LabelPlacer/               # Platform-agnostic engine (net48, x64)
│   ├── LabelState.cs          # Core geometry types (Point2D, Rect2D, LabelState)
│   ├── PlacerConfig.cs        # All tunable SA parameters
│   ├── CandidateGenerator.cs  # Discrete placement candidates per label
│   ├── GreedyPlacer.cs        # Fast greedy initial placement + refinement sweeps
│   ├── SALoop.cs              # Simulated-annealing optimizer (unused in Civil 3D path)
│   ├── EnergyDelta.cs         # Incremental energy / overlap diagnostics
│   ├── SpatialGrid.cs         # 2-D hash grid for O(1) neighbour queries
│   └── GroupStats.cs          # Coincident-group analysis helpers
│
├── LabelPlacer.Civil3D/       # Civil 3D host wrapper (net48, x64)
│   ├── CogoLabelArrangerCommand.cs  # All three Civil 3D commands + core algorithm
│   ├── PackageContents.xml    # .bundle manifest (targets Civil 3D R24.0+)
│   └── LabelPlacer.Civil3D.csproj
│
├── LabelPlacer.TestHarness/   # Console harness for offline testing
└── Annotations.sln
```

**Two separate algorithms exist** — the `LabelPlacer` library contains a full SA optimizer that is *not* currently used by the Civil 3D plugin. The Civil 3D plugin uses its own self-contained greedy algorithm inside `CogoLabelArrangerCommand.cs`. See [§ Architecture Decision](#architecture-decision) below.

---

## Section 1 — LabelPlacer Library (Platform-Agnostic Engine)

This library was built first as a general-purpose solver. It is compiled and referenced by the Civil 3D project but **its `GreedyPlacer` and `SALoop` entry points are not called** by the current Civil 3D workflow.

### 1.1 `LabelState.cs` — Core Geometry Types

- [ ] **`Point2D` / `Vector2D` / `Rect2D`** — simple immutable value types. `Rect2D` uses bottom-left origin, Y-up convention (matches Civil 3D drawing coordinates).
- [ ] **`LabelState`** — mutable state for one label during optimisation. Key fields:
  - `Anchor` — survey point XY, never modified
  - `CurrentOffset` — vector from anchor to label top-left; written by the optimiser
  - `Width` / `Height` — label bounding box (with safety margins applied)
  - `GroupId` — co-location group index (-1 = solo)
- [ ] **`LabelState.GetBoundingRect()`** — computes AABB from `Anchor + CurrentOffset`. Convention: anchor is at the **centre of the left edge** of the label.
- [ ] **`LabelState.EstimateSize()`** — fallback when Civil 3D API extents are unavailable. Uses `charWidthFactor × textHeight × lineCount`.
  ⚠️ *Not currently called by the Civil 3D plugin — dimensions are hardcoded instead.*

### 1.2 `PlacerConfig.cs` — SA Parameters

All weights and schedule constants live here. Nothing is hardcoded in the algorithm.

- [ ] **Energy weights (Stage A — overlap clearing)**
  - `Alpha = 1000` — overlap area penalty. Must dominate all other terms.
  - `Gamma = 100` — intragroup spread penalty (keeps co-located stack together).
  - `BetaY = 10` — vertical displacement penalty.
  - `BetaX = 1` — horizontal displacement penalty (small — horizontal spread is acceptable).
  - `LeaderLabelPenaltyWeight = 200` — per leader-through-label intersection.
  - `LeaderLeaderPenaltyWeight = 500` — per leader-through-leader crossing.
- [ ] **Energy weights (Stage B — beautify)**
  Stage B kicks in once overlap reaches zero. Weights are rebalanced to pull labels back toward anchors.
  - `BetaYStageB = 200` — much higher than Stage A to aggressively shorten leaders.
  - `LeaderLabelPenaltyWeightStageB = 6000`
  - `LeaderLeaderPenaltyWeightStageB = 20000`
- [ ] **Cooling schedule** — `CoolingRate = 0.9995`, `MinTemp = 0.01`. Geometric cooling.
- [ ] **`StageBReheatFraction = 0.01`** — reheats temperature to 1% of T₀ on Stage B entry, giving it energy to pull labels back under the higher BetaY.
- [ ] **`MaxVerticalDisplacementFactor = 6.0`** — hard clamp: label can move at most 6× its height vertically.
- [ ] **`CoincidenceFactor = 0.5`** — anchors within `0.5 × medianLabelHeight` are co-located.

### 1.3 `CandidateGenerator.cs` — Discrete Placement Candidates

Generates 21 candidate offsets per label in preference order (lower index = preferred):

- Right band (dx=0): 9 candidates, vertically spaced `1.05 × height` apart
- Left band (dx≈−W): 9 candidates, label entirely left of anchor
- Far-right band (dx≈+W/2): 3 candidates

`preferLeft` flag swaps Right ↔ Left ordering. Used by `GreedyPlacer` to bias clusters away from neighbours.

- [ ] **Vertical step** = `h × 1.05` — adjacent levels never overlap.
- [ ] **Left band offset** = `−(W + 0.05W)` — small horizontal gap between anchor and label edge.
- [ ] **Far-right band** — emergency overflow; only 3 candidates (rarely needed).

### 1.4 `GreedyPlacer.cs` — Fast Initial Placement

Two-phase approach:

**Phase 1 — Initial greedy pass**
- [ ] Processes labels in **descending constraint order** (most-neighbours-first gets priority).
- [ ] Scores each candidate: `overlap × 1e6 + leaderCrosses × 1000 + sideCost + candidateIndex`.
- [ ] Uses `SpatialGrid` for O(1) neighbour lookup during overlap and leader-cross counting.
- [ ] `WrongSidePenalty = 200` — applied when a candidate is on the non-preferred side but never forces an overlap to occur.

**Phase 2 — Refinement sweeps (up to 8)**
- [ ] For each label, tries all candidates with the **full two-way** leader score (outgoing + incoming crosses).
- [ ] Moves to best if it improves. Repeats until no improvement or max sweeps reached.
- [ ] Typically converges in 2–4 sweeps.

**`ComputePreferLeft`** (cluster-based bias):
- [ ] Groups labels into clusters via union-find (`clusterRadius = 3 × medianLabelHeight`).
- [ ] For each cluster, counts external neighbours to left/right within `6 × medianLabelHeight`.
- [ ] All labels in a cluster share the same `preferLeft` flag — consistent side across the group.
  ⚠️ *Currently O(n²) — fine for small batches, could be slow for 10k+ labels.*

### 1.5 `SALoop.cs` — Simulated Annealing Optimizer

Full SA implementation — **not currently invoked by the Civil 3D plugin**.

- [ ] **Warmup** — samples 500 random moves to calibrate T₀ for ~80% initial acceptance.
- [ ] **Three move types** (weighted mix 60/30/10):
  - Random small perturbation
  - Directional nudge (away from worst overlapping neighbour)
  - Swap within co-located group
- [ ] **Two-stage cooling** — Stage A clears overlaps, Stage B beautifies (shorter leaders, no crossings).
- [ ] **Stagnation stop** — exits after 20,000 iterations without improvement.
- [ ] **Best-state tracking** — saves best offsets throughout; restores at end.

### 1.6 `EnergyDelta.cs` — Energy and Diagnostics

- [ ] `OverlapDiagnostics()` — returns total overlap area, pair count, and max pairwise overlap. Used to decide Stage A → B transition.
- [ ] Incremental delta computation (not full recompute) for performance in SA inner loop.

### 1.7 `SpatialGrid.cs` — 2-D Hash Grid

- [ ] Uniform grid with cell size auto-selected from label dimensions.
- [ ] `Insert` / `QueryNeighbors` — O(1) average for spatially localised queries.
- [ ] Used by `GreedyPlacer` for overlap and leader-cross counting in the initial pass.

### 1.8 `GroupStats.cs` — Coincident Group Analysis

- [ ] Collects per-group statistics: anchor spread, label count, estimated stack height.
- [ ] Used to validate co-location grouping logic in the test harness.

---

## Section 2 — LabelPlacer.Civil3D Plugin

This is the active Civil 3D plugin. It contains a **fully self-contained greedy algorithm** that does not call anything from the `LabelPlacer` library at runtime (though the DLL is referenced and shipped).

### 2.1 Civil 3D Commands

Three commands are registered via `[CommandMethod]`:

| Command | Behaviour |
|---------|-----------|
| `ArrangeCogoLabels` | Prompts `[Selection/All]`, then calls `StackLabels` |
| `ArrangeCogoSelected` | Selection-only shortcut, no prompt |
| `ArrangeCogoAll` | All points in document, no prompt |

- [ ] `CollectSelection` — uses `SelectionFilter` with DXF type `AECC_COGO_POINT`.
- [ ] `CollectAllPoints` — enumerates `CivilApplication.ActiveDocument.CogoPoints`.

### 2.2 `StackLabels` — The Core Algorithm

Six phases, all in one method. Runtime log is written to `%USERPROFILE%\Desktop\LabelPlacer.log`.

---

#### Phase 0 — Timing / Logging

```csharp
Stopwatch sw = ...
void Tick(string phase) {
    File.AppendAllText(logPath, ...);   // real-time log — ed.WriteMessage is buffered
    ed.WriteMessage(...);
}
```

- [ ] Log path: `%USERPROFILE%\Desktop\LabelPlacer.log`. Remove or make configurable before final release.
- [ ] `ed.WriteMessage` is buffered during synchronous AutoCAD commands — file log is the only real-time visibility during a run.

---

#### Phase 1 — Read Anchor Positions

Single read-only transaction collects `(ObjectId, X, Y)` for every point.

- [ ] Only `pt.Location` is read — `LabelLocation` is intentionally ignored (cold start every run).
- [ ] All 13,791 points read in ~0.2 s in testing.

---

#### Phase 2 — Median Nearest-Neighbour Distance (`medianNN`)

Used as a spatial scale proxy throughout the algorithm (co-location threshold, maxDisp budget).

- [ ] **Algorithm:** sort by X, scan right-neighbours with sliding window until `dx² ≥ bestDist²`.
- [ ] **Sampling:** every `max(1, n/500)`-th point — O(n log n) with ≤500 samples.
- [ ] **Fallback:** if `medianNN < 1e-6` (degenerate dataset), estimate from coordinate magnitude.

---

#### Phase 3 — Co-location Grouping (Union-Find)

Points within `medianNN × 0.1` of each other are merged into one `LabelBlock` (stacked entity).

- [ ] **Threshold:** `0.1 × medianNN` ≈ 0.84 drawing units for the test dataset.
  *Tried 0.25× — merged too aggressively. Current value is correct.*
- [ ] **Algorithm:** O(n log n) sliding window on X-sorted array + path-compressed union-find.
- [ ] **Block centroidY** = mean anchor Y. **Block AnchorX** = rightmost anchor X.
- [ ] Members sorted by Y within each group so labels stack bottom→top in anchor order.

---

#### Phase 4 — Greedy Y Deconfliction

This is the heart of the Civil 3D algorithm.

**Spatial bucket index** (replaces O(b²) inner loop):
- [ ] Bucket width = `labelW + anchorGap + AnchorMarkerSize` ≈ 11.55 units.
- [ ] Each block stored once by `floor(AnchorX / bucketW)`.
- [ ] Query checks 3 buckets covering `[xL − bucketW, xR]` — typically returns 10–50 candidates instead of 5,662.

**Processing order — middle-out by LabelX:**
- [ ] Blocks sorted ascending by `LabelX`, then processed from median index outward.
- [ ] Gives the X-central (densest) area first priority; outer blocks adapt.
  ⚠️ *Known limitation: in a local cluster, the rightmost block is processed last and can cascade far if others have filled nearby Y space. See [§ Known Issues](#known-issues).*

**Pre-placed anchor obstacles:**
- [ ] One `LabelBlock` with `Members.Count == 0` per group, centred on `AnchorX/Y`, size = `AnchorMarkerSize`.
- [ ] Own-anchor obstacle is **skipped** when building `xConflicts` for the block it belongs to — this allows a block to sit at its anchor's Y without fighting itself.

**X conflict detection:**
- [ ] Block's X range: `[AnchorX, LabelX + LabelW]` — covers the full leader-to-text span.
- [ ] Placed block's X range: `[Min(AnchorX, LabelX), Max(AnchorX, LabelX + LabelW)]` — uses `Math.Min/Max` to correctly handle both right-placed and future left-placed blocks.

**`FindClearY` — 4-pass Y search:**
- [ ] **Occupied intervals** — two separate obstacles per placed block:
  1. Label text: `[LabelY, LabelY + BlockH]`
  2. Anchor marker: `[AnchorY − mr, AnchorY + mr]`
  *Previously these were merged into one big interval, which falsely blocked the gap between a displaced label and its anchor.*
- [ ] **Candidates** — boundary positions: just above each interval (`hi`) and just below (`lo − blockH`), sorted by distance from `startY`.
- [ ] **Pass 1** — within `maxDisp`, text clear AND leader corridor clear *(ideal)*
- [ ] **Pass 2** — within `maxDisp`, text clear only *(leader may cross)*
- [ ] **Pass 3** — unlimited, text + leader clear *(long leader, no text overlap)*
- [ ] **Pass 4** — unlimited, text clear only *(guaranteed no text-on-text)*
- [ ] **`maxDisp`** = `max(2 × medianNN, 1.5 × blockH)` ≈ 16.7 units for test dataset.

**Leader corridor check (`LeaderClear`):**
- [ ] Corridor = horizontal band `[anchorX, labelX]` × `[min(anchorY, cand), max(anchorY, cand + blockH)]`.
- [ ] Returns `false` if any already-placed label's text rectangle intersects this corridor.

---

#### Phase 5 — Bulk Write

- [ ] **`DisableUndoRecording(true)`** — eliminates per-write journal overhead.
- [ ] **`UpdateExt(false)`** — suppresses bounding-box recalculation during the loop.
- [ ] **Single transaction** for all writes — one commit for 13,791 labels.
- [ ] Progress tick every 500 labels (log only — no transaction boundary).
- [ ] `UpdateExt(true)` and `DisableUndoRecording(false)` restored in `finally` block.
- [ ] `Application.UpdateScreen()` + `doc.Editor.Regen()` after transaction.

---

### 2.3 Hardcoded Label Dimensions

```csharp
const double LabelW           = 7.8;   // drawing units — measured with DIST
const double LabelH           = 1.7;   // drawing units — measured with DIST
const double AnchorMarkerSize = 1.5;   // anchor marker diameter
double anchorGap  = AnchorMarkerSize * 1.5;   // = 2.25 units
double rowSpacing = labelH * 1.1;             // = 1.87 units per row
```

- [ ] ⚠️ **These must be re-measured with `DIST` in Civil 3D if the label style or annotation scale changes.** The algorithm will produce wrong results silently if these are stale.
- [ ] The unused `GetAnnotationScale()` helper exists for future use when auto-scaling label dimensions.

---

### 2.4 `.bundle` Deployment

- [ ] `PackageContents.xml` — targets `Platform="Civil3D"`, `SeriesMin="R24.0"`.
- [ ] Post-build MSBuild target (`DeployBundle`) copies DLLs to `%APPDATA%\Autodesk\ApplicationPlugins\LabelPlacer.bundle\Contents\`.
- [ ] Civil 3D auto-loads the bundle on next launch — no `NETLOAD` required.
- [ ] `*.bundle/` is in `.gitignore` — bundle folder is user-local, not source-controlled.

---

## Section 3 — Architecture Decision

The `LabelPlacer` library's `GreedyPlacer` + `SALoop` are **not wired up** in the Civil 3D path. There are two separate implementations:

| | LabelPlacer library | Civil 3D plugin |
|--|--|--|
| Algorithm | Discrete candidates + SA | Continuous Y search (greedy) |
| Coordinates | Offset-from-anchor | Absolute drawing units |
| Label dimensions | From `LabelState` (API or estimated) | Hardcoded constants |
| Leader model | Segment intersection test | Rectangle corridor test |
| Group handling | Co-location via `CoincidenceFactor` | `medianNN × 0.1` union-find |

- [ ] **Review whether the library's `GreedyPlacer` could replace or supplement the Civil 3D algorithm** — it has a more sophisticated scoring model (two-way leader crossing, cluster-aware side preference). The blocker is that it works with `LabelState` offset coordinates, not Civil 3D `LabelLocation` absolute coordinates.

---

## Section 4 — Known Issues

- [ ] **Cascade displacement in dense clusters** — when 4+ anchors are close together and the rightmost block is processed last (middle-out order), earlier blocks can cascade downward into the space the last block needs, producing a long leader. The interval-split fix (Phase 4) mitigates this but doesn't eliminate it.
  *Root cause: greedy algorithms can't look ahead. A two-pass or SA approach would solve it.*

- [ ] **Leader-through-label collisions** — Pass 2/4 of `FindClearY` accept placements where the leader crosses another label's text. This happens when no leader-clean slot exists within `maxDisp`. The 4-pass hierarchy minimises it but can't always avoid it.

- [ ] **Leader-through-anchor collisions** — the anchor marker obstacle in the index has a fixed size (`AnchorMarkerSize`), but Civil 3D renders a visual marker that may be slightly larger. A small tolerance bump on `mr` could reduce visual overlap.

- [ ] **Hardcoded dimensions** — `LabelW`, `LabelH`, `AnchorMarkerSize` must be manually updated if label style or scale changes.

- [ ] **`GetAnnotationScale()` is unused** — written but never called. Either wire it up to scale dimensions dynamically or remove it.

- [ ] **`ArrangeCogoAll` has duplicate logging setup** — writes a header line before `StackLabels` rewrites the log. Minor duplication.

- [ ] **Log file always on Desktop** — fine for development, should be made configurable or removed for production use.

- [ ] **`GreedyPlacer.ComputePreferLeft` is O(n²)** — fine for small batches (< 1k labels), could time out for 13k+ if called. Not currently invoked from Civil 3D.

---

## Section 5 — Build & Deployment Checklist

- [ ] Close Civil 3D before building (DLL will be locked otherwise → MSB3027).
- [ ] Build command: `source ~/.bashrc && vs rcb Annotations.sln`
- [ ] Bundle is auto-deployed to `%APPDATA%\Autodesk\ApplicationPlugins\LabelPlacer.bundle\` on every build.
- [ ] Check `%USERPROFILE%\Desktop\LabelPlacer.log` after each run to verify all phases completed.
- [ ] Expected timings on 13,791 points:
  - Read: ~0.2 s
  - medianNN + co-location: ~0.3 s
  - Deconfliction: ~0.5 s
  - Write: ~5–15 s (single transaction, undo disabled)

---

## Section 6 — Future Improvements (Prioritised)

1. **Dynamic label sizing** — call Civil 3D `label.GetBoundingBox()` or scale from `CANNOSCALEVALUE` instead of hardcoded constants.
2. **Cluster-aware processing order** — process blocks within a dense local cluster together (e.g., sort by anchor Y within cluster) to distribute cascade displacement more evenly.
3. **Wire up `GreedyPlacer`** from the library as a pre-pass before the Y-deconfliction, leveraging its cluster-side bias logic.
4. **Undo support** — currently undo stack is wiped. Consider grouping writes under a single undo mark (`UNDO BE` / `UNDO E`) if users need Ctrl+Z.
5. **Ribbon button** — currently invoked via command line. Add a `.cuix` ribbon panel for the three commands.
6. **Multi-style support** — different label styles have different widths/heights. Parameterise per style or read from API.
