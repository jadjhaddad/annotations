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

            // ── Median nearest-neighbour distance ─────────────────────────────
            // This gives a distance that reflects the natural spacing between
            // points, independent of total drawing extent.
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
                    if (sq < bestSq) bestSq = sq;
                }
                if (bestSq < double.MaxValue) nnDists.Add(Math.Sqrt(bestSq));
            }
            nnDists.Sort();
            double medianNN = nnDists.Count > 0 ? nnDists[nnDists.Count / 2] : 10.0;

            // ── Sizing ────────────────────────────────────────────────────────
            // Prefer annotation-scale label height; fall back to 15% of median spacing.
            double scaleH    = doc.Database.Dimtxt * GetAnnotationScale(doc);
            double baseUnit  = (scaleH > medianNN * 0.01 && scaleH < medianNN * 0.8)
                               ? scaleH
                               : medianNN * 0.15;
            // Cluster: only join points within 2× median nearest-neighbour
            double clusterDist = medianNN * 2.0;
            double columnGap   = baseUnit * 3.0;
            double rowSpacing  = baseUnit * 1.8;

            ed.WriteMessage($"  medianNN={medianNN:G4}  baseUnit={baseUnit:G4}\n");
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
                    double cx = (minX + maxX) / 2.0;
                    double cy = (minY + maxY) / 2.0;

                    // Prefer right; go left only when another cluster is close on the right
                    bool goLeft = HasNeighbourClusters(kv.Key, clusters, points, maxX, clusterDist * 3);
                    double colX = goLeft ? minX - columnGap : maxX + columnGap;

                    // Sort by Y so leader lines stay parallel
                    members.Sort((a, b) => points[a].y.CompareTo(points[b].y));

                    // Centre the column on the cluster's Y midpoint
                    double totalH = (count - 1) * rowSpacing;
                    double startY = cy - totalH / 2.0;

                    if (count > 1)
                        ed.WriteMessage($"  Cluster: {count} pts centre=({cx:F1},{cy:F1})  colX={colX:F1}  startY={startY:F1}\n");

                    for (int slot = 0; slot < count; slot++)
                    {
                        var pt = tr.GetObject(points[members[slot]].id, OpenMode.ForWrite) as CogoPoint;
                        if (pt == null) { ed.WriteMessage($"  [WARN] slot {slot}: GetObject returned null\n"); continue; }

                        var oldLoc = pt.LabelLocation;
                        var newLoc = new Point3d(colX, startY + slot * rowSpacing, pt.Location.Z);
                        pt.LabelLocation = newLoc;
                        var afterLoc = pt.LabelLocation;

                        ed.WriteMessage($"  pt#{slot}: anchor=({pt.Location.X:F2},{pt.Location.Y:F2})" +
                                        $"  label {oldLoc.X:F2},{oldLoc.Y:F2}" +
                                        $" -> {newLoc.X:F2},{newLoc.Y:F2}" +
                                        $" (read-back: {afterLoc.X:F2},{afterLoc.Y:F2})\n");
                        moved++;
                    }
                }

                tr.Commit();
            }

            Autodesk.AutoCAD.ApplicationServices.Application.UpdateScreen();
            doc.Editor.Regen();
            ed.WriteMessage($"\nCOGO label stacking complete — moved {moved} label(s).\n");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Returns true if there are clustered points to the right of this cluster.</summary>
        private static bool HasNeighbourClusters(
            int thisRoot,
            Dictionary<int, List<int>> clusters,
            List<(ObjectId id, double x, double y)> points,
            double thisMaxX,
            double searchDist)
        {
            foreach (var kv in clusters)
            {
                if (kv.Key == thisRoot) continue;
                foreach (int j in kv.Value)
                    if (points[j].x > thisMaxX && points[j].x - thisMaxX < searchDist)
                        return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the current model-space annotation scale ratio.
        /// Falls back to 1.0 if the scale cannot be read.
        /// </summary>
        private static double GetAnnotationScale(Document doc)
        {
            try
            {
                var ocm   = doc.Database.ObjectContextManager;
                var occ   = ocm?.GetContextCollection("ACDB_ANNOTATIONSCALES");
                if (occ?.CurrentContext is AnnotationScale s && s.PaperUnits > 0)
                    return s.DrawingUnits / s.PaperUnits;
            }
            catch { }
            return 1.0;
        }
    }
}
