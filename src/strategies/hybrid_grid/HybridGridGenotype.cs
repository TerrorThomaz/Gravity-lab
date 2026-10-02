namespace GravityGen2.Strategies.HybridGrid;

// Which sides a HybridGrid run may take. A RUN option, not a gene: keeping it out of the genome
// means LongOnly vs Both can be trained and compared as separate searches, so "does the short side
// add anything?" is a measured question rather than something the GA quietly answers by switching
// a side off (which would also hide the extra trials it spent finding that out).
public enum HybridGridSides { Both, LongOnly, ShortOnly }

// HybridGrid: Grid's honest resting-order ladder + AccumulationGrid's EMA anchor and trailing stop,
// with the SIDE chosen by where price sits relative to the EMA.
//
//   close > EMA + band·ATR and EMA rising   → LONG ladder:  buy-limits at EMA − k·step·ATR
//   close < EMA − band·ATR and EMA falling  → SHORT ladder: sell-limits at EMA + k·step·ATR
//   otherwise (near a flat EMA)             → no arm — that is Grid/GridShort's ranging territory
//
// 10 genes. Every one added to a GA deflates the book's Sharpe through the trial count, so the
// RegimeClassifier sustain gate AccumulationGrid used is replaced by the EMA slope + ADX floor
// rather than layered on top of it.
public sealed record HybridGridGenotype
{
    public int    EmaPeriod         { get; init; } = 50;    // anchor + side-selection EMA
    public int    SlopeLookback     { get; init; } = 20;    // bars over which EMA slope is measured
    public double SlopeMinPct       { get; init; } = 0.005; // |slope| must exceed this (fraction) to arm
    public double BiasBandAtr       { get; init; } = 0.5;   // close must be this many ATR beyond the EMA
    public double AdxMin            { get; init; } = 15.0;  // trend-strength floor to arm (0 = off)
    public double GridStepAtrMult   { get; init; } = 1.0;   // rung spacing from the EMA anchor
    public int    GridLevels        { get; init; } = 3;     // rungs, 1..MaxLevels
    public double TakeProfitAtrMult { get; init; } = 2.0;   // per-rung target, ATR frozen at fill
    public double TrailStopAtrMult  { get; init; } = 3.0;   // trailing stop off the best close
    public int    MaxHoldBars       { get; init; } = 120;

    public const int MaxLevels  = 5;
    public const int GeneCount  = 10;

    public static readonly (double Lo, double Hi)[] Bounds =
    [
        (20, 200),    // EmaPeriod
        (5, 60),      // SlopeLookback
        (0.0, 0.03),  // SlopeMinPct
        (0.0, 2.0),   // BiasBandAtr
        (0.0, 40.0),  // AdxMin
        (0.3, 3.0),   // GridStepAtrMult
        (1, MaxLevels), // GridLevels
        (0.5, 5.0),   // TakeProfitAtrMult
        (1.0, 6.0),   // TrailStopAtrMult
        (24, 300),    // MaxHoldBars
    ];

    public double[] ToVector() =>
    [
        EmaPeriod, SlopeLookback, SlopeMinPct, BiasBandAtr, AdxMin,
        GridStepAtrMult, GridLevels, TakeProfitAtrMult, TrailStopAtrMult, MaxHoldBars,
    ];

    public static HybridGridGenotype FromVector(double[] v)
    {
        double C(int i) => Math.Clamp(v[i], Bounds[i].Lo, Bounds[i].Hi);
        return new HybridGridGenotype
        {
            EmaPeriod         = (int)Math.Round(C(0)),
            SlopeLookback     = (int)Math.Round(C(1)),
            SlopeMinPct       = C(2),
            BiasBandAtr       = C(3),
            AdxMin            = C(4),
            GridStepAtrMult   = C(5),
            GridLevels        = (int)Math.Round(C(6)),
            TakeProfitAtrMult = C(7),
            TrailStopAtrMult  = C(8),
            MaxHoldBars       = (int)Math.Round(C(9)),
        };
    }
}
