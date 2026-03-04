namespace LabelPlacer;

/// <summary>
/// A discrete candidate placement offset for a label.
/// Candidates are returned in preference order (lowest score = most preferred).
/// </summary>
public readonly struct LabelCandidate
{
    public readonly Vector2D Offset;

    /// <summary>Preference score — lower is better.</summary>
    public readonly double Score;

    public LabelCandidate(Vector2D offset, double score)
    {
        Offset = offset;
        Score  = score;
    }
}

/// <summary>
/// Generates an ordered list of discrete candidate placement offsets for a label.
///
/// Candidates are arranged in three horizontal bands:
///   - Right  (dx = 0):         anchor at left edge — CAD default, most preferred
///   - Left   (dx = -W*1.05):   label to the left of the anchor
///   - Far-right (dx = +W*0.55): label pushed further right
///
/// Each band has multiple vertical levels spaced 1.05 × label height apart
/// so adjacent levels never overlap one another.
///
/// Candidates are returned pre-sorted from most preferred to least preferred.
/// </summary>
public static class CandidateGenerator
{
    public static LabelCandidate[] Generate(LabelState label)
    {
        double w     = label.Width;
        double h     = label.Height;
        double vStep = h * 1.05;          // one non-overlapping vertical level
        double hGap  = w * 0.05;          // small horizontal gap for left/far-right
        double dxLeft  = -(w + hGap);     // left band: label entirely left of anchor
        double dxFarR  =  (w * 0.5 + hGap); // far-right band: pushed further right

        double score = 0.0;
        var buf = new LabelCandidate[17];
        int n = 0;

        void Add(double dx, double dy)
            => buf[n++] = new LabelCandidate(new Vector2D(dx, dy), score++);

        // --- Right band (preferred) — 9 candidates ---
        Add(0,   0);
        Add(0,  +vStep);
        Add(0,  -vStep);
        Add(0,  +vStep * 2);
        Add(0,  -vStep * 2);
        Add(0,  +vStep * 3);
        Add(0,  -vStep * 3);
        Add(0,  +vStep * 4);
        Add(0,  -vStep * 4);

        // --- Left band — 5 candidates ---
        Add(dxLeft,  0);
        Add(dxLeft, +vStep);
        Add(dxLeft, -vStep);
        Add(dxLeft, +vStep * 2);
        Add(dxLeft, -vStep * 2);

        // --- Far-right band — 3 candidates ---
        Add(dxFarR,  0);
        Add(dxFarR, +vStep);
        Add(dxFarR, -vStep);

        return buf; // exactly 17 entries
    }
}
