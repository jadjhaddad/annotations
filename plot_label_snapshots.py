#!/usr/bin/env python3
"""Plot LabelPlacer snapshots (anchors + label rectangles) from a CSV.

This script reads the snapshot CSV produced by LabelPlacer.TestHarness
(`--snapshot-csv`) and writes one SVG per snapshot (scenario/kind/iteration).

No third-party dependencies.
"""

from __future__ import annotations

import argparse
import csv
import math
import os
from dataclasses import dataclass
from typing import Dict, Iterable, List, Optional, Tuple


@dataclass(frozen=True)
class Row:
    scenario: str
    kind: str
    iteration: int
    label_index: int
    group_id: int
    anchor_x: float
    anchor_y: float
    offset_x: float
    offset_y: float
    width: float
    height: float
    left: float
    bottom: float
    right: float
    top: float
    handle: str

    @property
    def center_x(self) -> float:
        return (self.left + self.right) * 0.5

    @property
    def center_y(self) -> float:
        return (self.bottom + self.top) * 0.5


def _xml_escape(s: str) -> str:
    return (
        s.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
        .replace('"', "&quot;")
        .replace("'", "&apos;")
    )


def _safe_filename(s: str) -> str:
    out = []
    for ch in s:
        if ch.isalnum() or ch in ("-", "_", "."):
            out.append(ch)
        else:
            out.append("_")
    return "".join(out).strip("_") or "snapshot"


def read_snapshots(csv_path: str) -> Dict[Tuple[str, str, int], List[Row]]:
    snapshots: Dict[Tuple[str, str, int], List[Row]] = {}
    with open(csv_path, "r", newline="") as f:
        reader = csv.DictReader(f)
        required = {
            "scenario",
            "kind",
            "iteration",
            "labelIndex",
            "groupId",
            "anchorX",
            "anchorY",
            "offsetX",
            "offsetY",
            "width",
            "height",
            "left",
            "bottom",
            "right",
            "top",
            "handle",
        }
        missing = required - set(reader.fieldnames or [])
        if missing:
            raise SystemExit(f"CSV is missing columns: {sorted(missing)}")

        for raw in reader:
            r = Row(
                scenario=raw["scenario"],
                kind=raw["kind"],
                iteration=int(raw["iteration"]),
                label_index=int(raw["labelIndex"]),
                group_id=int(raw["groupId"]),
                anchor_x=float(raw["anchorX"]),
                anchor_y=float(raw["anchorY"]),
                offset_x=float(raw["offsetX"]),
                offset_y=float(raw["offsetY"]),
                width=float(raw["width"]),
                height=float(raw["height"]),
                left=float(raw["left"]),
                bottom=float(raw["bottom"]),
                right=float(raw["right"]),
                top=float(raw["top"]),
                handle=raw.get("handle", ""),
            )
            key = (r.scenario, r.kind, r.iteration)
            snapshots.setdefault(key, []).append(r)
    return snapshots


def render_svg(
    rows: List[Row],
    title: str,
    out_path: str,
    max_dim_px: int = 1200,
    padding_frac: float = 0.05,
    label_mode: str = "auto",
    show_leaders: bool = True,
) -> None:
    if not rows:
        return

    # Determine bounds (anchors + rectangles).
    min_x = math.inf
    max_x = -math.inf
    min_y = math.inf
    max_y = -math.inf
    for r in rows:
        min_x = min(min_x, r.anchor_x, r.left)
        max_x = max(max_x, r.anchor_x, r.right)
        min_y = min(min_y, r.anchor_y, r.bottom)
        max_y = max(max_y, r.anchor_y, r.top)

    dx = max_x - min_x
    dy = max_y - min_y
    if dx <= 0:
        dx = 1.0
        min_x -= 0.5
        max_x += 0.5
    if dy <= 0:
        dy = 1.0
        min_y -= 0.5
        max_y += 0.5

    pad = max(dx, dy) * max(0.0, padding_frac)
    min_x -= pad
    max_x += pad
    min_y -= pad
    max_y += pad
    dx = max_x - min_x
    dy = max_y - min_y

    # Canvas sizing.
    max_dim_px = max(200, int(max_dim_px))
    if dx >= dy:
        width_px = max_dim_px
        height_px = max(200, int(round(max_dim_px * (dy / dx))))
    else:
        height_px = max_dim_px
        width_px = max(200, int(round(max_dim_px * (dx / dy))))

    margin = 24
    sx = (width_px - 2 * margin) / dx
    sy = (height_px - 2 * margin) / dy
    scale = min(sx, sy)

    def map_x(x: float) -> float:
        return margin + (x - min_x) * scale

    def map_y(y: float) -> float:
        # Invert Y so world-up becomes screen-up.
        return margin + (max_y - y) * scale

    # Label defaults.
    if label_mode == "auto":
        label_mode = "idx" if len(rows) <= 100 else "none"
    if label_mode not in ("none", "idx", "handle"):
        raise SystemExit("--label must be one of: auto, none, idx, handle")

    # Precompute draw primitives.
    lines: List[str] = []
    rects: List[str] = []
    anchors: List[str] = []
    texts: List[str] = []

    # Simple, consistent style.
    rect_fill = "#1e90ff"
    rect_fill_opacity = "0.07"
    rect_stroke = "#0b3d91"
    rect_stroke_opacity = "0.35"

    for r in sorted(rows, key=lambda x: x.label_index):
        ax = map_x(r.anchor_x)
        ay = map_y(r.anchor_y)
        cx = map_x(r.center_x)
        cy = map_y(r.center_y)

        if show_leaders:
            lines.append(
                f'<line x1="{ax:.2f}" y1="{ay:.2f}" x2="{cx:.2f}" y2="{cy:.2f}" '
                'stroke="#6b7280" stroke-opacity="0.35" stroke-width="1" />'
            )

        # Rect: SVG y is top-left.
        x = map_x(r.left)
        y = map_y(r.top)
        w = (r.right - r.left) * scale
        h = (r.top - r.bottom) * scale
        rects.append(
            f'<rect x="{x:.2f}" y="{y:.2f}" width="{w:.2f}" height="{h:.2f}" '
            f'fill="{rect_fill}" fill-opacity="{rect_fill_opacity}" '
            f'stroke="{rect_stroke}" stroke-opacity="{rect_stroke_opacity}" stroke-width="1" />'
        )

        anchors.append(
            f'<circle cx="{ax:.2f}" cy="{ay:.2f}" r="2.0" fill="#111827" fill-opacity="0.85" />'
        )

        if label_mode != "none":
            if label_mode == "idx":
                txt = str(r.label_index)
            else:
                # handle may contain escaped newlines (\\n). Show first line only.
                h0 = r.handle.split("\\n", 1)[0]
                txt = h0
                if len(txt) > 18:
                    txt = txt[:18] + "..."

            texts.append(
                f'<text x="{cx + 2:.2f}" y="{cy - 2:.2f}" font-size="10" '
                'font-family="ui-monospace, SFMono-Regular, Menlo, Consolas, monospace" '
                'fill="#111827" fill-opacity="0.75">'
                f"{_xml_escape(txt)}</text>"
            )

    os.makedirs(os.path.dirname(out_path) or ".", exist_ok=True)

    title_txt = _xml_escape(title)
    svg = []
    svg.append(
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{width_px}" height="{height_px}" '
        f'viewBox="0 0 {width_px} {height_px}">'
    )
    svg.append('<rect x="0" y="0" width="100%" height="100%" fill="#fbfbfd" />')
    svg.append(
        '<text x="16" y="22" font-size="14" '
        'font-family="ui-monospace, SFMono-Regular, Menlo, Consolas, monospace" '
        'fill="#111827" fill-opacity="0.9">' + title_txt + "</text>"
    )

    svg.append('<g id="leaders">' + "".join(lines) + "</g>")
    svg.append('<g id="rects">' + "".join(rects) + "</g>")
    svg.append('<g id="anchors">' + "".join(anchors) + "</g>")
    if texts:
        svg.append('<g id="labels">' + "".join(texts) + "</g>")
    svg.append("</svg>")

    with open(out_path, "w", newline="") as f:
        f.write("\n".join(svg))


def main() -> None:
    ap = argparse.ArgumentParser(
        description="Plot LabelPlacer label rectangles from snapshot CSV"
    )
    ap.add_argument("csv", help="Path to snapshot CSV (from --snapshot-csv)")
    ap.add_argument("--out", default="plots", help="Output directory for SVG files")
    ap.add_argument(
        "--scenario", default="", help="Filter by scenario name (e.g. dense)"
    )
    ap.add_argument("--kind", default="", help="Filter by kind: start|iter|end")
    ap.add_argument(
        "--iteration", type=int, default=None, help="Filter by a specific iteration"
    )
    ap.add_argument(
        "--size", type=int, default=1200, help="Max SVG dimension in pixels"
    )
    ap.add_argument(
        "--padding", type=float, default=0.05, help="Padding fraction around bounds"
    )
    ap.add_argument("--label", default="auto", help="Label text: auto|none|idx|handle")
    ap.add_argument(
        "--no-leaders", action="store_true", help="Disable anchor->label leader lines"
    )
    args = ap.parse_args()

    snapshots = read_snapshots(args.csv)
    keys = sorted(snapshots.keys())

    def want(k: Tuple[str, str, int]) -> bool:
        scenario, kind, it = k
        if args.scenario and args.scenario.lower() not in scenario.lower():
            return False
        if args.kind and args.kind.lower() != kind.lower():
            return False
        if args.iteration is not None and args.iteration != it:
            return False
        return True

    selected = [k for k in keys if want(k)]
    if not selected:
        raise SystemExit("No snapshots matched filters")

    for scenario, kind, it in selected:
        rows = snapshots[(scenario, kind, it)]
        title = f"{scenario}  {kind}  iter={it}  n={len(rows)}"
        out_name = f"{_safe_filename(scenario)}_{_safe_filename(kind)}_{it:06d}.svg"
        out_path = os.path.join(args.out, out_name)
        render_svg(
            rows,
            title=title,
            out_path=out_path,
            max_dim_px=args.size,
            padding_frac=args.padding,
            label_mode=args.label,
            show_leaders=not args.no_leaders,
        )

    print(f"Wrote {len(selected)} SVG file(s) to: {args.out}")


if __name__ == "__main__":
    main()
