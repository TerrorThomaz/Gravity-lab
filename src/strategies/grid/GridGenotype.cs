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

        double adxMax = Math.Min(adxCeiling, Bounds[0, 1]);
        double adxMin = 8.0;
        return new()
        {
            AdxThreshold     = adxMin + rng.NextDouble() * (adxMax - adxMin),
            BbPeriod         = RandInt(rng, 1),
            BbWidthMaxPct    = Rand(rng, 2),
            EmaPeriod        = RandInt(rng, 3),
            GridStepAtrMult  = Rand(rng, 4),
            GridLevels       = RandInt(rng, 5),
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
            AdxThreshold     = Nudge(AdxThreshold,      8.0, Math.Min(adxCeiling, Bounds[0, 1]), 2.0),
            BbPeriod         = NudgeInt(BbPeriod,        (int)Lo(1), (int)Hi(1),  5),
            BbWidthMaxPct    = Nudge(BbWidthMaxPct,      Lo(2), Hi(2), 0.3),
            EmaPeriod        = NudgeInt(EmaPeriod,       (int)Lo(3), (int)Hi(3), 10),
            GridStepAtrMult  = Nudge(GridStepAtrMult,    Lo(4), Hi(4), 0.3),
            GridLevels       = NudgeInt(GridLevels,        1,    3,   1),
            TakeProfitAtrMult= Nudge(TakeProfitAtrMult,  Lo(6), Hi(6), 0.4),
            HardStopAtrMult  = Nudge(HardStopAtrMult,    Lo(7), Hi(7), 0.4),
            BailOutAtrMult   = Nudge(BailOutAtrMult,     Lo(8), Hi(8), 0.5),
            MaxHoldCandles   = NudgeInt(MaxHoldCandles,  24,  200,  12),
            RungSellFrac     = Nudge(RungSellFrac,       Lo(10), Hi(10), 0.3),
            ReanchorAlpha    = Nudge(ReanchorAlpha,      Lo(11), Hi(11), 0.04),
            SlopeThreshold   = Nudge(SlopeThreshold,     Lo(12), Hi(12), 0.008),
            SlopeLookback    = NudgeInt(SlopeLookback,   (int)Lo(13), (int)Hi(13), 8),
        };
    }

    // Bounds is single source of truth; all operators read through these accessors.
    private static double Lo(int i) => Bounds[i, 0];
    private static double Rand(System.Random rng, int i) => Lo(i) + rng.NextDouble() * (Hi(i) - Lo(i));
    private static int RandInt(System.Random rng, int i) => rng.Next((int)Lo(i), (int)Hi(i) + 1);
    private static double Hi(int i) => Bounds[i, 1];

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
        {    5,   50 }, // BbPeriod
        {  0.8,  5.0 }, // BbWidthMaxPct
        {    5,  100 }, // EmaPeriod
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

    public static GridGenotype FromVector(double[] v, double adxCeiling = 20.0) => new()
    {
        AdxThreshold      = Math.Clamp(v[0],  8.0, Math.Min(adxCeiling, Bounds[0, 1])),
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
