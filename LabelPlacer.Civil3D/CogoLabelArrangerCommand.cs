using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;

[assembly: ExtensionApplication(null)]
[assembly: CommandClass(typeof(LabelPlacer.Civil3D.CogoLabelArrangerCommands))]

namespace LabelPlacer.Civil3D
{
    public class CogoLabelArrangerCommands
    {
        // ── Commands ──────────────────────────────────────────────────────────

        [CommandMethod("ArrangeCogoLabels")]
        public void ArrangeCogoLabels()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            PromptKeywordOptions pko =
                new PromptKeywordOptions("\nArrange COGO labels [Selection/All] <Selection>: ");
            pko.Keywords.Add("Selection");
            pko.Keywords.Add("All");
            pko.Keywords.Default = "Selection";
            pko.AllowNone        = true;

            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status == PromptStatus.Cancel) { ed.WriteMessage("\nCancelled.\n"); return; }

            bool useAll = pr.Status == PromptStatus.OK &&
                          pr.StringResult.Equals("All", StringComparison.OrdinalIgnoreCase);

            ObjectId[] ids = useAll ? CollectAllPoints(doc, ed) : CollectSelection(doc, ed);
            if (ids != null && ids.Length > 0)
                StackLabels(doc, ed, ids);
        }

        [CommandMethod("ArrangeCogoSelected")]
        public void ArrangeCogoSelected()
        {
            Document  doc = Application.DocumentManager.MdiActiveDocument;
            ObjectId[] ids = CollectSelection(doc, doc.Editor);
            if (ids != null && ids.Length > 0) StackLabels(doc, doc.Editor, ids);
        }

        [CommandMethod("ArrangeCogoAll")]
        public void ArrangeCogoAll()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            ObjectId[] ids = CollectAllPoints(doc, doc.Editor);
            if (ids != null && ids.Length > 0) StackLabels(doc, doc.Editor, ids);
        }

        // ── Collection helpers ────────────────────────────────────────────────

        private static ObjectId[] CollectSelection(Document doc, Editor ed)
        {
            var filter = new SelectionFilter(
                new[] { new TypedValue((int)DxfCode.Start, "AECC_COGO_POINT") });
            var pso = new PromptSelectionOptions { MessageForAdding = "\nSelect COGO points: " };
            PromptSelectionResult psr = ed.GetSelection(pso, filter);
            if (psr.Status != PromptStatus.OK) { ed.WriteMessage("\nCancelled.\n"); return null; }
            return psr.Value.GetObjectIds();
        }

        private static ObjectId[] CollectAllPoints(Document doc, Editor ed)
        {
            ed.WriteMessage("  Enumerating CogoPoints collection...\n");
            var ids = new List<ObjectId>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in CivilApplication.ActiveDocument.CogoPoints)
                    ids.Add(id);
                tr.Commit();
            }
            if (ids.Count == 0) { ed.WriteMessage("\nNo COGO points found.\n"); return null; }
            return ids.ToArray();
        }

        // ── Core: greedy deconfliction ────────────────────────────────────────

        private static void StackLabels(Document doc, Editor ed, ObjectId[] pointIds)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            void Tick(string phase)
            {
                ed.WriteMessage($"  [{sw.Elapsed:mm\\:ss\\.f}] {phase}\n");
            }

            ed.WriteMessage($"\nProcessing {pointIds.Length} point(s)...\n");

            // ── Read anchor positions ─────────────────────────────────────────
            Tick("Reading anchor positions...");
            var pts = new List<(ObjectId id, double x, double y)>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in pointIds)
                {
                    var pt = tr.GetObject(id, OpenMode.ForRead) as CogoPoint;
                    if (pt != null) pts.Add((id, pt.Location.X, pt.Location.Y));
                }
                tr.Commit();
            }

            int n = pts.Count;
            if (n == 0) { ed.WriteMessage("\nNo COGO points found.\n"); return; }
            Tick($"Read {n} points.");

            // ── Sort by X — used by both NN and co-location passes ────────────
            var byX = new List<int>(n);
            for (int i = 0; i < n; i++) byX.Add(i);
            byX.Sort((a, b) => pts[a].x.CompareTo(pts[b].x));

            // ── Median nearest-neighbour  O(n log n) sliding window ───────────
            // For each point scan only right-neighbours until the X gap alone
            // exceeds the current best distance, then mirror left.
            // Sample every k-th point (max 500 samples) for a fast median estimate.
            int sampleStep = Math.Max(1, n / 500);
            var nnDists = new List<double>((n + sampleStep - 1) / sampleStep);
            for (int si = 0; si < byX.Count; si += sampleStep)
            {
                int i = byX[si];
                double bestSq = double.MaxValue;
                for (int k = si + 1; k < byX.Count; k++)
                {
                    int j = byX[k];
                    double dx = pts[j].x - pts[i].x;
                    if (dx * dx >= bestSq) break;
                    double dy = pts[i].y - pts[j].y;
                    double sq = dx * dx + dy * dy;
                    if (sq > 1e-12 && sq < bestSq) bestSq = sq;
                }
                for (int k = si - 1; k >= 0; k--)
                {
                    int j = byX[k];
                    double dx = pts[i].x - pts[j].x;
                    if (dx * dx >= bestSq) break;
                    double dy = pts[i].y - pts[j].y;
                    double sq = dx * dx + dy * dy;
                    if (sq > 1e-12 && sq < bestSq) bestSq = sq;
                }
                if (bestSq < double.MaxValue) nnDists.Add(Math.Sqrt(bestSq));
            }
            nnDists.Sort();
            double medianNN = nnDists.Count > 0 ? nnDists[nnDists.Count / 2] : 0.0;

            if (medianNN < 1e-6)
                medianNN = Math.Max(Math.Abs(pts[0].x), Math.Abs(pts[0].y)) * 0.0002;
            medianNN = Math.Max(medianNN, 0.001);

            // ── Sizing ────────────────────────────────────────────────────────
            // All dimensions measured directly in Civil 3D with DIST (drawing units).
            // Update these constants if the label style or scale changes.
            const double LabelW        = 7.8;
            const double LabelH        = 1.7;
            const double AnchorMarkerSize = 1.5;  // anchor marker width and height

            double labelW     = LabelW;
            double labelH     = LabelH;
            double anchorGap  = AnchorMarkerSize * 1.5;  // marker width + 50% visual clearance
            double rowSpacing = labelH * 1.1;            // label height + 10% gap

            Tick($"medianNN={medianNN:G4}  labelW={labelW:G4}  labelH={labelH:G4}  anchorGap={anchorGap:G4}  rowSpacing={rowSpacing:G4}");

            // ── Phase 1: Group co-located anchors  O(n log n) sliding window ──
            // Points within 10% of medianNN are treated as one stacked entity.
            // byX is already sorted — only scan right-neighbours until X gap
            // alone exceeds the co-location threshold.
            double colocThresh = medianNN * 0.1;
            double colocSq     = colocThresh * colocThresh;
            int[] par = new int[n];
            for (int i = 0; i < n; i++) par[i] = i;
            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }

            for (int si = 0; si < byX.Count; si++)
            {
                int i = byX[si];
                for (int sk = si + 1; sk < byX.Count; sk++)
                {
                    int j = byX[sk];
                    double dx = pts[j].x - pts[i].x;
                    if (dx > colocThresh) break;
                    double dy = pts[i].y - pts[j].y;
                    if (dx * dx + dy * dy <= colocSq)
                    { int ri = Find(i), rj = Find(j); if (ri != rj) par[rj] = ri; }
                }
            }

            var groupMap = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!groupMap.TryGetValue(r, out var lst)) groupMap[r] = lst = new List<int>();
                lst.Add(i);
            }

            // ── Phase 2: Build LabelBlocks with initial positions ─────────────
            var blocks = new List<LabelBlock>(groupMap.Count);
            foreach (var kv in groupMap)
            {
                var members = kv.Value;
                members.Sort((a, b) => pts[a].y.CompareTo(pts[b].y)); // sort by Y

                double maxAnchorX = double.MinValue, sumY = 0;
                foreach (int i in members)
                {
                    if (pts[i].x > maxAnchorX) maxAnchorX = pts[i].x;
                    sumY += pts[i].y;
                }
                double centroidY = sumY / members.Count;
                double blockH    = members.Count * rowSpacing;

                blocks.Add(new LabelBlock
                {
                    Members  = members,
                    AnchorX  = maxAnchorX,
                    AnchorY  = centroidY,
                    LabelX   = maxAnchorX + anchorGap,
                    LabelY   = centroidY - blockH / 2.0,  // bottom of block, Y-up coords
                    BlockH   = blockH,
                    LabelH   = rowSpacing,
                    LabelW   = labelW,
                });
            }

            Tick($"Co-location done: {blocks.Count} block(s).");

            // ── Phase 3: Middle-out processing order by LabelX ───────────────
            // Process the X-centre block first so the densest area gets priority
            // for its natural position; outer blocks adapt around it.
            blocks.Sort((a, b) => a.LabelX.CompareTo(b.LabelX));
            int mid = blocks.Count / 2;
            var order = new List<int>(blocks.Count) { mid };
            for (int d = 1; d < blocks.Count; d++)
            {
                if (mid - d >= 0)            order.Add(mid - d);
                if (mid + d < blocks.Count)  order.Add(mid + d);
            }

            // ── Phase 4: Greedy Y deconfliction ──────────────────────────────
            // Pre-populate placed with anchor marker obstacles (immovable).
            // Each co-location group contributes one obstacle centred on its anchor.
            double mr = AnchorMarkerSize / 2.0;
            var placed = new List<LabelBlock>(blocks.Count * 2);

            // ── Spatial bucket index for O(1) X-conflict lookup ───────────────
            // Each block is stored in ONE bucket keyed by floor(AnchorX / bucketW).
            // When querying, we check the 3 buckets covering [blkXL-bucketW, blkXR].
            double bucketW = labelW + anchorGap + AnchorMarkerSize; // ≈ 11.55 — wider than any block
            var xIndex = new Dictionary<int, List<LabelBlock>>();
            void IndexAdd(LabelBlock b)
            {
                int key = (int)Math.Floor(b.AnchorX / bucketW);
                if (!xIndex.TryGetValue(key, out var lst)) xIndex[key] = lst = new List<LabelBlock>();
                lst.Add(b);
            }
            List<LabelBlock> IndexQuery(double xL, double xR)
            {
                int k0 = (int)Math.Floor((xL - bucketW) / bucketW);
                int k1 = (int)Math.Floor( xR            / bucketW);
                var result = new List<LabelBlock>();
                for (int k = k0; k <= k1; k++)
                    if (xIndex.TryGetValue(k, out var lst)) result.AddRange(lst);
                return result;
            }

            foreach (var blk in blocks)
            {
                var obs = new LabelBlock
                {
                    Members = new List<int>(),          // no labels — obstacle only
                    AnchorX = blk.AnchorX,
                    AnchorY = blk.AnchorY,
                    LabelX  = blk.AnchorX - mr,        // centred on anchor
                    LabelY  = blk.AnchorY - mr,
                    BlockH  = AnchorMarkerSize,
                    LabelH  = AnchorMarkerSize,
                    LabelW  = AnchorMarkerSize,
                };
                placed.Add(obs);
                IndexAdd(obs);
            }

            int nudged = 0;
            int processed = 0;
            int deconflictStep = Math.Max(1, blocks.Count / 10); // report every 10%
            Tick($"Deconfliction starting ({blocks.Count} blocks)...");

            foreach (int bi in order)
            {
                LabelBlock blk = blocks[bi];
                processed++;
                if (processed % deconflictStep == 0)
                    Tick($"  ...{processed}/{blocks.Count} blocks processed");

                // X collision rectangle:
                //   Label blocks  → [AnchorX, LabelX + LabelW]  covers leader + text
                //   Anchor obstacles → [LabelX, LabelX + LabelW]  (their own marker square)
                double blkXL = blk.AnchorX;
                double blkXR = blk.LabelX + blk.LabelW;

                var xConflicts = new List<LabelBlock>();
                foreach (var p in IndexQuery(blkXL, blkXR))
                {
                    // Skip this block's own anchor obstacle.
                    if (p.Members.Count == 0
                        && Math.Abs(p.AnchorX - blk.AnchorX) < 1e-9
                        && Math.Abs(p.AnchorY - blk.AnchorY) < 1e-9)
                        continue;

                    // Full X span: handles both right-placed and (future) left-placed blocks.
                    double pXL = p.Members.Count > 0
                        ? Math.Min(p.AnchorX, p.LabelX)
                        : p.LabelX;
                    double pXR = p.Members.Count > 0
                        ? Math.Max(p.AnchorX, p.LabelX + p.LabelW)
                        : p.LabelX + p.LabelW;
                    if (blkXL < pXR && blkXR > pXL)
                        xConflicts.Add(p);
                }

                if (xConflicts.Count > 0)
                {
                    double maxDisp = Math.Max(medianNN * 2.0, blk.BlockH * 1.5);
                    double clearY = FindClearY(
                        blk.LabelY, blk.BlockH,
                        blk.AnchorX, blk.AnchorY, blk.LabelX,
                        xConflicts, labelH, maxDisp);
                    if (Math.Abs(clearY - blk.LabelY) > 1e-9)
                    {
                        blk.LabelY = clearY;
                        nudged++;
                    }
                }

                placed.Add(blk);
                IndexAdd(blk);
            }

            Tick($"Deconfliction done: {nudged} block(s) nudged.");

            // ── Phase 5: Write LabelLocations ────────────────────────────────
            // Bulk-write strategy:
            //   • Disable undo recording to eliminate per-write journal overhead.
            //   • Commit in batches of 500 so Civil 3D processes changes
            //     incrementally rather than all 13k objects at once.
            Tick("Writing label positions...");

            // Flatten blocks → (ObjectId, x, y) write list
            var writes = new List<(ObjectId id, double x, double y, double z)>(n);
            foreach (var blk in placed)
            {
                if (blk.Members.Count == 0) continue;
                for (int slot = 0; slot < blk.Members.Count; slot++)
                {
                    var entry = pts[blk.Members[slot]];
                    writes.Add((entry.id,
                                blk.LabelX,
                                blk.LabelY + slot * blk.LabelH,
                                0));           // Z filled in from pt.Location.Z below
                }
            }

            int moved = 0;
            doc.Database.DisableUndoRecording(true);
            doc.Database.UpdateExt(false);
            try
            {
                using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
                {
                    for (int wi = 0; wi < writes.Count; wi++)
                    {
                        var w  = writes[wi];
                        var pt = tr.GetObject(w.id, OpenMode.ForWrite) as CogoPoint;
                        if (pt == null) continue;
                        pt.LabelLocation = new Point3d(w.x, w.y, pt.Location.Z);
                        moved++;
                        if ((wi + 1) % 500 == 0)
                            Tick($"  ...{wi + 1}/{writes.Count} labels written");
                    }
                    tr.Commit();
                }
                Tick($"  ...{writes.Count}/{writes.Count} labels written");
            }
            finally
            {
                doc.Database.UpdateExt(true);
                doc.Database.DisableUndoRecording(false);
            }

            Autodesk.AutoCAD.ApplicationServices.Application.UpdateScreen();
            doc.Editor.Regen();
            Tick($"Done — moved {moved} label(s).");
            ed.WriteMessage($"\nCOGO label stacking complete — moved {moved} label(s).\n");
        }

        /// <summary>
        /// Finds the Y (bottom of block) nearest to <paramref name="startY"/> that
        /// does not overlap any already-placed block in <paramref name="conflicts"/>.
        /// </summary>
        /// <param name="anchorX">X of this block's anchor (left edge of leader).</param>
        /// <param name="anchorY">Y of this block's anchor.</param>
        /// <param name="labelX">X of this block's label column (right edge of leader / left edge of text).</param>
        /// <param name="maxDisp">Maximum displacement from startY before falling back.</param>
        private static double FindClearY(double startY, double blockH,
                                         double anchorX, double anchorY, double labelX,
                                         List<LabelBlock> conflicts, double labelH,
                                         double maxDisp = double.MaxValue)
        {
            double eps = labelH * 0.05;
            double mr  = labelH * 0.44; // ≈ AnchorMarkerSize/2 relative to labelH

            // ── Occupied Y intervals ──────────────────────────────────────────────────────
            // A displaced block's label text and its anchor marker are treated as two
            // SEPARATE obstacles.  Merging them into one big interval would falsely
            // block the gap between a far-displaced label and its anchor, making it
            // impossible for other blocks to use that space.
            var occupied = new List<(double lo, double hi)>(conflicts.Count * 2);
            foreach (var c in conflicts)
            {
                if (c.Members.Count > 0)
                {
                    // Label text block
                    occupied.Add((c.LabelY - eps, c.LabelY + c.BlockH + eps));
                    // Anchor marker (may be far from the text if the block was displaced)
                    occupied.Add((c.AnchorY - mr - eps, c.AnchorY + mr + eps));
                }
                else
                {
                    // Pure anchor obstacle
                    occupied.Add((c.LabelY - eps, c.LabelY + c.BlockH + eps));
                }
            }

            // ── Leader corridor check ─────────────────────────────────────────────────────
            // For a candidate bottom Y, the leader runs from (anchorX, anchorY) to
            // (labelX, candidateY..candidateY+blockH).  Returns false if this corridor
            // overlaps any placed block's label text rectangle.
            bool LeaderClear(double cand)
            {
                double ldXL = anchorX;
                double ldXR = labelX;
                if (ldXL >= ldXR) return true; // degenerate — no horizontal leader extent

                double ldYL = Math.Min(anchorY, cand)          - eps;
                double ldYH = Math.Max(anchorY, cand + blockH) + eps;

                foreach (var c in conflicts)
                {
                    if (c.Members.Count == 0) continue; // anchor marker — not text
                    double cXL = c.LabelX,           cXR = c.LabelX + c.LabelW;
                    double cYL = c.LabelY    - eps,  cYH = c.LabelY + c.BlockH + eps;
                    if (ldXL < cXR && ldXR > cXL && ldYL < cYH && ldYH > cYL)
                        return false;
                }
                return true;
            }

            // Quick check: is startY already clear (text and leader)?
            if (!OverlapsAny(startY, startY + blockH, occupied) && LeaderClear(startY))
                return startY;

            // Candidates: just above or just below each occupied interval, nearest first.
            var candidates = new List<double>(occupied.Count * 2);
            foreach (var (lo, hi) in occupied)
            {
                candidates.Add(hi);          // bottom of block sits just above interval
                candidates.Add(lo - blockH); // top of block sits just below interval
            }
            candidates.Sort((a, b) => Math.Abs(a - startY).CompareTo(Math.Abs(b - startY)));

            // Pass 1: within maxDisp, text clear AND leader corridor clear (ideal).
            foreach (double cand in candidates)
                if (Math.Abs(cand - startY) <= maxDisp
                    && !OverlapsAny(cand, cand + blockH, occupied) && LeaderClear(cand))
                    return cand;

            // Pass 2: within maxDisp, text clear only (leader may cross).
            foreach (double cand in candidates)
                if (Math.Abs(cand - startY) <= maxDisp
                    && !OverlapsAny(cand, cand + blockH, occupied))
                    return cand;

            // Pass 3: unlimited, text clear AND leader clear (long leader but no text overlap).
            foreach (double cand in candidates)
                if (!OverlapsAny(cand, cand + blockH, occupied) && LeaderClear(cand))
                    return cand;

            // Pass 4: unlimited, text clear only — always avoid text-on-text overlap.
            foreach (double cand in candidates)
                if (!OverlapsAny(cand, cand + blockH, occupied))
                    return cand;

            return startY; // should not reach here
        }

        private static bool OverlapsAny(double lo, double hi,
                                        List<(double lo, double hi)> intervals)
        {
            foreach (var iv in intervals)
                if (lo < iv.hi && hi > iv.lo) return true;
            return false;
        }

    }

    /// <summary>
    /// A group of co-located COGO points treated as a single label entity
    /// for the purposes of Y deconfliction.
    /// </summary>
    internal class LabelBlock
    {
        public List<int> Members;   // indices into the master pts list
        public double AnchorX;      // rightmost anchor X in this group
        public double AnchorY;      // centroid Y of anchors
        public double LabelX;       // label column X (left edge)
        public double LabelY;       // Y of bottom-most label (Y-up coordinate system)
        public double BlockH;       // total block height = Members.Count × LabelH
        public double LabelH;       // per-label height
        public double LabelW;       // per-label width (for X overlap test)
    }
}
