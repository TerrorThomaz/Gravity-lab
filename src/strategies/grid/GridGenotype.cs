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
    };

    public override string ToString() =>
        $"ADX(14,{AdxThreshold:F0}) BB({BbPeriod},{BbWidthMaxPct:F1}%) EMA{EmaPeriod} " +
        $"Step={GridStepAtrMult:F2}A Lvl={GridLevels} TP={TakeProfitAtrMult:F2}A " +
        $"Stop={HardStopAtrMult:F1}A Bail={BailOutAtrMult:F1}A MaxH={MaxHoldCandles}h F={Fitness:F4}";
}
