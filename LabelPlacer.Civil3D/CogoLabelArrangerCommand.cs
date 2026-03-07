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

        // ── Core: column stacking ─────────────────────────────────────────────

        /// <summary>
        /// Groups nearby COGO points into clusters, then stacks each cluster's
        /// labels in a neat vertical column to one side — the survey-standard layout.
        ///
        /// All sizing is derived from the current annotation scale so it works
        /// at any drawing scale without hard-coded values.
        /// </summary>
        private static void StackLabels(Document doc, Editor ed, ObjectId[] pointIds)
        {
            ed.WriteMessage($"\nProcessing {pointIds.Length} point(s)...\n");

            // ── Read anchor positions ─────────────────────────────────────────
            var points = new List<(ObjectId id, double x, double y)>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in pointIds)
                {
                    var pt = tr.GetObject(id, OpenMode.ForRead) as CogoPoint;
                    if (pt != null) points.Add((id, pt.Location.X, pt.Location.Y));
                }
                tr.Commit();
            }

            int n = points.Count;
            if (n == 0) { ed.WriteMessage("\nNo COGO points found.\n"); return; }
            ed.WriteMessage($"  Read {n} point(s).\n");

            // ── Nearest-neighbour distances (skip zero = duplicate coords) ────
            var nnDists = new List<double>(n);
            for (int i = 0; i < n; i++)
            {
                double bestSq = double.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (i == j) continue;
                    double dx = points[i].x - points[j].x;
                    double dy = points[i].y - points[j].y;
                    double sq = dx * dx + dy * dy;
                    if (sq > 1e-12 && sq < bestSq) bestSq = sq;  // ignore exact duplicates
                }
                if (bestSq < double.MaxValue) nnDists.Add(Math.Sqrt(bestSq));
            }
            nnDists.Sort();
            double medianNN = nnDists.Count > 0 ? nnDists[nnDists.Count / 2] : 0.0;

            // ── Scale info (logged only — not used for layout) ────────────────
            double scale  = GetAnnotationScale(doc);
            double scaleH = doc.Database.Dimtxt * scale;
            ed.WriteMessage($"  annotScale={scale:G4}  Dimtxt={doc.Database.Dimtxt:G4}  scaleH={scaleH:G4}  medianNN={medianNN:G4}\n");

            // ── Layout distances — derived from medianNN ──────────────────────
            // medianNN is the measured typical spacing between nearby points in
            // this drawing, so it's the most reliable spatial unit we have.
            // If all points are duplicates (medianNN=0) fall back to coordinate magnitude.
            if (medianNN < 1e-6)
                medianNN = Math.Max(Math.Abs(points[0].x), Math.Abs(points[0].y)) * 0.0002;
            medianNN = Math.Max(medianNN, 0.001);

            double clusterDist = medianNN * 1.5;   // group only truly close points
            double columnGap   = medianNN * 0.6;   // column sits 60% of point spacing from cluster edge
            double rowSpacing  = medianNN * 0.5;   // rows spaced 50% of point spacing

            ed.WriteMessage($"  clusterDist={clusterDist:G4}  columnGap={columnGap:G4}  rowSpacing={rowSpacing:G4}\n");

            // ── Union-find clustering ─────────────────────────────────────────
            int[] par = new int[n];
            for (int i = 0; i < n; i++) par[i] = i;
            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }

            double cd2 = clusterDist * clusterDist;
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = points[i].x - points[j].x;
                double dy = points[i].y - points[j].y;
                if (dx * dx + dy * dy <= cd2)
                {
                    int ri = Find(i), rj = Find(j);
                    if (ri != rj) par[rj] = ri;
                }
            }

            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!clusters.TryGetValue(r, out var lst)) clusters[r] = lst = new List<int>();
                lst.Add(i);
            }

            int largestCluster = 0;
            foreach (var kv in clusters) if (kv.Value.Count > largestCluster) largestCluster = kv.Value.Count;
            ed.WriteMessage($"  → {clusters.Count} cluster(s), largest has {largestCluster} pt(s)\n");

            // ── Place each cluster ────────────────────────────────────────────
            // Small clusters (≤ MaxClusterStack): sort by Y, stack a neat column
            //   to the RIGHT of the cluster bounding box.
            // Large clusters: each point gets its own label offset from its own
            //   anchor — avoids all leaders fanning to one distant column.
            const int MaxClusterStack = 5;

            int moved = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var kv in clusters)
                {
                    List<int> members = kv.Value;
                    int count = members.Count;

                    // Bounding box of this cluster's anchors
                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    foreach (int i in members)
                    {
                        if (points[i].x < minX) minX = points[i].x;
                        if (points[i].x > maxX) maxX = points[i].x;
                        if (points[i].y < minY) minY = points[i].y;
                        if (points[i].y > maxY) maxY = points[i].y;
                    }
                    double cy = (minY + maxY) / 2.0;

                    // Sort by Y so leader lines don't cross within the cluster
                    members.Sort((a, b) => points[a].y.CompareTo(points[b].y));

                    if (count <= MaxClusterStack)
                    {
                        // ── Small cluster: shared column to the right ─────────
                        double colX   = maxX + columnGap;
                        double totalH = (count - 1) * rowSpacing;
                        double startY = cy - totalH / 2.0;

                        ed.WriteMessage($"  SmallCluster({count}): colX={colX:F1} startY={startY:F1}\n");

                        for (int slot = 0; slot < count; slot++)
                        {
                            var pt = tr.GetObject(points[members[slot]].id, OpenMode.ForWrite) as CogoPoint;
                            if (pt == null) continue;
                            pt.LabelLocation = new Point3d(colX, startY + slot * rowSpacing, pt.Location.Z);
                            moved++;
                        }
                    }
                    else
                    {
                        // ── Large / dense cluster: each point independently ────
                        // Place each label directly to the right of its own anchor.
                        // Stack labels that share the same anchor Y so they don't overlap.
                        ed.WriteMessage($"  LargeCluster({count}): individual offsets\n");

                        for (int slot = 0; slot < count; slot++)
                        {
                            int idx = members[slot];
                            var pt = tr.GetObject(points[idx].id, OpenMode.ForWrite) as CogoPoint;
                            if (pt == null) continue;

                            // Each label goes right of its own anchor; use slot index for
                            // a slight Y stagger so duplicate-coord labels don't overlap.
                            double labelX = points[idx].x + columnGap;
                            double labelY = points[idx].y + (slot - count / 2.0) * rowSpacing * 0.4;
                            pt.LabelLocation = new Point3d(labelX, labelY, pt.Location.Z);
                            moved++;
                        }
                    }
                }

                tr.Commit();
            }

            Autodesk.AutoCAD.ApplicationServices.Application.UpdateScreen();
            doc.Editor.Regen();
            ed.WriteMessage($"\nCOGO label stacking complete — moved {moved} label(s).\n");
        }

        /// <summary>
        /// Returns the current model-space annotation scale ratio.
        /// Falls back to 1.0 if the scale cannot be read.
        /// </summary>
        private static double GetAnnotationScale(Document doc)
        {
            // CANNOSCALEVALUE = paper/drawing ratio (e.g. 1:500 → 0.002)
            // Invert to get model units per paper unit (e.g. 500)
            try
            {
                var val = Autodesk.AutoCAD.ApplicationServices.Application
                              .GetSystemVariable("CANNOSCALEVALUE");
                double ratio = Convert.ToDouble(val);
                if (ratio > 1e-12) return 1.0 / ratio;
            }
            catch { }

            // Fallback: ObjectContextManager
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
}
