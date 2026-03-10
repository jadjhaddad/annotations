# LabelPlacer — Code Review Checklist

> **Purpose:** COGO point label deconfliction plugin for Civil 3D.
> Given N survey anchor points, it automatically repositions their labels so they don't overlap each other, their leaders, or the anchor markers, while keeping leaders as short as possible.

---

## Repository Layout

```
Annotations/
├── LabelPlacer.Civil3D/              # Civil 3D plugin (net48, x64)
│   ├── CogoLabelArrangerCommand.cs   # All three commands + full algorithm
│   ├── PackageContents.xml           # .bundle manifest (targets Civil 3D R24.0+)
│   └── LabelPlacer.Civil3D.csproj
└── Annotations.sln
```

---

## Section 1 — Commands

Three commands registered via `[CommandMethod]`:

| Command | Behaviour |
|---------|-----------|
| `ArrangeCogoLabels` | Prompts `[Selection/All]`, then calls `StackLabels` |
| `ArrangeCogoSelected` | Selection-only shortcut, no prompt |
| `ArrangeCogoAll` | All points in document, no prompt |

- [ ] `CollectSelection` — uses `SelectionFilter` with DXF type `AECC_COGO_POINT`.
- [ ] `CollectAllPoints` — enumerates `CivilApplication.ActiveDocument.CogoPoints` inside a read transaction.

---

## Section 2 — `StackLabels` Core Algorithm

Six phases. Progress is written to the Civil 3D command line via `ed.WriteMessage`.

### Phase 0 — Timing / Logging

```csharp
Stopwatch sw = ...
void Tick(string phase) {
    ed.WriteMessage($"  [{sw.Elapsed:mm\\:ss\\.f}] {phase}\n");
}
```

---

### Phase 1 — Read Anchor Positions

Single read-only transaction collects `(ObjectId, X, Y)` for every point.

- [ ] Only `pt.Location` is read — `LabelLocation` intentionally ignored (cold start every run).
- [ ] All 13,791 points read in ~0.2 s in testing.

---

### Phase 2 — Median Nearest-Neighbour Distance (`medianNN`)

Used as a spatial scale proxy throughout (co-location threshold, `maxDisp` budget).

- [ ] **Algorithm:** sort by X, scan right-neighbours with sliding window until `dx² ≥ bestDist²`.
- [ ] **Sampling:** every `max(1, n/500)`-th point — O(n log n) with ≤ 500 samples.
- [ ] **Fallback:** if `medianNN < 1e-6` (degenerate dataset), estimate from coordinate magnitude.

---

### Phase 3 — Co-location Grouping (Union-Find)

Points within `medianNN × 0.1` are merged into one `LabelBlock` (stacked entity).

- [ ] **Threshold:** `0.1 × medianNN` ≈ 0.84 drawing units for the test dataset.
- [ ] **Algorithm:** O(n log n) sliding window on X-sorted array + path-compressed union-find.
- [ ] **Block centroidY** = mean anchor Y. **Block AnchorX** = rightmost anchor X.
- [ ] Members sorted by Y within each group so labels stack bottom→top in anchor order.

---

### Phase 4 — Greedy Y Deconfliction

**Spatial bucket index** (replaces O(b²) inner loop):
- [ ] Bucket width = `labelW + anchorGap + AnchorMarkerSize` ≈ 11.55 units.
- [ ] Each block stored by `floor(AnchorX / bucketW)`.
- [ ] Query checks 3 buckets covering `[xL − bucketW, xR]` — typically returns 10–50 candidates.

**Processing order — middle-out by LabelX:**
- [ ] Blocks sorted ascending by `LabelX`, processed from median index outward.
- [ ] Gives the X-central (densest) area first priority; outer blocks adapt.
  ⚠️ *Known limitation: in a local cluster the rightmost block is processed last and can cascade far. See [§ Known Issues](#section-4--known-issues).*

**Pre-placed anchor obstacles:**
- [ ] One `LabelBlock` with `Members.Count == 0` per group, centred on `AnchorX/Y`, size = `AnchorMarkerSize`.
- [ ] Own-anchor obstacle skipped when building `xConflicts` — allows a block to sit at its anchor's Y without fighting itself.

**X conflict detection:**
- [ ] Block's X range: `[AnchorX, LabelX + LabelW]` — covers the full leader-to-text span.
- [ ] Placed block's X range: `[Min(AnchorX, LabelX), Max(AnchorX, LabelX + LabelW)]`.

**`FindClearY` — 4-pass Y search:**
- [ ] **Occupied intervals** — two separate obstacles per placed block:
  1. Label text: `[LabelY, LabelY + BlockH]`
  2. Anchor marker: `[AnchorY − mr, AnchorY + mr]`
  *(Keeping these separate prevents a displaced label from falsely blocking the gap between it and its anchor.)*
- [ ] **Candidates** — boundary positions just above/below each interval, sorted by distance from `startY`.
- [ ] **Pass 1** — within `maxDisp`, text clear AND leader corridor clear *(ideal)*
- [ ] **Pass 2** — within `maxDisp`, text clear only *(leader may cross)*
- [ ] **Pass 3** — unlimited, text + leader clear *(long leader, no text overlap)*
- [ ] **Pass 4** — unlimited, text clear only *(guaranteed no text-on-text)*
- [ ] **`maxDisp`** = `max(2 × medianNN, 1.5 × blockH)` ≈ 16.7 units for test dataset.

**Leader corridor check (`LeaderClear`):**
- [ ] Corridor = horizontal band `[anchorX, labelX]` × `[min(anchorY, cand), max(anchorY, cand + blockH)]`.
- [ ] Returns `false` if any already-placed label's text rectangle intersects this corridor.

---

### Phase 5 — Bulk Write

- [ ] **`DisableUndoRecording(true)`** — eliminates per-write journal overhead.
- [ ] **`UpdateExt(false)`** — suppresses bounding-box recalculation during the loop.
- [ ] **Single transaction** for all writes — one commit for 13,791 labels.
- [ ] Progress tick every 500 labels (log only — no transaction boundary).
- [ ] `UpdateExt(true)` and `DisableUndoRecording(false)` restored in `finally` block.
- [ ] `Application.UpdateScreen()` + `doc.Editor.Regen()` after transaction.

---

## Section 3 — Hardcoded Label Dimensions

```csharp
const double LabelW           = 7.8;   // drawing units — measured with DIST
const double LabelH           = 1.7;   // drawing units — measured with DIST
const double AnchorMarkerSize = 1.5;   // anchor marker diameter
double anchorGap  = AnchorMarkerSize * 1.5;   // = 2.25 units
double rowSpacing = labelH * 1.1;             // = 1.87 units per row
```

- [ ] ⚠️ **These must be re-measured with `DIST` in Civil 3D if the label style or annotation scale changes.** The algorithm will produce wrong results silently if these are stale.

---

## Section 4 — `.bundle` Deployment

- [ ] `PackageContents.xml` — targets `Platform="Civil3D"`, `SeriesMin="R24.0"` `SeriesMax="R26.0"` (Civil 3D 2024–2026). `RuntimeRequirements` declared at both the package and component level (required by the Autoloader). Declares all three commands so the DLL is demand-loaded on first invocation.
- [ ] Post-build target (`DeployBundle`) does two things:
  1. Assembles `bin\Debug\LabelPlacer.bundle\` — copy this folder to any machine's `%APPDATA%\Autodesk\ApplicationPlugins\` to deploy.
  2. Auto-copies it to the local `%APPDATA%` path so Civil 3D picks it up on next launch.
- [ ] Civil 3D auto-loads the bundle on next launch — no `NETLOAD` required.
- [ ] `*.bundle/` and `**/bin/` are in `.gitignore` — build artifacts are not source-controlled.

---

## Section 5 — Known Issues

- [ ] **Cascade displacement in dense clusters** — when 4+ anchors are close together and the rightmost block is processed last (middle-out order), earlier blocks can cascade downward into the space the last block needs, producing a long leader. The interval-split fix mitigates this but doesn't eliminate it.
  *Root cause: greedy algorithms can't look ahead.*

- [ ] **Leader-through-label collisions** — Pass 2/4 of `FindClearY` accept placements where the leader crosses another label's text. This happens when no leader-clean slot exists within `maxDisp`.

- [ ] **Leader-through-anchor collisions** — the anchor marker obstacle has a fixed size (`AnchorMarkerSize`), but Civil 3D renders a visual marker that may be slightly larger. A small tolerance bump on `mr` could reduce visual overlap.

- [ ] **Hardcoded dimensions** — `LabelW`, `LabelH`, `AnchorMarkerSize` must be manually updated if label style or scale changes.

---

## Section 6 — Build & Deployment Checklist

- [ ] Close Civil 3D before building (DLL will be locked otherwise → MSB3027).
- [ ] Build: `source ~/.bashrc && vs rcb "$(wslpath -w path/to/Annotations.sln)"`
- [ ] Bundle auto-deployed to `%APPDATA%\Autodesk\ApplicationPlugins\LabelPlacer.bundle\` on every build.
- [ ] Monitor progress in the Civil 3D command line during a run.
- [ ] Expected timings on 13,791 points:
  - Read: ~0.2 s
  - medianNN + co-location: ~0.3 s
  - Deconfliction: ~0.5 s
  - Write: ~5–15 s (single transaction, undo disabled)

---

## Section 7 — Future Improvements (Prioritised)

1. **Dynamic label sizing** — call Civil 3D `label.GetBoundingBox()` or scale from `CANNOSCALEVALUE` instead of hardcoded constants.
2. **Cluster-aware processing order** — process blocks within a dense local cluster together (e.g., sort by anchor Y within cluster) to distribute cascade displacement more evenly.
3. **Undo support** — currently undo stack is wiped. Consider grouping writes under a single undo mark (`UNDO BE` / `UNDO E`) if users need Ctrl+Z.
4. **Ribbon button** — currently invoked via command line. Add a `.cuix` ribbon panel for the three commands.
5. **Multi-style support** — different label styles have different widths/heights. Parameterise per style or read from API.
