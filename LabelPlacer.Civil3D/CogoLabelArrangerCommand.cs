using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using LabelPlacer;

[assembly: ExtensionApplication(null)]
[assembly: CommandClass(typeof(LabelPlacer.Civil3D.CogoLabelArrangerCommands))]

namespace LabelPlacer.Civil3D
{
    /// <summary>
    /// Civil 3D commands that auto-arrange COGO point labels to minimise overlap.
    ///
    /// Commands
    /// --------
    ///   ARRANGECOGOSELECTED  — user selects COGO points, then labels are arranged
    ///   ARRANGECOGOALL       — arranges every COGO point in the drawing
    ///   ARRANGECOGOLABELS    — interactive: prompts Selection / All
    /// </summary>
    public class CogoLabelArrangerCommands
    {
        // ─────────────────────────────────────────────────────────────────────
        // Commands
        // ─────────────────────────────────────────────────────────────────────

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

            if (useAll) RunOnAll(doc, ed);
            else        RunOnSelection(doc, ed);
        }

        [CommandMethod("ArrangeCogoSelected")]
        public void ArrangeCogoSelected()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            RunOnSelection(doc, doc.Editor);
        }

        [CommandMethod("ArrangeCogoAll")]
        public void ArrangeCogoAll()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            RunOnAll(doc, doc.Editor);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Selection helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void RunOnSelection(Document doc, Editor ed)
        {
            TypedValue[]    filter = { new TypedValue((int)DxfCode.Start, "AECC_COGO_POINT") };
            SelectionFilter sf     = new SelectionFilter(filter);

            PromptSelectionOptions pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect COGO points to arrange: "
            };

            PromptSelectionResult psr = ed.GetSelection(pso, sf);
            if (psr.Status != PromptStatus.OK) { ed.WriteMessage("\nNo selection. Cancelled.\n"); return; }

            ArrangePoints(doc, ed, psr.Value.GetObjectIds());
        }

        private static void RunOnAll(Document doc, Editor ed)
        {
            var ids = new List<ObjectId>();
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in CivilApplication.ActiveDocument.CogoPoints)
                    ids.Add(id);
                tr.Commit();
            }

            if (ids.Count == 0) { ed.WriteMessage("\nNo COGO points found in drawing.\n"); return; }

            ArrangePoints(doc, ed, ids.ToArray());
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core placement logic
        // ─────────────────────────────────────────────────────────────────────

        private static void ArrangePoints(Document doc, Editor ed, ObjectId[] pointIds)
        {
            if (pointIds == null || pointIds.Length == 0) return;

            ed.WriteMessage($"\nReading {pointIds.Length} COGO point(s)...\n");

            var labels = new List<LabelState>(pointIds.Length);
            var config = new PlacerConfig();

            // ── 1. Read anchor positions and label dimensions ─────────────────
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in pointIds)
                {
                    CogoPoint pt = tr.GetObject(id, OpenMode.ForRead) as CogoPoint;
                    if (pt == null) continue;

                    Point3d anchor = pt.Location;
                    double  w, h;
                    LabelSizeSource src;

                    if (TryGetLabelExtents(pt, out w, out h))
                    {
                        src = LabelSizeSource.ApiExtents;
                    }
                    else
                    {
                        // Estimate from point number + elevation text.
                        string text       = pt.PointNumber + "\n" + pt.Elevation.ToString("F3");
                        double textHeight = doc.Database.Dimtxt;
                        (w, h) = LabelState.EstimateSize(text, textHeight, config);
                        src    = LabelSizeSource.Estimated;
                    }

                    labels.Add(new LabelState(
                        handle:     id,
                        anchor:     new Point2D(anchor.X, anchor.Y),
                        width:      w,
                        height:     h,
                        sizeSource: src));
                }
                tr.Commit();
            }

            if (labels.Count == 0) { ed.WriteMessage("\nNo valid COGO points to process.\n"); return; }

            // ── 2. Run greedy placer ─────────────────────────────────────────
            var sw = Stopwatch.StartNew();
            GreedyResult result = GreedyPlacer.Place(labels);
            sw.Stop();

            ed.WriteMessage(
                $"\nPlacement: {sw.ElapsedMilliseconds} ms | " +
                $"Overlap pairs: {result.FinalOverlapPairs} | " +
                $"Unplaced: {result.UnplacedCount} | " +
                $"Sweeps: {result.RefinementSweeps}\n");

            // ── 3. Write back label positions ────────────────────────────────
            // LabelPlacer convention:
            //   CurrentOffset.X  = left-edge X offset from anchor
            //   CurrentOffset.Y  = vertical-centre Y offset from anchor
            //
            // Civil 3D LabelLocation = bottom-left corner of label text bounding box.
            //   → LabelLocation.X = anchor.X + offset.X
            //   → LabelLocation.Y = anchor.Y + offset.Y - height/2
            using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (LabelState ls in labels)
                {
                    CogoPoint pt = tr.GetObject((ObjectId)ls.Handle, OpenMode.ForWrite) as CogoPoint;
                    if (pt == null) continue;

                    double newX = pt.Location.X + ls.CurrentOffset.X;
                    double newY = pt.Location.Y + ls.CurrentOffset.Y - ls.Height / 2.0;

                    pt.LabelLocation = new Point3d(newX, newY, pt.Location.Z);
                }
                tr.Commit();
            }

            ed.WriteMessage("\nCOGO label arrangement complete.\n");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the label extents from the Civil 3D entity (includes label text).
        /// Applies 5 % padding. Returns false if extents are degenerate.
        /// </summary>
        private static bool TryGetLabelExtents(CogoPoint pt, out double width, out double height)
        {
            width = height = 0;
            try
            {
                Extents3d ext = pt.GeometricExtents;
                double w = ext.MaxPoint.X - ext.MinPoint.X;
                double h = ext.MaxPoint.Y - ext.MinPoint.Y;
                if (w < 1e-6 || h < 1e-6) return false;

                const double pad = 1.05;
                width  = w * pad;
                height = h * pad;
                return true;
            }
            catch { return false; }
        }
    }
}
