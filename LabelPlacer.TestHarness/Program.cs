using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using LabelPlacer;

// ============================================================================
// LabelPlacer console test harness
//
// Scenarios:
//   1. Sparse    — 20 labels in a 200×200 grid, no initial overlap expected
//   2. Cluster   — 10 coincident labels at the same anchor  [AUDIT ENABLED]
//   3. Dense     — 100 labels scattered in a tight 30×30 region [AUDIT ENABLED]
//   4. Mixed     — 5 coincident groups + sparse background (150 labels total)
//   5. Stress    — 500 labels in a very tight area (convergence diagnostic)
// ============================================================================

static class Program
{
    static bool LogEveryIteration;
    static string? SnapshotCsvPath;
    static int SnapshotInterval;
    static StreamWriter? SnapshotWriter;
    static string CurrentScenarioName = "";

    static void Main(string[] args)
    {
        (string filter, bool logEveryIter, string? snapshotCsvPath, int snapshotInterval) = ParseArgs(args);
        LogEveryIteration = logEveryIter;
        SnapshotCsvPath = snapshotCsvPath;
        SnapshotInterval = snapshotInterval;

        if (!string.IsNullOrWhiteSpace(SnapshotCsvPath))
        {
            string? dir = Path.GetDirectoryName(SnapshotCsvPath);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

            SnapshotWriter = new StreamWriter(SnapshotCsvPath!);
            SnapshotWriter.WriteLine("scenario,kind,iteration,labelIndex,groupId,anchorX,anchorY,offsetX,offsetY,width,height,left,bottom,right,top,handle");
            SnapshotWriter.Flush();
        }

        RunIf("sparse",  filter, Sparse);
        RunIf("cluster", filter, Cluster);
        RunIf("dense",   filter, Dense);
        RunIf("mixed",   filter, Mixed);
        RunIf("stress",  filter, Stress);

        Console.WriteLine("\nAll selected scenarios complete.");

        SnapshotWriter?.Dispose();
    }

    static (string filter, bool logEveryIter, string? snapshotCsvPath, int snapshotInterval) ParseArgs(string[] args)
    {
        string filter = "";
        bool logEveryIter = false;
        string? snapshotCsvPath = null;
        int snapshotInterval = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--log-every-iter", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--log-every-iteration", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("-i", StringComparison.OrdinalIgnoreCase))
            {
                logEveryIter = true;
                continue;
            }

            if (arg.Equals("--snapshot-csv", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--snapshots-csv", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--csv", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) snapshotCsvPath = args[++i];
                continue;
            }

            if (arg.Equals("--snapshot-interval", StringComparison.OrdinalIgnoreCase)
                || arg.Equals("--snap-interval", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length
                    && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    snapshotInterval = Math.Max(0, v);
                continue;
            }

            if (!arg.StartsWith("-", StringComparison.Ordinal) && filter.Length == 0)
                filter = arg.ToLowerInvariant();
        }

        return (filter, logEveryIter, snapshotCsvPath, snapshotInterval);
    }

    static void RunIf(string name, string filter, Action test)
    {
        if (filter.Length > 0 && !name.Contains(filter)) return;
        Console.WriteLine($"\n{'=',0}============================================================");
        Console.WriteLine($"  SCENARIO: {name.ToUpperInvariant()}");
        Console.WriteLine($"  ===========================================================");
        var sw = Stopwatch.StartNew();

        CurrentScenarioName = name;
        test();
        CurrentScenarioName = "";

        sw.Stop();
        Console.WriteLine($"  Wall time: {sw.ElapsedMilliseconds} ms");
    }

    // -------------------------------------------------------------------------
    // Scenario builders
    // -------------------------------------------------------------------------

    static void Sparse()
    {
        var cfg    = new PlacerConfig { CoolingRate = 0.999, StagnationIterations = 5000 };
        var labels = new List<LabelState>();
        var rng    = new Random(1);

        for (int row = 0; row < 5; row++)
        for (int col = 0; col < 4; col++)
        {
            double x = col * 50.0 + rng.NextDouble() * 2;
            double y = row * 20.0 + rng.NextDouble() * 2;
            labels.Add(MakeLabel($"PT{labels.Count+1}\n{y:F2}", x, y, textHeight: 2.5, cfg));
        }

        RunAndReport(labels, cfg, seed: 1);
        AssertZeroOverlap(labels, "Sparse");
    }

    static void Cluster()
    {
        var cfg    = new PlacerConfig();
        var labels = new List<LabelState>();

        for (int i = 0; i < 10; i++)
            labels.Add(MakeLabel($"STA{i+1}\n{(100.0 + i * 0.01):F2}", 0, 0, textHeight: 2.5, cfg));

        // Audit: print every 100 iterations so we see exactly when drift starts.
        RunAndReport(labels, cfg, seed: 2, verbose: true,
            auditInterval: 100,
            onAudit: (iter, running, trueE, drift) =>
            {
                if (Math.Abs(drift) > 1e-6)
                    Console.WriteLine($"  [AUDIT] iter={iter,6}  running={running,16:F4}  true={trueE,16:F4}  drift={drift,16:F4}  ***");
                else
                    Console.WriteLine($"  [AUDIT] iter={iter,6}  running={running,16:F4}  true={trueE,16:F4}  drift={drift,16:F6}");
            });

        AssertZeroOverlap(labels, "Cluster");
    }

    static void Dense()
    {
        var cfg    = new PlacerConfig { CoolingRate = 0.9998, StagnationIterations = 15000 };
        var labels = new List<LabelState>();
        var rng    = new Random(3);

        for (int i = 0; i < 100; i++)
        {
            double x = rng.NextDouble() * 30;
            double y = rng.NextDouble() * 30;
            labels.Add(MakeLabel($"PT{i+1}\n{y:F2}", x, y, textHeight: 2.0, cfg));
        }

        // Audit at coarser interval; only print when drift is non-trivial.
        RunAndReport(labels, cfg, seed: 3, verbose: true,
            auditInterval: 500,
            onAudit: (iter, running, trueE, drift) =>
            {
                string flag = Math.Abs(drift) > 1.0 ? "  ***" : "";
                Console.WriteLine($"  [AUDIT] iter={iter,8:N0}  running={running,16:F2}  true={trueE,16:F2}  drift={drift,14:F2}{flag}");
            });
    }

    static void Mixed()
    {
        var cfg    = new PlacerConfig { CoolingRate = 0.9997 };
        var labels = new List<LabelState>();
        var rng    = new Random(4);

        double[] gx = { 0, 100, 200,  50, 150 };
        double[] gy = { 0,   0,   0, 100, 100 };
        for (int g = 0; g < 5; g++)
        for (int m = 0; m < 4; m++)
            labels.Add(MakeLabel($"G{g}M{m}\n{gy[g]:F2}", gx[g], gy[g], 2.5, cfg));

        for (int i = 0; i < 130; i++)
        {
            double x = (i % 13) * 25.0 + rng.NextDouble() * 3;
            double y = (i / 13) * 30.0 + rng.NextDouble() * 3;
            labels.Add(MakeLabel($"BG{i}\n{y:F2}", x, y, 2.5, cfg));
        }

        RunAndReport(labels, cfg, seed: 4);
    }

    static void Stress()
    {
        var cfg = new PlacerConfig
        {
            CoolingRate          = 0.9999,
            StagnationIterations = 30000,
            Alpha                = 1000,
        };
        var labels = new List<LabelState>();
        var rng    = new Random(5);

        for (int i = 0; i < 500; i++)
        {
            double x = rng.NextDouble() * 50;
            double y = rng.NextDouble() * 50;
            labels.Add(MakeLabel($"S{i}\n{y:F1}", x, y, textHeight: 1.8, cfg));
        }

        Console.WriteLine($"  {labels.Count} labels in 50×50 region");
        PrintInitialDiagnostics(labels);
        RunAndReport(labels, cfg, seed: 5, verbose: true);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    static LabelState MakeLabel(string text, double ax, double ay,
                                double textHeight, PlacerConfig cfg)
    {
        var (w, h) = LabelState.EstimateSize(text, textHeight, cfg);
        return new LabelState(
            handle:     text,
            anchor:     new Point2D(ax, ay),
            width:      w,
            height:     h,
            sizeSource: LabelSizeSource.Estimated);
    }

    static void RunAndReport(
        List<LabelState> labels, PlacerConfig cfg, int seed,
        bool verbose = false,
        int auditInterval = 0,
        Action<int, double, double, double>? onAudit = null)
    {
        Console.WriteLine($"  {labels.Count} labels");
        PrintInitialDiagnostics(labels);

        if (SnapshotWriter != null)
            WriteSnapshot("start", 0, labels);

        var loop = new SALoop { ProgressInterval = 500 };

        Action<SAIterationLog>? iterCb = null;

        if (LogEveryIteration)
        {
            iterCb += (step) =>
            {
                char move = step.MoveKind switch
                {
                    SAMoveKind.Random => 'R',
                    SAMoveKind.Directional => 'D',
                    SAMoveKind.Swap => 'S',
                    SAMoveKind.SwapFallbackRandom => 'F',
                    _ => '?'
                };

                char stage = step.InStageB ? 'B' : 'A';
                char st = step.StageTransitioned ? '*' : ' ';
                string j = step.SwapPartnerIndex >= 0 ? step.SwapPartnerIndex.ToString() : "-";

                Console.WriteLine(
                    $"  iter={step.Iteration,8:N0}  T={step.Temperature,10:F6}  E={step.Energy,14:F2}  dE={step.DeltaEnergy,12:F2}  acc={(step.Accepted ? 1 : 0)}  move={move}  i={step.LabelIndex,4}  j={j,4}  stage={stage}{st}");
            };
        }

        if (SnapshotWriter != null && SnapshotInterval > 0)
        {
            iterCb += (step) =>
            {
                if (step.Iteration % SnapshotInterval == 0)
                    WriteSnapshot("iter", step.Iteration, labels);
            };
        }

        if (iterCb != null)
        {
            loop.OnIteration = iterCb;
            loop.IterationCallbackInterval = LogEveryIteration ? 1 : (SnapshotInterval > 0 ? SnapshotInterval : 0);
        }

        if (verbose)
        {
            loop.OnProgress = (iter, temp, energy, overlap) =>
                Console.WriteLine($"  iter={iter,8:N0}  T={temp,10:F6}  E={energy,14:F2}  overlap={overlap,10:F4}");
        }

        if (auditInterval > 0 && onAudit != null)
        {
            loop.AuditInterval  = auditInterval;
            loop.OnEnergyAudit  = onAudit;
        }

        var rng    = new Random(seed);
        var sw     = Stopwatch.StartNew();
        var result = loop.Run(labels, cfg, rng);
        sw.Stop();

        if (SnapshotWriter != null)
            WriteSnapshot("end", result.TotalIterations, labels);

        Console.WriteLine();
        Console.WriteLine($"  --- Result ---");
        Console.WriteLine($"  Iterations   : {result.TotalIterations:N0}");
        Console.WriteLine($"  Initial E    : {result.InitialEnergy:F4}");
        Console.WriteLine($"  Best E       : {result.BestEnergy:F4}");
        Console.WriteLine($"  Stop reason  : {(result.StagnationStop ? "stagnation" : "T < minTemp")}");
        Console.WriteLine($"  Zero overlap : {result.ZeroOverlapReached}");
        Console.WriteLine($"  Overlap area : {result.FinalOverlapArea:F6}");
        Console.WriteLine($"  Overlap pairs: {result.FinalOverlapPairs}");
        Console.WriteLine($"  Max pair ovlp: {result.FinalMaxPairOverlap:F6}");
        Console.WriteLine($"  SA time      : {sw.ElapsedMilliseconds} ms");
    }

    static void PrintInitialDiagnostics(List<LabelState> labels)
    {
        foreach (var ls in labels) ls.CurrentOffset = Vector2D.Zero;
        var (area, pairs, maxP) = EnergyDelta.OverlapDiagnostics(labels);
        Console.WriteLine($"  Initial overlap — area={area:F4}  pairs={pairs}  max={maxP:F4}");
    }

    static void AssertZeroOverlap(List<LabelState> labels, string scenarioName)
    {
        var (area, pairs, _) = EnergyDelta.OverlapDiagnostics(labels);
        string status = (area < 1e-9) ? "PASS" : $"FAIL (area={area:F6}, pairs={pairs})";
        Console.WriteLine($"  Zero-overlap assertion: {status}");
    }

    static void WriteSnapshot(string kind, int iteration, List<LabelState> labels)
    {
        if (SnapshotWriter == null) return;

        for (int i = 0; i < labels.Count; i++)
        {
            LabelState ls = labels[i];
            Rect2D r = ls.GetBoundingRect();

            string handle = ls.Handle?.ToString() ?? "";
            handle = handle.Replace("\r", "").Replace("\n", "\\n");

            SnapshotWriter.Write(CurrentScenarioName);
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(kind);
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(iteration.ToString(CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(i.ToString(CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(ls.GroupId.ToString(CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(ls.Anchor.X.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(ls.Anchor.Y.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(ls.CurrentOffset.X.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(ls.CurrentOffset.Y.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(ls.Width.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(ls.Height.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(r.Left.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(r.Bottom.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(r.Right.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(r.Top.ToString("G17", CultureInfo.InvariantCulture));
            SnapshotWriter.Write(',');
            SnapshotWriter.Write(CsvEscape(handle));
            SnapshotWriter.WriteLine();
        }

        SnapshotWriter.Flush();
    }

    static string CsvEscape(string s)
    {
        // Always quote; escape quotes by doubling.
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
