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

            // ── Read anchor positions first ───────────────────────────────────
            var points = new List<(ObjectId id, double x, double y)>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in pointIds)
                {
                    CogoPoint pt = tr.GetObject(id, OpenMode.ForRead) as CogoPoint;
                    if (pt == null) continue;
                    points.Add((id, pt.Location.X, pt.Location.Y));
                }
                tr.Commit();
            }

            if (points.Count == 0) { ed.WriteMessage("\nNo COGO points found in transaction.\n"); return; }

            // ── Derive sizing from actual point spread ────────────────────────
            // This guarantees visible offsets regardless of drawing scale/units.
            double allMinX = double.MaxValue, allMaxX = double.MinValue;
            double allMinY = double.MaxValue, allMaxY = double.MinValue;
            foreach (var p in points)
            {
                if (p.x < allMinX) allMinX = p.x;
                if (p.x > allMaxX) allMaxX = p.x;
                if (p.y < allMinY) allMinY = p.y;
                if (p.y > allMaxY) allMaxY = p.y;
            }
            double spread = Math.Max(allMaxX - allMinX, allMaxY - allMinY);

            // Also try annotation scale × Dimtxt as a cross-check
            double scale  = GetAnnotationScale(doc);
            double scaleH = doc.Database.Dimtxt * scale;

            // Use whichever gives a more meaningful unit (prefer scale-based if
            // it is at least 0.5% of spread; otherwise fall back to spread-based)
            double baseUnit = (scaleH > spread * 0.005 && scaleH > 1e-6)
                ? scaleH
                : Math.Max(spread * 0.015, 1.0);   // 1.5% of spread, ≥ 1 drawing unit

            double rowSpacing  = baseUnit * 2.5;
            double clusterDist = baseUnit * 8.0;
            double columnGap   = baseUnit * 4.0;

            ed.WriteMessage($"  spread={spread:G4}, baseUnit={baseUnit:G4}, " +
                            $"rowSpacing={rowSpacing:G4}, columnGap={columnGap:G4}\n");

            // ── Cluster by proximity (union-find) ─────────────────────────────
            int   n   = points.Count;
            int[] par = new int[n];
            for (int i = 0; i < n; i++) par[i] = i;

            int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }

            double d2 = clusterDist * clusterDist;
            for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                double dx = points[i].x - points[j].x;
                double dy = points[i].y - points[j].y;
                if (dx * dx + dy * dy <= d2)
                {
                    int ri = Find(i), rj = Find(j);
                    if (ri != rj) par[rj] = ri;
                }
            }

            // Group labels by cluster root
            var clusters = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++)
            {
                int r = Find(i);
                if (!clusters.ContainsKey(r)) clusters[r] = new List<int>();
                clusters[r].Add(i);
            }

            ed.WriteMessage($"  {points.Count} pts → {clusters.Count} cluster(s)\n");

            // ── Place each cluster ────────────────────────────────────────────
            int moved = 0;
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var kv in clusters)
                {
                    List<int> members = kv.Value;

                    // Bounding box of anchor positions in this cluster
                    double minX = double.MaxValue, maxX = double.MinValue;
                    double minY = double.MaxValue, maxY = double.MinValue;
                    foreach (int i in members)
                    {
                        if (points[i].x < minX) minX = points[i].x;
                        if (points[i].x > maxX) maxX = points[i].x;
                        if (points[i].y < minY) minY = points[i].y;
                        if (points[i].y > maxY) maxY = points[i].y;
                    }

                    // Decide side: prefer right unless more clusters sit to the right
                    bool goLeft = HasNeighbourClusters(kv.Key, clusters, points, maxX, clusterDist * 3);
                    double colX = goLeft
                        ? minX - columnGap
                        : maxX + columnGap;

                    // Sort members by anchor Y ascending so leaders don't cross
                    members.Sort((a, b) => points[a].y.CompareTo(points[b].y));

                    // Center the label column vertically on the cluster
                    int    count   = members.Count;
                    double totalH  = (count - 1) * rowSpacing;
                    double startY  = (minY + maxY) / 2.0 - totalH / 2.0;

                    ed.WriteMessage($"  Cluster {kv.Key}: {count} pts, bbox=({minX:F1},{minY:F1})-({maxX:F1},{maxY:F1}), colX={colX:F1}, startY={startY:F1}\n");

                    // Write LabelLocation for each label
                    for (int slot = 0; slot < count; slot++)
                    {
                        int     idx = members[slot];
                        ObjectId id = points[idx].id;

                        CogoPoint pt = tr.GetObject(id, OpenMode.ForWrite) as CogoPoint;
                        if (pt == null) continue;

                        double labelY = startY + slot * rowSpacing;
                        var newLoc = new Point3d(colX, labelY, pt.Location.Z);
                        pt.LabelLocation = newLoc;
                        moved++;
                    }
                }

                tr.Commit();
            }

            // Force display refresh
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
