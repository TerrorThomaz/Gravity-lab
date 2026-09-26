namespace TradingGA;

// Grid genotype: long grid in ranging markets (ADX<threshold + BB compression).
// Regime partition: grid ADX < swing ADX (enforced dynamically by adxCeiling).
public class GridGenotype
{
    public double AdxThreshold    { get; set; }  // 8–20   grid fires when ADX(14) < this
    public int    BbPeriod        { get; set; }  // 10–50  BB period for compression
    public double BbWidthMaxPct   { get; set; }  // 0.8–2.5 max BB width %
    public int    EmaPeriod       { get; set; }  // 10–100 range centre
    public double GridStepAtrMult { get; set; }  // 0.3–2.0 level spacing
    public int    GridLevels      { get; set; }  // 1–3    buy levels below EMA
    public double TakeProfitAtrMult { get; set; } // 0.5–3.0 per-level TP
    public double HardStopAtrMult { get; set; }  // 1.5–3.0 hard stop below anchor
    public double BailOutAtrMult  { get; set; }  // 1.0–4.0 close all below lowest fill
    public int    MaxHoldCandles  { get; set; }  // 24–200 h1 bars before force-close
    public double RungSellFrac    { get; set; }  // 0=legacy TP; 0.5–2.0 = multiples of rung spacing
    public double ReanchorAlpha   { get; set; }  // 0=static anchor; >0 tracks live EMA
    public double SlopeThreshold  { get; set; } = -0.005;  // -0.03–0.0 max EMA slope to open (never positive)
    public int    SlopeLookback   { get; set; }  // 5–60   bars for slope measurement

    public double Fitness { get; set; } = double.MinValue;

    // Seeded init: 30% loose mutant, ~45% anchored / ~55% exploration.
    private const double SeedMutantProbability = 0.3;

    // adxCeiling: dynamic upper bound from swing genotype. BB compression is the real partition.
    public static GridGenotype Random(System.Random rng, GridGenotype? seed = null, double adxCeiling = 20.0)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBounds(adxCeiling).Mutate(rng, 0.5, adxCeiling);

        double adxMax = Math.Min(adxCeiling, Hi(0));
        return new()
        {
            AdxThreshold     = Lo(0) + rng.NextDouble() * (adxMax - Lo(0)),
            BbPeriod         = RandInt(rng, 1),
            BbWidthMaxPct    = Rand(rng, 2),
            EmaPeriod        = RandInt(rng, 3),
            GridStepAtrMult  = Rand(rng, 4),
            GridLevels       = RandInt(rng, 5),
            TakeProfitAtrMult= Rand(rng, 6),
            HardStopAtrMult  = Rand(rng, 7),
            BailOutAtrMult   = Rand(rng, 8),
            MaxHoldCandles   = RandInt(rng, 9),
            // The 50% spike at exactly 0 is the "feature off" mode, not a range endpoint — both
            // genes are switches with a continuous tail, so the zero atom is kept deliberately.
            RungSellFrac     = rng.NextDouble() < 0.5 ? 0.0 : Rand(rng, 10),
            ReanchorAlpha    = rng.NextDouble() < 0.5 ? 0.0 : Rand(rng, 11),
            SlopeThreshold   = Rand(rng, 12),
            SlopeLookback    = RandInt(rng, 13),
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
            AdxThreshold     = Nudge(AdxThreshold,      Lo(0), Math.Min(adxCeiling, Hi(0)), 2.0),
            BbPeriod         = NudgeInt(BbPeriod,        (int)Lo(1), (int)Hi(1),  5),
            BbWidthMaxPct    = Nudge(BbWidthMaxPct,      Lo(2), Hi(2), 0.3),
            EmaPeriod        = NudgeInt(EmaPeriod,       (int)Lo(3), (int)Hi(3), 10),
            GridStepAtrMult  = Nudge(GridStepAtrMult,    Lo(4), Hi(4), 0.3),
            GridLevels       = NudgeInt(GridLevels,       (int)Lo(5), (int)Hi(5), 1),
            TakeProfitAtrMult= Nudge(TakeProfitAtrMult,  Lo(6), Hi(6), 0.4),
            HardStopAtrMult  = Nudge(HardStopAtrMult,    Lo(7), Hi(7), 0.4),
            BailOutAtrMult   = Nudge(BailOutAtrMult,     Lo(8), Hi(8), 0.5),
            MaxHoldCandles   = NudgeInt(MaxHoldCandles,   (int)Lo(9), (int)Hi(9), 12),
            RungSellFrac     = Nudge(RungSellFrac,       Lo(10), Hi(10), 0.3),
            ReanchorAlpha    = Nudge(ReanchorAlpha,      Lo(11), Hi(11), 0.04),
            SlopeThreshold   = Nudge(SlopeThreshold,     Lo(12), Hi(12), 0.008),
            SlopeLookback    = NudgeInt(SlopeLookback,   (int)Lo(13), (int)Hi(13), 8),
        };
    }

    // Bounds is the single source of truth — Random, Mutate, ClampToBounds and FromVector all read
    // through these accessors. GenotypeBoundsTests pins that as a property; adding a fifth copy of a
    // gene's range fails it by name.
    private static double Lo(int i) => Bounds[i, 0];
    private static double Hi(int i) => Bounds[i, 1];
    private static double Rand(System.Random rng, int i) => Lo(i) + rng.NextDouble() * (Hi(i) - Lo(i));
    private static int RandInt(System.Random rng, int i) => rng.Next((int)Lo(i), (int)Hi(i) + 1);
    private static double Clamp(double v, int i) => Math.Clamp(v, Lo(i), Hi(i));
    private static int ClampInt(double v, int i) => (int)Math.Clamp(Math.Round(v), Lo(i), Hi(i));

    public GridGenotype ClampToBounds(double adxCeiling = double.MaxValue) => new()
    {
        AdxThreshold     = Math.Clamp(AdxThreshold,     Lo(0), Math.Min(adxCeiling, Hi(0))),
        BbPeriod         = (int)Math.Clamp(BbPeriod,    Lo(1), Hi(1)),
        BbWidthMaxPct    = Math.Clamp(BbWidthMaxPct,    Lo(2), Hi(2)),
        EmaPeriod        = (int)Math.Clamp(EmaPeriod,   Lo(3), Hi(3)),
        GridStepAtrMult  = Math.Clamp(GridStepAtrMult,  Lo(4), Hi(4)),
        GridLevels       = (int)Math.Clamp(GridLevels,  Lo(5), Hi(5)),
        TakeProfitAtrMult= Math.Clamp(TakeProfitAtrMult,Lo(6), Hi(6)),
        HardStopAtrMult  = Math.Clamp(HardStopAtrMult,  Lo(7), Hi(7)),
        BailOutAtrMult   = Math.Clamp(BailOutAtrMult,   Lo(8), Hi(8)),
        MaxHoldCandles   = (int)Math.Clamp(MaxHoldCandles, Lo(9), Hi(9)),
        RungSellFrac     = Math.Clamp(RungSellFrac,     Lo(10), Hi(10)),
        ReanchorAlpha    = Math.Clamp(ReanchorAlpha,    Lo(11), Hi(11)),
        SlopeThreshold   = Math.Clamp(SlopeThreshold,   Lo(12), Hi(12)),
        SlopeLookback    = (int)Math.Clamp(SlopeLookback, Lo(13), Hi(13)),
        Fitness = Fitness,
    };

    public static readonly double[,] Bounds =
    {
        {  8.0, 35.0 }, // AdxThreshold
        // PINNED, not searched. The committed Grid and GridShort genotypes both carried BbPeriod=5
        // and EmaPeriod=5 — the floor of each former range. A 5-bar "range centre" that also
        // re-anchors to the live EMA (ReanchorAlpha was at its own ceiling too) is not a grid around
        // a range; it is a momentum ladder. The strategy's premise had been optimised away, and the
        // fold score had no term that could notice.
        //
        // 20 is the conventional period for both, chosen WITHOUT consulting fitness — so it costs no
        // trials, adds nothing to the deflation burden, and removes two dimensions from a search the
        // MinBTL bound already cannot afford.
        {   20,   20 }, // BbPeriod   (pinned)
        {  0.8,  5.0 }, // BbWidthMaxPct
        {   20,   20 }, // EmaPeriod  (pinned)
        {  0.3,  2.0 }, // GridStepAtrMult
        {    1,    3 }, // GridLevels
        {  0.5,  3.0 }, // TakeProfitAtrMult
        {  1.5,  3.0 }, // HardStopAtrMult
        {  1.0,  4.0 }, // BailOutAtrMult
        {   24,  200 }, // MaxHoldCandles
        {  0.0,  2.0 }, // RungSellFrac   — 0 disables, else multiples of the rung spacing
        {  0.0,  0.2 }, // ReanchorAlpha  — 0 = static anchor (legacy)
        {-0.03,  0.0 }, // SlopeThreshold (never positive — would contradict ranging regime)
        {    5,   60 }, // SlopeLookback
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

    // GridGA hands `Bounds` to the Bayesian optimiser and evaluates each suggestion THROUGH this
    // method, so a tighter clamp here means the TPE surrogate is fitted to mislabelled data: it
    // proposes a point and is told the score of that point's projection. This used to hold a
    // second, narrower copy of three ranges — BbPeriod [10,50], BbWidthMaxPct [0.8,2.5] and
    // EmaPeriod [10,100] against a declared [5,50], [0.8,5.0] and [5,100].
    public static GridGenotype FromVector(double[] v, double adxCeiling = 20.0) => new()
    {
        AdxThreshold      = Math.Clamp(v[0], Lo(0), Math.Min(adxCeiling, Hi(0))),
        BbPeriod          = ClampInt(v[1],   1),
        BbWidthMaxPct     = Clamp(v[2],      2),
        EmaPeriod         = ClampInt(v[3],   3),
        GridStepAtrMult   = Clamp(v[4],      4),
        GridLevels        = ClampInt(v[5],   5),
        TakeProfitAtrMult = Clamp(v[6],      6),
        HardStopAtrMult   = Clamp(v[7],      7),
        BailOutAtrMult    = Clamp(v[8],      8),
        MaxHoldCandles    = ClampInt(v[9],   9),
        RungSellFrac      = Clamp(v[10],    10),
        ReanchorAlpha     = Clamp(v[11],    11),
        SlopeThreshold    = Clamp(v[12],    12),
        SlopeLookback     = ClampInt(v[13], 13),
    };

    public override string ToString() =>
        $"ADX(14,{AdxThreshold:F0}) BB({BbPeriod},{BbWidthMaxPct:F1}%) EMA{EmaPeriod} " +
        $"Step={GridStepAtrMult:F2}A Lvl={GridLevels} TP={TakeProfitAtrMult:F2}A " +
        $"Stop={HardStopAtrMult:F1}A Bail={BailOutAtrMult:F1}A MaxH={MaxHoldCandles}h " +
        $"Rung={RungSellFrac:F2} Reanchor={ReanchorAlpha:F3} Slope({SlopeLookback}b,{SlopeThreshold:P1}) F={Fitness:F4}";
}
