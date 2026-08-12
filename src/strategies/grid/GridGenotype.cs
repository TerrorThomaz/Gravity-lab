namespace TradingGA;

// Grid trading genotype — long grid in ranging markets.
//
// Regime: ADX(14) < AdxThreshold AND BbWidth(BbPeriod) < BbWidthMaxPct → ranging, grid fires.
//         Both conditions must hold at entry.
//         ADX(14) is fixed (not a gene) — shared indicator with swing, same value at every candle.
//         BbWidth = (UpperBB − LowerBB) / MiddleBB × 100 — measures price compression.
//         Exit during active grid: ADX(14) ≥ AdxThreshold → trending, close all.
//
// Regime partition vs swing:
//   Swing fires when ADX(14) ≥ swing.AdxThreshold (typically 18–24).
//   Grid fires when ADX(14) < grid.AdxThreshold (hard upper bound 20).
//   This ensures grid always sits below swing's entry zone — no overlap.
//   RunGridTrain loads the live swing genotype and enforces this ceiling dynamically.
//
// Grid: buy at fixed ATR-spaced levels below the EMA anchor.
//       Each level closes independently when price recovers TakeProfitAtrMult × ATR above it.
//       Hard stop at HardStopAtrMult × ATR below anchor — exits all when range breaks down.
//
// Gene units:
//   AdxThreshold             — ranging regime upper bound: grid fires when ADX(14) < this [8–20]
//   BbPeriod                 — Bollinger Band period for compression detection [10–50]
//   BbWidthMaxPct            — maximum BB width % — tight to force genuine ranging [0.8–2.5]
//   EmaPeriod                — EMA anchor for grid centre [10–100]
//   GridStepAtrMult          — ATR-scaled spacing between grid levels [0.3–2.0]
//   GridLevels               — number of buy levels below EMA [1–3]
//   TakeProfitAtrMult        — per-level profit target above each fill [0.5–3.0]
//   HardStopAtrMult          — hard stop below EMA anchor [1.5–3.0]
//   BailOutAtrMult           — close all when price drops this far below lowest filled level [1.0–4.0]
//   MaxHoldCandles           — h1 bars before force-close [24–200]
public class GridGenotype
{
    // ── Regime (coordinated with swing's ADX threshold — see RunGridTrain) ────────
    public double AdxThreshold    { get; set; }  // 8–20   grid fires when ADX(14) < this
    public int    BbPeriod        { get; set; }  // 10–50  BB period for compression gate
    public double BbWidthMaxPct   { get; set; }  // 0.8–2.5 max BB width % — genuine range only
    public int    EmaPeriod       { get; set; }  // 10–100 range centre reference

    // ── Grid structure ────────────────────────────────────────────────────────────
    public double GridStepAtrMult  { get; set; }  // 0.3–2.0 level spacing
    public int    GridLevels       { get; set; }  // 1–3     buy levels below EMA
    public double TakeProfitAtrMult { get; set; } // 0.5–3.0 per-level TP above fill
    public double HardStopAtrMult  { get; set; }  // 1.5–3.0 hard stop below anchor — catastrophic break
    public double BailOutAtrMult   { get; set; }  // 1.0–4.0 close all when price drops this far below lowest filled level
    public int    MaxHoldCandles   { get; set; }  // 24–200  h1 bars before force-close

    // ── Grid-bot mechanics, added 2026-08 ────────────────────────────────────────────────────
    // RungSellFrac: sell target as a FRACTION OF THE RUNG SPACING rather than a free ATR target.
    // 1.0 = a true grid bot — buy a rung, sell at the rung above, capture exactly one step.
    // 0 = disabled, keep the legacy TakeProfitAtrMult behaviour.
    //
    // Why this exists: the legacy exit sells at entry + TakeProfitAtrMult x ATR, which on the
    // trained genotype is 2.43x the rung spacing — FURTHER than the entire 3-level ladder is deep
    // (2.04 ATR). So a fill had to ride a sustained directional move to exit, while the Ranging
    // gate exists precisely to select against sustained moves. The entry regime and the exit
    // target were pulling in opposite directions.
    public double RungSellFrac   { get; set; }  // 0 = legacy TP · 0.5–2.0 = multiples of a rung

    // ReanchorAlpha: EMA-following rate for the anchor while a session is open. 0 = the legacy
    // static anchor fixed at activation; higher values track the moving EMA, as
    // AccumulationGridSimulator already does. A static ladder is stranded when price trends away
    // from it, which is the structural difference between Grid and the accumulator — and the
    // accumulator is the component that validated OOS (-3.47% vs trailing EMA on 27/27 coins).
    public double ReanchorAlpha  { get; set; }  // 0–0.2 per-bar pull toward the live EMA

    // The direction bias was a HARDCODED veto: `if (emaSlope < -0.005) continue`, with a fixed
    // 20-bar lookback. Neither number was searchable, so the GA could not tune how directional
    // the grid should be — only whether the constants happened to suit.
    // Bounded at or below ZERO. A positive threshold would demand a RISING market to open, turning
    // a Ranging-gated grid into a trend follower and contradicting its own entry regime. The gene
    // tunes how much decline to tolerate, not whether to require an advance.
    public double SlopeThreshold { get; set; } = -0.005;  // -0.03–0.0 min EMA slope to open
    public int    SlopeLookback  { get; set; }  // 5–60 bars

    public double Fitness { get; set; } = double.MinValue;

    // ── Seeded initialisation ────────────────────────────────────────────────────
    // Probability that a seeded Random draw returns a LOOSE mutant of the seed
    // rather than an independent uniform draw. Matches RegimeRouterGenotype.Random,
    // which sits on the identical GA Run skeleton; one number across the whole
    // strategy suite keeps the initial-diversity mix comparable between GAs.
    //
    // Resulting population mix at popSize 80 with a seed (the GA's Run block
    // installs the clamped seed at index 0 and tight rate-0.25 mutants at 1..16):
    //   1  exact seed
    //   16 tight  (rate 0.25) mutants  — the seed's immediate neighbourhood
    //   ~19 loose (rate 0.50) mutants  — 30% of the remaining 63 slots
    //   ~44 fully independent random genotypes
    // ≈ 45% anchored on the incumbent, ≈ 55% genuine exploration. Before this
    // change the last 63 slots were byte-identical copies of the seed, leaving
    // at most 17 distinct starting points (78.75% duplicates) — a hill-climb,
    // not a GA.
    private const double SeedMutantProbability = 0.3;

    // adxCeiling: dynamic upper bound loaded from swing genotype at train time.
    // Pass swing.AdxThreshold − 1 so the two strategies never overlap. The
    // seed-mutant branch threads it through both the clamp and the mutation so a
    // seeded draw can never breach the grid/swing regime partition either.
    public static GridGenotype Random(System.Random rng, GridGenotype? seed = null, double adxCeiling = 20.0)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBounds(adxCeiling).Mutate(rng, 0.5, adxCeiling);

        double adxMax = Math.Min(adxCeiling, 20.0);
        double adxMin = 8.0;
        return new()
        {
            AdxThreshold     = adxMin + rng.NextDouble() * (adxMax - adxMin),
            BbPeriod         = rng.Next(10, 51),
            BbWidthMaxPct    = 0.8  + rng.NextDouble() * 1.7,
            EmaPeriod        = rng.Next(10, 101),
            GridStepAtrMult  = 0.3  + rng.NextDouble() * 1.7,
            GridLevels       = rng.Next(1, 4),
            TakeProfitAtrMult= 0.5  + rng.NextDouble() * 2.5,
            HardStopAtrMult  = 1.5  + rng.NextDouble() * 1.5,
            BailOutAtrMult   = 1.0  + rng.NextDouble() * 3.0,
            MaxHoldCandles   = rng.Next(24, 201),
            RungSellFrac     = rng.NextDouble() < 0.5 ? 0.0 : 0.5 + rng.NextDouble() * 1.5,
            ReanchorAlpha    = rng.NextDouble() < 0.5 ? 0.0 : rng.NextDouble() * 0.2,
            SlopeThreshold   = -0.03 + rng.NextDouble() * 0.03,
            SlopeLookback    = rng.Next(5, 61),
        };
    }

    public static GridGenotype Crossover(GridGenotype a, GridGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            AdxThreshold     = Pick(a.AdxThreshold,      b.AdxThreshold),
            BbPeriod         = Pick(a.BbPeriod,           b.BbPeriod),
            BbWidthMaxPct    = Pick(a.BbWidthMaxPct,     b.BbWidthMaxPct),
            EmaPeriod        = Pick(a.EmaPeriod,          b.EmaPeriod),
            GridStepAtrMult  = Pick(a.GridStepAtrMult,   b.GridStepAtrMult),
            GridLevels       = Pick(a.GridLevels,         b.GridLevels),
            TakeProfitAtrMult= Pick(a.TakeProfitAtrMult, b.TakeProfitAtrMult),
            HardStopAtrMult  = Pick(a.HardStopAtrMult,   b.HardStopAtrMult),
            BailOutAtrMult   = Pick(a.BailOutAtrMult,    b.BailOutAtrMult),
            MaxHoldCandles   = Pick(a.MaxHoldCandles,    b.MaxHoldCandles),
            RungSellFrac     = Pick(a.RungSellFrac,      b.RungSellFrac),
            ReanchorAlpha    = Pick(a.ReanchorAlpha,     b.ReanchorAlpha),
            SlopeThreshold   = Pick(a.SlopeThreshold,    b.SlopeThreshold),
            SlopeLookback    = Pick(a.SlopeLookback,     b.SlopeLookback),
        };
    }

    public GridGenotype Mutate(System.Random rng, double rate, double adxCeiling = 20.0)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, int min, int max, int step = 3)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + rng.Next(-step, step + 1), min, max);
        }
        return new GridGenotype
        {
            AdxThreshold     = Nudge(AdxThreshold,      8.0, Math.Min(adxCeiling, 20.0), 2.0),
            BbPeriod         = NudgeInt(BbPeriod,        10,   50,  5),
            BbWidthMaxPct    = Nudge(BbWidthMaxPct,      0.8,  2.5, 0.3),
            EmaPeriod        = NudgeInt(EmaPeriod,        10,  100, 10),
            GridStepAtrMult  = Nudge(GridStepAtrMult,    0.3,  2.0, 0.3),
            GridLevels       = NudgeInt(GridLevels,        1,    3,   1),
            TakeProfitAtrMult= Nudge(TakeProfitAtrMult,  0.5,  3.0, 0.4),
            HardStopAtrMult  = Nudge(HardStopAtrMult,    1.5,  3.0, 0.4),
            BailOutAtrMult   = Nudge(BailOutAtrMult,     1.0,  4.0, 0.5),
            MaxHoldCandles   = NudgeInt(MaxHoldCandles,  24,  200,  12),
            RungSellFrac     = Nudge(RungSellFrac,       0.0,  2.0, 0.3),
            ReanchorAlpha    = Nudge(ReanchorAlpha,      0.0,  0.2, 0.04),
            SlopeThreshold   = Nudge(SlopeThreshold,   -0.03,  0.0, 0.008),
            SlopeLookback    = NudgeInt(SlopeLookback,     5,   60,   8),
        };
    }

    public GridGenotype ClampToBounds(double adxCeiling = 20.0) => new()
    {
        AdxThreshold     = Math.Clamp(AdxThreshold,     8.0, Math.Min(adxCeiling, 20.0)),
        BbPeriod         = Math.Clamp(BbPeriod,          10,   50),
        BbWidthMaxPct    = Math.Clamp(BbWidthMaxPct,     0.8,  2.5),
        EmaPeriod        = Math.Clamp(EmaPeriod,         10,  100),
        GridStepAtrMult  = Math.Clamp(GridStepAtrMult,   0.3,  2.0),
        GridLevels       = Math.Clamp(GridLevels,         1,    3),
        TakeProfitAtrMult= Math.Clamp(TakeProfitAtrMult, 0.5,  3.0),
        HardStopAtrMult  = Math.Clamp(HardStopAtrMult,   1.5,  3.0),
        BailOutAtrMult   = Math.Clamp(BailOutAtrMult,    1.0,  4.0),
        MaxHoldCandles   = Math.Clamp(MaxHoldCandles,    24,  200),
        RungSellFrac     = Math.Clamp(RungSellFrac,      0.0,  2.0),
        ReanchorAlpha    = Math.Clamp(ReanchorAlpha,     0.0,  0.2),
        SlopeThreshold   = Math.Clamp(SlopeThreshold,  -0.03,  0.0),
        SlopeLookback    = Math.Clamp(SlopeLookback,       5,   60),
        Fitness = Fitness,
    };

    // ── Bayesian optimiser interface ──────────────────────────────────────────
    // adxCeiling is dynamic (from swing genotype), so we use 20.0 as the static upper bound.
    public static readonly double[,] Bounds =
    {
        {  8.0, 20.0 }, // AdxThreshold
        {   10,   50 }, // BbPeriod
        {  0.8,  2.5 }, // BbWidthMaxPct
        {   10,  100 }, // EmaPeriod
        {  0.3,  2.0 }, // GridStepAtrMult
        {    1,    3 }, // GridLevels
        {  0.5,  3.0 }, // TakeProfitAtrMult
        {  1.5,  3.0 }, // HardStopAtrMult
        {  1.0,  4.0 }, // BailOutAtrMult
        {   24,  200 }, // MaxHoldCandles
        {  0.0,  2.0 }, // RungSellFrac   — 0 disables, else multiples of the rung spacing
        {  0.0,  0.2 }, // ReanchorAlpha  — 0 = static anchor (legacy)
        {-0.03,  0.0 }, // SlopeThreshold — was hardcoded -0.005; never POSITIVE (see below)
        {    5,   60 }, // SlopeLookback  — was hardcoded 20
    };

    public static readonly string[] ParameterNames =
    [
        "AdxThreshold", "BbPeriod", "BbWidthMaxPct", "EmaPeriod",
        "GridStepAtrMult", "GridLevels", "TakeProfitAtrMult",
        "HardStopAtrMult", "BailOutAtrMult", "MaxHoldCandles",
    ];

    public double[] ToVector() =>
    [
        AdxThreshold, BbPeriod, BbWidthMaxPct, EmaPeriod,
        GridStepAtrMult, GridLevels, TakeProfitAtrMult,
        HardStopAtrMult, BailOutAtrMult, MaxHoldCandles,
        RungSellFrac, ReanchorAlpha, SlopeThreshold, SlopeLookback,
    ];

    public static GridGenotype FromVector(double[] v, double adxCeiling = 20.0) => new()
    {
        AdxThreshold      = Math.Clamp(v[0],  8.0, Math.Min(adxCeiling, 20.0)),
        BbPeriod          = Math.Clamp((int)Math.Round(v[1]),  10,  50),
        BbWidthMaxPct     = Math.Clamp(v[2],  0.8,  2.5),
        EmaPeriod         = Math.Clamp((int)Math.Round(v[3]),  10, 100),
        GridStepAtrMult   = Math.Clamp(v[4],  0.3,  2.0),
        GridLevels        = Math.Clamp((int)Math.Round(v[5]),   1,   3),
        TakeProfitAtrMult = Math.Clamp(v[6],  0.5,  3.0),
        HardStopAtrMult   = Math.Clamp(v[7],  1.5,  3.0),
        BailOutAtrMult    = Math.Clamp(v[8],  1.0,  4.0),
        MaxHoldCandles    = Math.Clamp((int)Math.Round(v[9]),  24, 200),
        RungSellFrac      = Math.Clamp(v[10], 0.0,  2.0),
        ReanchorAlpha     = Math.Clamp(v[11], 0.0,  0.2),
        SlopeThreshold    = Math.Clamp(v[12], -0.03,  0.0),
        SlopeLookback     = Math.Clamp((int)Math.Round(v[13]), 5, 60),
    };

    public override string ToString() =>
        $"ADX(14,{AdxThreshold:F0}) BB({BbPeriod},{BbWidthMaxPct:F1}%) EMA{EmaPeriod} " +
        $"Step={GridStepAtrMult:F2}A Lvl={GridLevels} TP={TakeProfitAtrMult:F2}A " +
        $"Stop={HardStopAtrMult:F1}A Bail={BailOutAtrMult:F1}A MaxH={MaxHoldCandles}h " +
        $"Rung={RungSellFrac:F2} Reanchor={ReanchorAlpha:F3} Slope({SlopeLookback}b,{SlopeThreshold:P1}) F={Fitness:F4}";
}
