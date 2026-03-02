using System;

namespace LabelPlacer;

/// <summary>
/// Immutable 2-D point.
/// </summary>
public readonly struct Point2D
{
    public readonly double X;
    public readonly double Y;

    public Point2D(double x, double y) { X = x; Y = y; }

    public override string ToString() => $"({X:F4}, {Y:F4})";
}

/// <summary>
/// Mutable 2-D vector used as a label offset.
/// </summary>
public struct Vector2D
{
    public double X;
    public double Y;

    public Vector2D(double x, double y) { X = x; Y = y; }

    public static Vector2D Zero => new Vector2D(0, 0);

    public override string ToString() => $"[{X:F4}, {Y:F4}]";
}

/// <summary>
/// Axis-aligned bounding rectangle.
/// Convention: origin is at the bottom-left corner; Y increases upward.
/// </summary>
public readonly struct Rect2D
{
    public readonly double Left;
    public readonly double Bottom;
    public readonly double Right;
    public readonly double Top;

    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public double CenterX => (Left + Right) * 0.5;
    public double CenterY => (Bottom + Top) * 0.5;

    public Rect2D(double left, double bottom, double right, double top)
    {
        Left = left;
        Bottom = bottom;
        Right = right;
        Top = top;
    }

    /// <summary>
    /// Construct from center point and half-extents.
    /// </summary>
    public static Rect2D FromCenter(double cx, double cy, double halfW, double halfH)
        => new Rect2D(cx - halfW, cy - halfH, cx + halfW, cy + halfH);

    /// <summary>
    /// Axis-aligned overlap area between two rectangles. Returns 0 if they do not overlap.
    /// </summary>
    public static double OverlapArea(Rect2D a, Rect2D b)
    {
        double ox = Math.Max(0.0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double oy = Math.Max(0.0, Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom));
        return ox * oy;
    }

    /// <summary>Smallest AABB that contains both rectangles.</summary>
    public static Rect2D Union(Rect2D a, Rect2D b)
        => new Rect2D(
            Math.Min(a.Left,   b.Left),
            Math.Min(a.Bottom, b.Bottom),
            Math.Max(a.Right,  b.Right),
            Math.Max(a.Top,    b.Top));

    public override string ToString()
        => $"Rect[L={Left:F4} B={Bottom:F4} R={Right:F4} T={Top:F4}]";
}

/// <summary>
/// Describes how the label's computed width and height were determined.
/// </summary>
public enum LabelSizeSource
{
    /// <summary>Exact extents obtained from the Civil 3D label API.</summary>
    ApiExtents,

    /// <summary>Estimated via the fallback character-count estimator with safety margins.</summary>
    Estimated,
}

/// <summary>
/// Mutable state for a single label during the SA optimization.
///
/// Geometry convention (internal, independent of Civil 3D label offset semantics):
///   The anchor is at the center of the left edge of the label.
///   The bounding rectangle center is at (Anchor + CurrentOffset + (Width/2, 0)).
///   The Civil 3D wrapper is responsible for converting between this convention
///   and whatever offset reference point Civil 3D uses (leader landing, corner, etc.).
/// </summary>
public sealed class LabelState
{
    // -------------------------------------------------------------------------
    // Identity / grouping
    // -------------------------------------------------------------------------

    /// <summary>Opaque handle that the Civil 3D wrapper uses to identify this label.</summary>
    public object Handle { get; }

    /// <summary>
    /// Index of the coincident group this label belongs to.
    /// Labels in the same group share the same (or very close) anchor XY.
    /// -1 means the label is not in any coincident group (it is the sole label at its anchor).
    /// </summary>
    public int GroupId { get; set; } = -1;

    // -------------------------------------------------------------------------
    // Anchor (read-only during optimization)
    // -------------------------------------------------------------------------

    /// <summary>XY position of the associated point / object. Never modified by the placer.</summary>
    public Point2D Anchor { get; }

    // -------------------------------------------------------------------------
    // Label dimensions (set once, immutable during optimization)
    // -------------------------------------------------------------------------

    /// <summary>Width of the label bounding rectangle (with safety margins already applied).</summary>
    public double Width { get; }

    /// <summary>Height of the label bounding rectangle (with safety margins already applied).</summary>
    public double Height { get; }

    /// <summary>How the width/height were determined.</summary>
    public LabelSizeSource SizeSource { get; }

    // -------------------------------------------------------------------------
    // Mutable offset (written by the SA loop)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Current label offset from the anchor, expressed in drawing units.
    /// The bounding rectangle center is at Anchor + CurrentOffset + (Width/2, 0).
    /// Reset to zero at the start of every run (cold start).
    /// </summary>
    public Vector2D CurrentOffset { get; set; }

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public LabelState(
        object handle,
        Point2D anchor,
        double width,
        double height,
        LabelSizeSource sizeSource = LabelSizeSource.Estimated)
    {
        Handle = handle;
        Anchor = anchor;
        Width = width;
        Height = height;
        SizeSource = sizeSource;
        CurrentOffset = Vector2D.Zero;
    }

    // -------------------------------------------------------------------------
    // Geometry helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the bounding rectangle for the label at its current offset.
    /// Center = Anchor + CurrentOffset + (Width/2, 0).
    /// </summary>
    public Rect2D GetBoundingRect()
    {
        double left = Anchor.X + CurrentOffset.X;
        double cy = Anchor.Y + CurrentOffset.Y;
        return new Rect2D(left, cy - Height * 0.5, left + Width, cy + Height * 0.5);
    }

    /// <summary>
    /// Returns the bounding rectangle as if the label were at a hypothetical offset
    /// (used by the SA loop when evaluating proposed moves without committing).
    /// </summary>
    public Rect2D GetBoundingRectAt(Vector2D proposedOffset)
    {
        double left = Anchor.X + proposedOffset.X;
        double cy = Anchor.Y + proposedOffset.Y;
        return new Rect2D(left, cy - Height * 0.5, left + Width, cy + Height * 0.5);
    }

    /// <summary>
    /// Computes the absolute Y clamp limit for this label based on config.
    /// </summary>
    public double MaxAbsoluteY(PlacerConfig cfg) => cfg.MaxVerticalDisplacementFactor * Height;

    // -------------------------------------------------------------------------
    // Fallback size estimator (static factory helper)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Estimate label dimensions from text content when Civil 3D API extents are unavailable.
    ///
    /// width  = longestLineCharCount × textHeight × charWidthFactor  × sizeSafetyFactorX
    /// height = lineCount            × textHeight × lineSpacingFactor × sizeSafetyFactorY
    /// </summary>
    public static (double width, double height) EstimateSize(
        string labelText,
        double textHeight,
        PlacerConfig cfg)
    {
        if (string.IsNullOrEmpty(labelText))
            return (textHeight * cfg.SizeSafetyFactorX, textHeight * cfg.SizeSafetyFactorY);

        string[] lines = labelText.Split('\n');
        int lineCount = lines.Length;
        int maxChars = 0;
        foreach (string line in lines)
            if (line.Length > maxChars) maxChars = line.Length;

        double width = maxChars * textHeight * cfg.CharWidthFactor * cfg.SizeSafetyFactorX;
        double height = lineCount * textHeight * cfg.LineSpacingFactor * cfg.SizeSafetyFactorY;

        return (width, height);
    }

    public override string ToString()
        => $"Label[{Handle} anchor={Anchor} offset={CurrentOffset} group={GroupId} {Width:F3}×{Height:F3}]";
}
