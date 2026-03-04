"""
visualize.py — Plot start and end label placement states from a snapshot CSV.

Usage:
    python visualize.py snapshots.csv [scenario_filter]

    scenario_filter  Optional substring to restrict which scenarios are plotted.
                     E.g. "survey" plots only the survey scenario.

The CSV must have been produced by the test harness --csv flag.
Columns: scenario,kind,iteration,labelIndex,groupId,anchorX,anchorY,
         offsetX,offsetY,width,height,left,bottom,right,top,handle
"""

import sys
import csv
import math
import itertools
from collections import defaultdict
from pathlib import Path
import matplotlib
import matplotlib.pyplot as plt
import matplotlib.patches as mpatches
from matplotlib.patches import FancyArrowPatch


# ---------------------------------------------------------------------------
# Colours
# ---------------------------------------------------------------------------
ANCHOR_COLOR    = "#E63946"   # red dot for the anchor point
RECT_FILL_OK    = "#AED9E0"   # blue-ish fill when no overlap
RECT_FILL_OVLP  = "#FFBF69"   # orange fill when overlapping another rect
RECT_EDGE       = "#2C3E50"   # dark border
LEADER_COLOR    = "#7F8C8D"   # grey leader line from anchor to rect
TEXT_COLOR      = "#2C3E50"
BACKGROUND      = "#F9F9F9"


# ---------------------------------------------------------------------------
# Data loading
# ---------------------------------------------------------------------------

def load_csv(path):
    rows = []
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            rows.append({
                "scenario":   row["scenario"],
                "kind":       row["kind"],
                "iteration":  int(row["iteration"]),
                "labelIndex": int(row["labelIndex"]),
                "groupId":    int(row["groupId"]),
                "anchorX":    float(row["anchorX"]),
                "anchorY":    float(row["anchorY"]),
                "offsetX":    float(row["offsetX"]),
                "offsetY":    float(row["offsetY"]),
                "width":      float(row["width"]),
                "height":     float(row["height"]),
                "left":       float(row["left"]),
                "bottom":     float(row["bottom"]),
                "right":      float(row["right"]),
                "top":        float(row["top"]),
                "handle":     row["handle"],
            })
    return rows


def group_by_scenario_kind(rows, scenario_filter=""):
    # {scenario: {"start": [rows], "end": [rows]}}
    result = defaultdict(lambda: defaultdict(list))
    for row in rows:
        if scenario_filter and scenario_filter.lower() not in row["scenario"].lower():
            continue
        result[row["scenario"]][row["kind"]].append(row)
    return result


# ---------------------------------------------------------------------------
# Overlap detection (axis-aligned)
# ---------------------------------------------------------------------------

def compute_overlapping_indices(labels):
    """Return a set of labelIndex values that overlap at least one other label."""
    overlapping = set()
    n = len(labels)
    for i in range(n):
        a = labels[i]
        for j in range(i + 1, n):
            b = labels[j]
            ox = max(0.0, min(a["right"], b["right"]) - max(a["left"], b["left"]))
            oy = max(0.0, min(a["top"],   b["top"])   - max(a["bottom"], b["bottom"]))
            if ox * oy > 1e-9:
                overlapping.add(a["labelIndex"])
                overlapping.add(b["labelIndex"])
    return overlapping


# ---------------------------------------------------------------------------
# Single-panel drawing
# ---------------------------------------------------------------------------

def draw_state(ax, labels, title, show_handles=True):
    ax.set_facecolor(BACKGROUND)
    ax.set_title(title, fontsize=11, fontweight="bold", pad=8)
    ax.set_aspect("equal")
    ax.grid(True, color="#DDDDDD", linewidth=0.5, zorder=0)

    overlapping = compute_overlapping_indices(labels)

    # Sort so overlapping rects are drawn on top.
    sorted_labels = sorted(labels, key=lambda r: r["labelIndex"] in overlapping)

    for lbl in sorted_labels:
        ax_x    = lbl["anchorX"]
        ax_y    = lbl["anchorY"]
        left    = lbl["left"]
        bottom  = lbl["bottom"]
        w       = lbl["width"]
        h       = lbl["height"]
        cx      = left + w / 2
        cy      = bottom + h / 2
        lx      = left
        ly      = cy
        is_ovlp = lbl["labelIndex"] in overlapping

        fill = RECT_FILL_OVLP if is_ovlp else RECT_FILL_OK
        alpha = 0.80 if is_ovlp else 0.65

        # Anchor dot
        ax.plot(ax_x, ax_y, "o", color=ANCHOR_COLOR, markersize=4, zorder=5)

        # Leader line from anchor to left-edge center of the label box.
        if abs(lx - ax_x) > 0.01 or abs(ly - ax_y) > 0.01:
            ax.plot([ax_x, lx], [ax_y, ly],
                    color=LEADER_COLOR, linewidth=0.7, linestyle="--", zorder=3)

        # Label rectangle
        rect = mpatches.FancyBboxPatch(
            (left, bottom), w, h,
            boxstyle="round,pad=0.02",
            linewidth=0.8 if not is_ovlp else 1.5,
            edgecolor="#E63946" if is_ovlp else RECT_EDGE,
            facecolor=fill,
            alpha=alpha,
            zorder=4,
        )
        ax.add_patch(rect)

        # Handle text (truncated)
        if show_handles:
            short = lbl["handle"][:12]
            font_size = max(4.0, min(7.0, h * 2.5))
            ax.text(
                cx, cy, short,
                ha="center", va="center",
                fontsize=font_size,
                color=TEXT_COLOR,
                clip_on=True,
                zorder=6,
            )

    # Auto-scale with a small margin
    all_xs = [l["left"] for l in labels] + [l["right"] for l in labels] + [l["anchorX"] for l in labels]
    all_ys = [l["bottom"] for l in labels] + [l["top"] for l in labels] + [l["anchorY"] for l in labels]
    if all_xs and all_ys:
        xmin, xmax = min(all_xs), max(all_xs)
        ymin, ymax = min(all_ys), max(all_ys)
        margin_x = max((xmax - xmin) * 0.08, 1.0)
        margin_y = max((ymax - ymin) * 0.08, 1.0)
        ax.set_xlim(xmin - margin_x, xmax + margin_x)
        ax.set_ylim(ymin - margin_y, ymax + margin_y)

    n_ovlp = len(overlapping)
    n_total = len(labels)
    info = f"{n_total} labels  |  {n_ovlp} overlapping"
    ax.text(
        0.01, 0.01, info,
        transform=ax.transAxes,
        fontsize=7, color="#555555",
        verticalalignment="bottom",
    )


# ---------------------------------------------------------------------------
# Per-scenario figure
# ---------------------------------------------------------------------------

def plot_scenario(scenario, start_rows, end_rows, out_dir):
    fig, axes = plt.subplots(1, 2, figsize=(16, 9))
    fig.patch.set_facecolor(BACKGROUND)
    fig.suptitle(f"Scenario: {scenario}", fontsize=14, fontweight="bold", y=0.98)

    draw_state(axes[0], start_rows, "Before (offset = 0)")
    draw_state(axes[1], end_rows,   "After SA optimisation")

    # Legend
    ok_patch   = mpatches.Patch(facecolor=RECT_FILL_OK,   edgecolor=RECT_EDGE,   label="No overlap")
    ovl_patch  = mpatches.Patch(facecolor=RECT_FILL_OVLP, edgecolor="#E63946",   label="Overlapping")
    anc_handle = plt.Line2D([0], [0], marker="o", color="w",
                            markerfacecolor=ANCHOR_COLOR, markersize=6, label="Anchor")
    fig.legend(handles=[ok_patch, ovl_patch, anc_handle],
               loc="lower center", ncol=3, fontsize=9,
               framealpha=0.9, bbox_to_anchor=(0.5, 0.0))

    plt.tight_layout(rect=[0, 0.04, 1, 0.97])

    out_path = Path(out_dir) / f"{scenario}.png"
    fig.savefig(out_path, dpi=150, bbox_inches="tight")
    print(f"  Saved: {out_path}")
    plt.close(fig)


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    csv_path = sys.argv[1] if len(sys.argv) > 1 else "snapshots.csv"
    scenario_filter = sys.argv[2] if len(sys.argv) > 2 else ""

    out_dir = Path(csv_path).parent / "viz"
    out_dir.mkdir(parents=True, exist_ok=True)

    print(f"Loading {csv_path} ...")
    rows = load_csv(csv_path)
    grouped = group_by_scenario_kind(rows, scenario_filter)

    if not grouped:
        print("No matching scenarios found.")
        return

    for scenario, kinds in sorted(grouped.items()):
        start_rows = kinds.get("start", [])
        end_rows   = kinds.get("end",   [])

        if not start_rows or not end_rows:
            print(f"  Skipping {scenario}: missing start or end snapshot.")
            continue

        print(f"  Plotting {scenario} ({len(start_rows)} labels) ...")
        plot_scenario(scenario, start_rows, end_rows, out_dir)

    print("Done.")


if __name__ == "__main__":
    main()
