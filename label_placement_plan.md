# Label Placement Engine Plan (Inspired by Open Source)

This plan outlines a new placement engine that combines discrete candidate
positions, a greedy collision resolver, and a refinement pass. The goal is to
reduce overlap while keeping labels close to their anchors, with a predictable
fallback when dense cases cannot be solved perfectly.

References (inspiration):
- QGIS PAL engine: https://github.com/qgis/QGIS/tree/master/src/core/pal
- PAL labeling library: https://github.com/Pana/pal
- Mapbox GL JS placement: https://github.com/Mapbox/mapbox-gl-js/blob/main/src/symbol/placement.ts
- MapLibre GL JS placement: https://github.com/maplibre/maplibre-gl-js/blob/main/src/symbol/placement.ts
- Labelgun: https://github.com/Geovation/labelgun
- D3-Labeler: https://github.com/tinker10/D3-Labeler

## 1) Candidate Position Generation (PAL / Mapbox style)

Objective:
Create a small, discrete set of plausible label positions for each anchor.
These serve as inputs to a fast collision solver and provide deterministic
choices in dense cases.

Key ideas:
- Generate fixed positions around each anchor (e.g., N, NE, E, SE, S, SW, W, NW).
- Include a center/offset option if no leader lines are required.
- Scale offsets by label width/height so candidates are consistent across sizes.
- Allow per-layer or per-label rules (e.g., prefer right/above positions).

Implementation notes:
- Compute candidate rectangles by translating label bounds from the anchor.
- If leader lines are allowed, store the leader endpoint for each candidate.
- Optionally precompute a candidate score (distance from anchor, preferred side).

## 2) Greedy Collision Resolver with Priorities (Mapbox / Labelgun)

Objective:
Select one candidate per label with zero overlap when possible. This provides a
fast, deterministic solution that scales to large label sets.

Key ideas:
- Sort labels by priority (weight), higher first.
- For each label, test candidates in priority order and choose the first
  collision-free option.
- Use a spatial index (grid or R-tree) for fast overlap queries.
- If no candidate fits, either:
  - Hide/skip the label (Labelgun fallback), or
  - Accept the least-bad candidate by overlap area (optional).

Implementation notes:
- Build a spatial index of accepted label rectangles.
- For each candidate, query for overlapping rectangles and reject if any found.
- If all candidates collide, record as skipped or place with penalty.

## 3) Refinement Pass (D3-Labeler style simulated annealing)

Objective:
Improve aesthetics after greedy placement, while preserving non-overlap.
Refinement can reduce distance to anchors and improve leader geometry.

Key ideas:
- Run SA only on labels already placed by the greedy solver.
- Constrain movement within a small window around the chosen candidate.
- Reject moves that introduce overlaps (hard constraint) or use huge penalties.
- Energy terms:
  - Label-label overlap (hard or very large weight)
  - Label-anchor overlap
  - Distance from anchor (encourages pull-back)
  - Leader line intersections (optional)

Implementation notes:
- Use the current SA loop with tighter bounds and different weights.
- Keep a strict overlap check to preserve the greedy solution’s feasibility.

## 4) Engine Switch and Harness Updates

Objective:
Make the new engine selectable alongside the current SA-only approach.

Key ideas:
- Add a config flag for engine selection (SA-only vs hybrid).
- Extend test harness to run both engines for comparison.
- Generate comparison images for each scenario.

Implementation notes:
- Add a new engine entry point (e.g., HybridPlacer).
- Share data structures (LabelState, anchor data, bounds) between engines.

## 5) Civil 3D Integration Alignment

Objective:
Ensure the engine outputs map cleanly to Civil 3D label APIs.

Key ideas:
- Output should include final label rectangle, anchor position, and leader info.
- Provide a mode that prioritizes minimal leader length for CAD legibility.

Implementation notes:
- Keep engine as a pure geometry module for reuse.
- Add a small adapter that translates to Civil 3D dragged state and leader points.

## Suggested Implementation Order

1. Candidate generation and priority order.
2. Greedy collision resolver with spatial index.
3. Basic hybrid output in the harness.
4. Refinement pass with constrained SA.
5. Integrate with Civil 3D API adapter.
