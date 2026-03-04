using System.Collections.Generic;

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
/// Three horizontal bands are offered:
///   - Right  (dx = 0):           anchor at left edge — CAD default
///   - Left   (dx ≈ -W):          label entirely to the left of the anchor
///   - Far-right (dx ≈ +W/2):     label pushed further right
///
/// Band order depends on <paramref name="preferLeft"/>:
///   false (default) → Right first, then Left, then Far-right
///   true            → Left first, then Right, then Far-right
///
/// Within each band, vertical levels are spaced 1.05 × height apart so that
/// adjacent levels never overlap one another.
///
/// Candidates are assigned preference scores 0, 1, 2, … in output order
/// so that earlier (more preferred) entries beat later ones in the composite
/// greedy score when overlap and leader-cross counts tie.
/// </summary>
public static class CandidateGenerator
{
    /// <summary>
    /// Generates candidates for <paramref name="label"/>.
    /// </summary>
    /// <param name="label">The label being placed.</param>
    /// <param name="preferLeft">
    ///   When true, left-side candidates are listed before right-side ones.
    ///   Pass true when more anchor neighbours lie to the right of this anchor
    ///   (so placing the label left moves it away from the crowd).
    /// </param>
    public static LabelCandidate[] Generate(LabelState label, bool preferLeft = false)
    {
        double w     = label.Width;
        double h     = label.Height;
        double vStep = h * 1.05;          // non-overlapping vertical step
        double hGap  = w * 0.05;          // small gap for left/far-right bands
        double dxLeft = -(w + hGap);      // left band: label entirely left of anchor
        double dxFarR =  w * 0.5 + hGap; // far-right band

        var    buf   = new List<LabelCandidate>(21);
        double score = 0.0;

        void Add(double dx, double dy)
            => buf.Add(new LabelCandidate(new Vector2D(dx, dy), score++));

        void AddRightBand()
        {
            Add(0, 0);
            Add(0, +vStep);   Add(0, -vStep);
            Add(0, +vStep*2); Add(0, -vStep*2);
            Add(0, +vStep*3); Add(0, -vStep*3);
            Add(0, +vStep*4); Add(0, -vStep*4);
        }

        void AddLeftBand()
        {
            Add(dxLeft, 0);
            Add(dxLeft, +vStep);   Add(dxLeft, -vStep);
            Add(dxLeft, +vStep*2); Add(dxLeft, -vStep*2);
            Add(dxLeft, +vStep*3); Add(dxLeft, -vStep*3);
            Add(dxLeft, +vStep*4); Add(dxLeft, -vStep*4);
        }

        void AddFarRightBand()
        {
            Add(dxFarR, 0);
            Add(dxFarR, +vStep); Add(dxFarR, -vStep);
        }

        if (preferLeft)
        {
            AddLeftBand();
            AddRightBand();
            AddFarRightBand();
        }
        else
        {
            AddRightBand();
            AddLeftBand();
            AddFarRightBand();
        }

        return buf.ToArray(); // 21 entries
    }
}
