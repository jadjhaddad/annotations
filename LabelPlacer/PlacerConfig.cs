namespace LabelPlacer;

/// <summary>
/// All tunable parameters for the SA label placer.
/// Weights: alpha >> gamma >> beta_y >> beta_x
/// </summary>
public sealed class PlacerConfig
{
    // --- Energy weights ---

    /// <summary>Overlap area penalty weight. Must dominate all other terms.</summary>
    public double Alpha { get; set; } = 1000.0;

    /// <summary>Horizontal displacement penalty weight.</summary>
    public double BetaX { get; set; } = 1.0;

    /// <summary>Vertical displacement penalty weight. Much larger than BetaX to prefer horizontal solutions.</summary>
    public double BetaY { get; set; } = 10.0;

    /// <summary>Intragroup spread penalty weight.</summary>
    public double Gamma { get; set; } = 100.0;

    // --- Two-stage weights (Stage B, applied after overlaps reach zero) ---

    /// <summary>Alpha in Stage B (beautify). Slightly reduced from Stage A.</summary>
    public double AlphaStageB { get; set; } = 800.0;

    /// <summary>BetaX in Stage B.</summary>
    public double BetaXStageB { get; set; } = 2.0;

    /// <summary>BetaY in Stage B.</summary>
    public double BetaYStageB { get; set; } = 200.0;

    /// <summary>Gamma in Stage B.</summary>
    public double GammaStageB { get; set; } = 150.0;

    // --- Cooling schedule ---

    /// <summary>Geometric cooling multiplier applied each iteration.</summary>
    public double CoolingRate { get; set; } = 0.9995;

    /// <summary>Temperature at which the SA loop terminates.</summary>
    public double MinTemp { get; set; } = 0.01;

    // --- Stopping ---

    /// <summary>Stop if best energy has not improved for this many consecutive iterations.</summary>
    public int StagnationIterations { get; set; } = 20_000;

    // --- Movement constraints ---

    /// <summary>
    /// If true, labels that share the same anchor are treated as a single block
    /// during optimization (all members move together).
    /// </summary>
    public bool StackLabelsByAnchor { get; set; } = true;

    /// <summary>
    /// If true, stacked blocks must preserve anchor-Y order (no vertical crossing).
    /// Deprecated: soft ordering via <see cref="OrderingPenaltyWeight"/> is now always
    /// applied when <see cref="StackLabelsByAnchor"/> is true. This flag is ignored.
    /// </summary>
    public bool EnforceAnchorOrder { get; set; } = false;

    /// <summary>
    /// Energy penalty per world-unit of block-center crossing in Stage A.
    /// A small value (≪ Alpha) gently steers Stage A toward ordering-friendly
    /// solutions without blocking overlap clearing.
    /// Stage B uses <see cref="OrderingPenaltyWeightStageB"/> instead.
    /// </summary>
    public double OrderingPenaltyWeight { get; set; } = 20.0;

    /// <summary>
    /// Energy penalty per world-unit of block-center crossing in Stage B (beautify phase).
    /// Applied after overlaps reach zero. Should be large enough to prevent crossings
    /// at the final low temperatures, but not so large as to overwhelm the alpha term
    /// (which must still be able to clear any small overlaps reintroduced by displacement pull-back).
    /// </summary>
    public double OrderingPenaltyWeightStageB { get; set; } = 1000.0;


    /// <summary>
    /// Maximum absolute Y offset as a multiple of the label height.
    /// e.g. 2.0 means the label can move at most 2× its own height vertically.
    /// </summary>
    public double MaxVerticalDisplacementFactor { get; set; } = 6.0;

    /// <summary>
    /// Maximum absolute Y offset for stacked anchor blocks as a multiple of the
    /// minimum vertical spacing between distinct anchors.
    /// </summary>
    public double MaxBlockDisplacementFactor { get; set; } = 1.0;

    // --- Label size estimation (fallback when API extents unavailable) ---

    /// <summary>Multiplier for line spacing when estimating label height.</summary>
    public double LineSpacingFactor { get; set; } = 1.2;

    /// <summary>Per-character width multiplier when estimating label width.</summary>
    public double CharWidthFactor { get; set; } = 0.6;

    /// <summary>Safety margin on estimated width to prevent visual touching.</summary>
    public double SizeSafetyFactorX { get; set; } = 1.10;

    /// <summary>Safety margin on estimated height to prevent visual touching.</summary>
    public double SizeSafetyFactorY { get; set; } = 1.15;

    // --- Coincident grouping ---

    /// <summary>
    /// Tolerance = coincidenceFactor × medianLabelHeight.
    /// Labels whose anchors are within this distance are considered coincident.
    /// </summary>
    public double CoincidenceFactor { get; set; } = 0.5;

    // --- Move mix (unnormalized weights; will be normalized internally) ---

    /// <summary>Relative weight for random small perturbation moves.</summary>
    public double MoveWeightRandom { get; set; } = 0.6;

    /// <summary>Relative weight for directional moves (nudge away from worst overlapping neighbor).</summary>
    public double MoveWeightDirectional { get; set; } = 0.3;

    /// <summary>Relative weight for swap-within-group moves.</summary>
    public double MoveWeightSwap { get; set; } = 0.1;

    // --- Warmup ---

    /// <summary>Number of random perturbations to sample during warmup to determine T₀.</summary>
    public int WarmupSamples { get; set; } = 500;

    /// <summary>Target initial acceptance probability for uphill moves (~80%).</summary>
    public double WarmupTargetAcceptance { get; set; } = 0.8;
}
