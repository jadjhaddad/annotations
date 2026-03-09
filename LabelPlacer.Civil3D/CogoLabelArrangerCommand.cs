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
            Document  doc = Application.DocumentManager.MdiActiveDocument;
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
            ed.WriteMessage($"\nProcessing {pointIds.Length} point(s)...\n");

            // ── Read anchor positions ─────────────────────────────────────────
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

            // ── Median nearest-neighbour (skip exact duplicates) ──────────────
            var nnDists = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double bestSq = double.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    double dx = pts[i].x - pts[j].x, dy = pts[i].y - pts[j].y;
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
            // Label dimensions measured directly in Civil 3D with DIST (drawing units).
            // Update these two constants if the label style or scale changes.
            const double LabelW = 7.8;
            const double LabelH = 1.7;

            double labelW    = LabelW;
            double labelH    = LabelH;
            double anchorGap = medianNN * 0.3;  // small gap from anchor to label left edge

            ed.WriteMessage($"  medianNN={medianNN:G4}  labelW={labelW:G4}  labelH={labelH:G4}  anchorGap={anchorGap:G4}\n");

            // ── Phase 1: Group co-located anchors (same physical point) ───────
            // Points within 10% of medianNN are treated as one stacked entity.
            double colocSq = Math.Pow(medianNN * 0.1, 2);
            int[] par = new int[n];
            for (int i = 0; i < n; i++) par[i] = i;
            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }

            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = pts[i].x - pts[j].x, dy = pts[i].y - pts[j].y;
                if (dx * dx + dy * dy <= colocSq)
                { int ri = Find(i), rj = Find(j); if (ri != rj) par[rj] = ri; }
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
                double blockH    = members.Count * labelH;

                blocks.Add(new LabelBlock
                {
                    Members  = members,
                    AnchorX  = maxAnchorX,
                    AnchorY  = centroidY,
                    LabelX   = maxAnchorX + anchorGap,
                    LabelY   = centroidY - blockH / 2.0,  // bottom of block, Y-up coords
                    BlockH   = blockH,
                    LabelH   = labelH,
                    LabelW   = labelW,
                });
            }

            ed.WriteMessage($"  {blocks.Count} block(s) after co-location grouping\n");

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
            var placed = new List<LabelBlock>(blocks.Count);
            int nudged = 0;

            foreach (int bi in order)
            {
                LabelBlock blk = blocks[bi];

                // Collect already-placed blocks whose X range overlaps this one
                var xConflicts = new List<LabelBlock>();
                foreach (var p in placed)
                    if (Math.Abs(p.LabelX - blk.LabelX) < (p.LabelW + blk.LabelW) / 2.0)
                        xConflicts.Add(p);

                if (xConflicts.Count > 0)
                {
                    double clearY = FindClearY(blk.LabelY, blk.BlockH, xConflicts, labelH);
                    if (Math.Abs(clearY - blk.LabelY) > 1e-9)
                    {
                        blk.LabelY = clearY;
                        nudged++;
                    }
                }

                placed.Add(blk);
            }

            ed.WriteMessage($"  {nudged} block(s) nudged for Y clearance\n");

            // ── Phase 5: Write LabelLocations ────────────────────────────────
            int moved = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var blk in placed)
                {
                    for (int slot = 0; slot < blk.Members.Count; slot++)
                    {
                        var pt = tr.GetObject(pts[blk.Members[slot]].id, OpenMode.ForWrite) as CogoPoint;
                        if (pt == null) continue;
                        pt.LabelLocation = new Point3d(
                            blk.LabelX,
                            blk.LabelY + slot * blk.LabelH,
                            pt.Location.Z);
                        moved++;
                    }
                }
                tr.Commit();
            }

            Autodesk.AutoCAD.ApplicationServices.Application.UpdateScreen();
            doc.Editor.Regen();
            ed.WriteMessage($"\nCOGO label stacking complete — moved {moved} label(s).\n");
        }

        /// <summary>
        /// Finds the Y (bottom of block) nearest to <paramref name="startY"/> that
        /// does not overlap any already-placed block in <paramref name="conflicts"/>.
        /// </summary>
        private static double FindClearY(double startY, double blockH,
                                         List<LabelBlock> conflicts, double labelH)
        {
            double eps = labelH * 0.05;

            // Build occupied intervals from X-conflicting blocks
            var occupied = new List<(double lo, double hi)>(conflicts.Count);
            foreach (var c in conflicts)
                occupied.Add((c.LabelY - eps, c.LabelY + c.BlockH + eps));

            // Quick check: is startY already clear?
            if (!OverlapsAny(startY, startY + blockH, occupied)) return startY;

            // Candidates: just above or just below each occupied interval
            var candidates = new List<double>(occupied.Count * 2);
            foreach (var (lo, hi) in occupied)
            {
                candidates.Add(hi);             // bottom of block sits just above interval
                candidates.Add(lo - blockH);    // top of block sits just below interval
            }
            // Sort by distance from startY — minimum displacement first
            candidates.Sort((a, b) => Math.Abs(a - startY).CompareTo(Math.Abs(b - startY)));

            foreach (double cand in candidates)
                if (!OverlapsAny(cand, cand + blockH, occupied))
                    return cand;

            return startY; // fallback: couldn't clear (shouldn't happen)
        }

        private static bool OverlapsAny(double lo, double hi,
                                        List<(double lo, double hi)> intervals)
        {
            foreach (var iv in intervals)
                if (lo < iv.hi && hi > iv.lo) return true;
            return false;
        }

        private static double GetAnnotationScale(Document doc)
        {
            try
            {
                var val = Autodesk.AutoCAD.ApplicationServices.Application
                              .GetSystemVariable("CANNOSCALEVALUE");
                double ratio = Convert.ToDouble(val);
                if (ratio > 1e-12) return 1.0 / ratio;
            }
            catch { }
            try
            {
                var ocm = doc.Database.ObjectContextManager;
                var occ = ocm?.GetContextCollection("ACDB_ANNOTATIONSCALES");
                if (occ?.CurrentContext is AnnotationScale s && s.PaperUnits > 0)
                    return s.DrawingUnits / s.PaperUnits;
            }
            catch { }
            return 1.0;
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
