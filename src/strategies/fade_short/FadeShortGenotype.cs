namespace TradingGA;

// FadeShort genotype: fades overbought rallies in uptrends. 1h setup + 15m entry/exit.
// Exit ATR uses h4; MinRally uses h1. Fixed: RsiPeriod=7, AdxPeriod=7.
public class FadeShortGenotype
{
    public int    EmaPeriod     { get; set; }   // 20–100   trend-direction EMA
    public double AdxThreshold  { get; set; }   // 10–45    uptrend gate
    public int    LookbackCandles  { get; set; }   // 12–300  h1 bars to locate swing high
    public double RsiOverbought    { get; set; }   // 65–80   RSI floor for swing high
    public double RsiDivThreshold  { get; set; }   // 5–15    min RSI drop from swing high
    public double MinRallyAtrMult  { get; set; }   // 5–30    min rally size (h1 ATR)
    public double StopLossAtrMult  { get; set; }   // 0.3–2.0  ATR buffer above swing high
    public double MaeAtrMult       { get; set; }   // 1.5–4.0  max adverse excursion ceiling
    public double TakeProfitAtrMult { get; set; }  // 2.0–25.0 fixed target
    public double TrailingActivationAtrMult { get; set; } // 1.0–4.0 arm trail after this profit
    public double TrailingStopAtrMult { get; set; }   // 1.0–5.0  trail distance from peak
    public int    MaxHoldCandles   { get; set; }   // 24–120   h1 bars before forced exit
    public double PositionSizePct  { get; set; }   // 0.01–0.05 capital fraction
    public int    RegimeSustainBars { get; set; }  // 0–120   min uptrend bars before fade eligible (0=disabled)
    public int    RegimeEmaPeriod   { get; set; }  // 100–500 regime-defining EMA (separate from signal EMA)
    public int    RegimeSlopeLookback { get; set; } // 10–60   bars to measure regime slope

    public double Fitness { get; set; } = double.MinValue;

    // Seeded init: 30% loose mutant, ~45% anchored / ~55% exploration.
    private const double SeedMutantProbability = 0.3;

    // ── Payoff-ratio constraint ──────────────────────────────────────────────────────────────
    //
    // The genotype committed in 2026-09 paired StopLossAtrMult = 0.30 (the floor of [0.3, 2.0])
    // with TakeProfitAtrMult = 23.27 against a ceiling of 25: a 77:1 payoff ratio. Such a genotype
    // almost never reaches its target, and when it does the win is enormous — which is exactly what
    // the finalist screen reported as "99% of the edge lives in the top 1% of trades". That is not
    // a subtle statistical finding, it is the arithmetic of an optimiser driven to opposite corners
    // of the risk/reward box.
    //
    // A CONSTRAINT, not a fitness penalty. An inexpressible genotype costs zero trials and cannot be
    // traded off against anything, whereas a penalty can always be outbid by a large enough `gain`.
    public const double MaxPayoffRatio = 6.0;

    // Deterministic projection back into the feasible set. Widening the STOP is tried first: it cuts
    // the ratio without discarding the trade's thesis, whereas cutting the target changes what the
    // strategy is trying to capture. The target only moves when the stop has hit its own ceiling.
    // Feasible for every bounds table where tpLo / stopHi <= MaxPayoffRatio.
    private static (double Stop, double Tp) ConstrainPayoff(double stop, double tp, double[,] b)
    {
        stop = Math.Clamp(stop, b[6, 0], b[6, 1]);
        tp   = Math.Clamp(tp,   b[8, 0], b[8, 1]);
        stop = Math.Clamp(Math.Max(stop, tp / MaxPayoffRatio), b[6, 0], b[6, 1]);
        tp   = Math.Clamp(Math.Min(tp, stop * MaxPayoffRatio), b[8, 0], b[8, 1]);
        return (stop, tp);
    }

    // Applied at the end of every operator, so no path into the population can skip it.
    private FadeShortGenotype WithPayoffConstrained(double[,] b)
    {
        var (stop, tp) = ConstrainPayoff(StopLossAtrMult, TakeProfitAtrMult, b);
        StopLossAtrMult   = stop;
        TakeProfitAtrMult = tp;
        return this;
    }

    public static FadeShortGenotype Random(System.Random rng, FadeShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBounds().Mutate(rng, 0.5);

        return new FadeShortGenotype()
        {
            EmaPeriod        = RandInt(rng, 0),
            AdxThreshold     = Rand(rng, 1),
            LookbackCandles  = RandInt(rng, 2),
            RsiOverbought    = Rand(rng, 3),
            RsiDivThreshold  = Rand(rng, 4),
            MinRallyAtrMult  = Rand(rng, 5),
            StopLossAtrMult           = Rand(rng, 6),
            MaeAtrMult                = Rand(rng, 7),
            TakeProfitAtrMult         = Rand(rng, 8),
            TrailingActivationAtrMult = Rand(rng, 9),
            TrailingStopAtrMult       = Rand(rng, 10),
            MaxHoldCandles            = RandInt(rng, 11),
            PositionSizePct           = Rand(rng, 12),
            RegimeSustainBars         = RandInt(rng, 13),
            RegimeEmaPeriod           = RandInt(rng, 14),
            RegimeSlopeLookback       = RandInt(rng, 15),
        }.WithPayoffConstrained(Bounds);
    }

    public static FadeShortGenotype Crossover(FadeShortGenotype a, FadeShortGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            EmaPeriod        = Pick(a.EmaPeriod,       b.EmaPeriod),
            AdxThreshold     = Pick(a.AdxThreshold,    b.AdxThreshold),
            LookbackCandles  = Pick(a.LookbackCandles, b.LookbackCandles),
            RsiOverbought    = Pick(a.RsiOverbought,   b.RsiOverbought),
            RsiDivThreshold  = Pick(a.RsiDivThreshold, b.RsiDivThreshold),
            MinRallyAtrMult  = Pick(a.MinRallyAtrMult, b.MinRallyAtrMult),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            MaeAtrMult                = Pick(a.MaeAtrMult,                b.MaeAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
            PositionSizePct           = Pick(a.PositionSizePct,           b.PositionSizePct),
            RegimeSustainBars         = Pick(a.RegimeSustainBars,         b.RegimeSustainBars),
            RegimeEmaPeriod           = Pick(a.RegimeEmaPeriod,           b.RegimeEmaPeriod),
            RegimeSlopeLookback       = Pick(a.RegimeSlopeLookback,       b.RegimeSlopeLookback),
        };
    }

    public FadeShortGenotype Mutate(System.Random rng, double rate)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, double min, double max, int step = 3)
        {
            if (rng.NextDouble() > rate) return val;
            return (int)Math.Clamp(val + rng.Next(-step, step + 1), min, max);
        }
        return new FadeShortGenotype
        {
            EmaPeriod        = NudgeInt(EmaPeriod,       Lo(0),  Hi(0),  10),
            AdxThreshold     = Nudge(AdxThreshold,       Lo(1),  Hi(1),  4.0),
            LookbackCandles  = NudgeInt(LookbackCandles, Lo(2),  Hi(2),  8),
            RsiOverbought    = Nudge(RsiOverbought,      Lo(3),  Hi(3),  3.0),
            RsiDivThreshold  = Nudge(RsiDivThreshold,    Lo(4),  Hi(4),  2.0),
            MinRallyAtrMult  = Nudge(MinRallyAtrMult,    Lo(5),  Hi(5),  1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           Lo(6),  Hi(6),  0.3),
            MaeAtrMult                = Nudge(MaeAtrMult,                Lo(7),  Hi(7),  0.4),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         Lo(8),  Hi(8),  1.5),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, Lo(9),  Hi(9),  0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       Lo(10), Hi(10), 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles,         Lo(11), Hi(11), 12),
            PositionSizePct           = Nudge(PositionSizePct,           Lo(12), Hi(12), 0.005),
            RegimeSustainBars         = NudgeInt(RegimeSustainBars,       Lo(13), Hi(13), 8),
            RegimeEmaPeriod           = NudgeInt(RegimeEmaPeriod,         Lo(14), Hi(14), 40),
            RegimeSlopeLookback       = NudgeInt(RegimeSlopeLookback,     Lo(15), Hi(15), 6),
        }.WithPayoffConstrained(Bounds);
    }

    public FadeShortGenotype ClampToBounds() => new FadeShortGenotype()
    {
        EmaPeriod        = ClampInt(EmaPeriod,       0),
        AdxThreshold     = Clamp(AdxThreshold,       1),
        LookbackCandles  = ClampInt(LookbackCandles, 2),
        RsiOverbought    = Clamp(RsiOverbought,      3),
        RsiDivThreshold  = Clamp(RsiDivThreshold,    4),
        MinRallyAtrMult  = Clamp(MinRallyAtrMult,    5),
        StopLossAtrMult           = Clamp(StopLossAtrMult,            6),
        MaeAtrMult                = Clamp(MaeAtrMult,                 7),
        TakeProfitAtrMult         = Clamp(TakeProfitAtrMult,          8),
        TrailingActivationAtrMult = Clamp(TrailingActivationAtrMult,  9),
        TrailingStopAtrMult       = Clamp(TrailingStopAtrMult,       10),
        MaxHoldCandles            = ClampInt(MaxHoldCandles,         11),
        PositionSizePct           = Clamp(PositionSizePct,           12),
        RegimeSustainBars         = ClampInt(RegimeSustainBars,       13),
        RegimeEmaPeriod           = ClampInt(RegimeEmaPeriod,         14),
        RegimeSlopeLookback       = ClampInt(RegimeSlopeLookback,     15),
        Fitness = Fitness,
    }.WithPayoffConstrained(Bounds);

    public static readonly double[,] Bounds =
    {
        {  20, 100  }, // EmaPeriod
        {  10,  45  }, // AdxThreshold
        {  12, 300  }, // LookbackCandles
        {  65,  80  }, // RsiOverbought
        { 5.0, 15.0 }, // RsiDivThreshold
        { 5.0, 30.0 }, // MinRallyAtrMult
        { 0.3,  2.0 }, // StopLossAtrMult
        { 1.5,  4.0 }, // MaeAtrMult
        // 25.0 -> 12.0: with StopLossAtrMult capped at 2.0 and MaxPayoffRatio 6, no target above 12
        // is reachable — the projection folded everything higher back onto 12 anyway. Leaving the
        // ceiling at 25 would spend roughly half the Bayesian optimiser's samples in a region whose
        // every point evaluates as the same genotype.
        { 2.0, 12.0 }, // TakeProfitAtrMult
        { 1.0,  4.0 }, // TrailingActivationAtrMult
        { 1.0,  5.0 }, // TrailingStopAtrMult
        {  24, 120  }, // MaxHoldCandles
        {0.01, 0.05 }, // PositionSizePct
        {   0, 120  }, // RegimeSustainBars (0=disabled)
        { 100, 500  }, // RegimeEmaPeriod
        {  10,  60  }, // RegimeSlopeLookback
    };

    // Bounds is single source of truth; all operators read through these accessors.
    private static double Lo(int i) => Bounds[i, 0];
    private static double Hi(int i) => Bounds[i, 1];
    private static double Rand(System.Random rng, int i) => Lo(i) + rng.NextDouble() * (Hi(i) - Lo(i));
    private static int RandInt(System.Random rng, int i) => rng.Next((int)Lo(i), (int)Hi(i) + 1);
    private static double Clamp(double v, int i) => Math.Clamp(v, Lo(i), Hi(i));
    private static int ClampInt(double v, int i) => (int)Math.Clamp(Math.Round(v), Lo(i), Hi(i));

    public static readonly double[,] BoundsHighVol =
    {
        {  25,  80  }, // EmaPeriod
        {  30,  50  }, // AdxThreshold
        {  36,  96  }, // LookbackCandles
        {  68,  82  }, // RsiOverbought
        { 7.0, 18.0 }, // RsiDivThreshold
        { 6.0, 15.0 }, // MinRallyAtrMult
        { 1.5,  2.5 }, // StopLossAtrMult
        { 2.5,  5.0 }, // MaeAtrMult
        { 5.0, 15.0 }, // TakeProfitAtrMult
        { 2.0,  5.0 }, // TrailingActivationAtrMult
        { 2.5,  6.0 }, // TrailingStopAtrMult
        {  24,  72  }, // MaxHoldCandles
        {0.03, 0.07 }, // PositionSizePct
        {   0, 120  }, // RegimeSustainBars
        { 100, 500  }, // RegimeEmaPeriod
        {  10,  60  }, // RegimeSlopeLookback
    };

    public static readonly string[] ParameterNames =
    [
        "EmaPeriod", "AdxThreshold", "LookbackCandles",
        "RsiOverbought", "RsiDivThreshold", "MinRallyAtrMult",
        "StopLossAtrMult", "MaeAtrMult", "TakeProfitAtrMult",
        "TrailingActivationAtrMult", "TrailingStopAtrMult",
        "MaxHoldCandles", "PositionSizePct",
    ];

    // High-vol variant: seed-mutant uses HighVol clamp+mutate.
    public static FadeShortGenotype RandomHighVol(System.Random rng, FadeShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.MutateHighVol(rng, 0.5);

        return new()
        {
            EmaPeriod        = rng.Next(25, 81),
            AdxThreshold     = 30.0 + rng.NextDouble() * 20.0,
            LookbackCandles  = rng.Next(36, 97),
            RsiOverbought    = 68.0 + rng.NextDouble() * 14.0,
            RsiDivThreshold  = 7.0  + rng.NextDouble() * 11.0,
            MinRallyAtrMult  = 6.0  + rng.NextDouble() * 9.0,
            StopLossAtrMult           = 1.5 + rng.NextDouble() * 1.0,
            MaeAtrMult                = 2.5 + rng.NextDouble() * 2.5,
            TakeProfitAtrMult         = 5.0 + rng.NextDouble() * 10.0,
            TrailingActivationAtrMult = 2.0 + rng.NextDouble() * 3.0,
            TrailingStopAtrMult       = 2.5 + rng.NextDouble() * 3.5,
            MaxHoldCandles            = rng.Next(24, 73),
            PositionSizePct           = 0.03 + rng.NextDouble() * 0.04,
            RegimeSustainBars         = rng.Next(0, 121),
            RegimeEmaPeriod           = rng.Next(100, 501),
            RegimeSlopeLookback       = rng.Next(10, 61),
        };
    }

    // High-vol variant operators — driven off BoundsHighVol.
    public static FadeShortGenotype FromVectorHighVol(double[] v) => new()
    {
        EmaPeriod                 = (int)Math.Clamp(Math.Round(v[0]),  BoundsHighVol[0, 0],  BoundsHighVol[0, 1]),
        AdxThreshold              = Math.Clamp(v[1],  BoundsHighVol[1, 0],  BoundsHighVol[1, 1]),
        LookbackCandles           = (int)Math.Clamp(Math.Round(v[2]),  BoundsHighVol[2, 0],  BoundsHighVol[2, 1]),
        RsiOverbought             = Math.Clamp(v[3],  BoundsHighVol[3, 0],  BoundsHighVol[3, 1]),
        RsiDivThreshold           = Math.Clamp(v[4],  BoundsHighVol[4, 0],  BoundsHighVol[4, 1]),
        MinRallyAtrMult           = Math.Clamp(v[5],  BoundsHighVol[5, 0],  BoundsHighVol[5, 1]),
        StopLossAtrMult           = Math.Clamp(v[6],  BoundsHighVol[6, 0],  BoundsHighVol[6, 1]),
        MaeAtrMult                = Math.Clamp(v[7],  BoundsHighVol[7, 0],  BoundsHighVol[7, 1]),
        TakeProfitAtrMult         = Math.Clamp(v[8],  BoundsHighVol[8, 0],  BoundsHighVol[8, 1]),
        TrailingActivationAtrMult = Math.Clamp(v[9],  BoundsHighVol[9, 0],  BoundsHighVol[9, 1]),
        TrailingStopAtrMult       = Math.Clamp(v[10], BoundsHighVol[10, 0], BoundsHighVol[10, 1]),
        MaxHoldCandles            = (int)Math.Clamp(Math.Round(v[11]), BoundsHighVol[11, 0], BoundsHighVol[11, 1]),
        PositionSizePct           = Math.Clamp(v[12], BoundsHighVol[12, 0], BoundsHighVol[12, 1]),
        RegimeSustainBars         = (int)Math.Clamp(Math.Round(v[13]), BoundsHighVol[13, 0], BoundsHighVol[13, 1]),
        RegimeEmaPeriod           = (int)Math.Clamp(Math.Round(v[14]), BoundsHighVol[14, 0], BoundsHighVol[14, 1]),
        RegimeSlopeLookback       = (int)Math.Clamp(Math.Round(v[15]), BoundsHighVol[15, 0], BoundsHighVol[15, 1]),
    };

    public FadeShortGenotype ClampToBoundsHighVol()
    {
        var g = FromVectorHighVol(ToVector());
        g.Fitness = Fitness;
        return g;
    }

    public FadeShortGenotype MutateHighVol(System.Random rng, double rate)
    {
        double[] v = ClampToBoundsHighVol().ToVector();
        for (int i = 0; i < v.Length; i++)
        {
            if (rng.NextDouble() > rate) continue;
            double lo = BoundsHighVol[i, 0], hi = BoundsHighVol[i, 1];
            v[i] = Math.Clamp(v[i] + (rng.NextDouble() - 0.5) * (hi - lo) * 0.2, lo, hi);
        }
        return FromVectorHighVol(v);
    }

    public static readonly double[,] BoundsLowVol =
    {
        {  20, 100  }, // EmaPeriod
        {  10,  30  }, // AdxThreshold (lower for low-vol)
        {  12, 120  }, // LookbackCandles
        {  65,  80  }, // RsiOverbought
        { 5.0, 15.0 }, // RsiDivThreshold
        { 5.0, 12.0 }, // MinRallyAtrMult
        { 0.3,  1.0 }, // StopLossAtrMult (tighter for low-vol)
        { 1.5,  3.0 }, // MaeAtrMult (tighter for low-vol)
        { 2.0,  5.0 }, // TakeProfitAtrMult (smaller for low-vol)
        { 1.0,  3.0 }, // TrailingActivationAtrMult
        { 1.0,  3.0 }, // TrailingStopAtrMult (tighter for low-vol)
        {  72, 200  }, // MaxHoldCandles (longer for low-vol)
        {0.01, 0.03 }, // PositionSizePct (smaller for low-vol)
        {   0, 120  }, // RegimeSustainBars
        { 100, 500  }, // RegimeEmaPeriod
        {  10,  60  }, // RegimeSlopeLookback
    };

    public double[] ToVector() =>
    [
        EmaPeriod, AdxThreshold, LookbackCandles,
        RsiOverbought, RsiDivThreshold, MinRallyAtrMult,
        StopLossAtrMult, MaeAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult,
        MaxHoldCandles, PositionSizePct, RegimeSustainBars, RegimeEmaPeriod, RegimeSlopeLookback,
    ];

    public static FadeShortGenotype FromVector(double[] v) => new FadeShortGenotype()
    {
        EmaPeriod        = ClampInt(v[0],  0),
        AdxThreshold     = Clamp(v[1],     1),
        LookbackCandles  = ClampInt(v[2],  2),
        RsiOverbought    = Clamp(v[3],     3),
        RsiDivThreshold  = Clamp(v[4],     4),
        MinRallyAtrMult  = Clamp(v[5],     5),
        StopLossAtrMult           = Clamp(v[6],   6),
        MaeAtrMult                = Clamp(v[7],   7),
        TakeProfitAtrMult         = Clamp(v[8],   8),
        TrailingActivationAtrMult = Clamp(v[9],   9),
        TrailingStopAtrMult       = Clamp(v[10], 10),
        MaxHoldCandles            = ClampInt(v[11], 11),
        PositionSizePct           = Clamp(v[12], 12),
        RegimeSustainBars         = ClampInt(v[13], 13),
        RegimeEmaPeriod           = ClampInt(v[14], 14),
        RegimeSlopeLookback       = ClampInt(v[15], 15),
    }.WithPayoffConstrained(Bounds);

    public static FadeShortGenotype FromVectorLowVol(double[] v) => new FadeShortGenotype()
    {
        EmaPeriod        = Math.Clamp((int)Math.Round(v[0]),  20, 100),
        AdxThreshold     = Math.Clamp(v[1],  10.0, 30.0),
        LookbackCandles  = Math.Clamp((int)Math.Round(v[2]),  12, 120),
        RsiOverbought    = Math.Clamp(v[3],  65.0, 80.0),
        RsiDivThreshold  = Math.Clamp(v[4],   5.0, 15.0),
        MinRallyAtrMult  = Math.Clamp(v[5],   5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(v[6],  0.3,  1.0),
        MaeAtrMult                = Math.Clamp(v[7],  1.5,  3.0),
        TakeProfitAtrMult         = Math.Clamp(v[8],  2.0,  5.0),
        TrailingActivationAtrMult = Math.Clamp(v[9],  1.0,  3.0),
        TrailingStopAtrMult       = Math.Clamp(v[10], 1.0,  3.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[11]), 72, 200),
        PositionSizePct           = Math.Clamp(v[12], 0.01, 0.03),
        RegimeSustainBars         = (int)Math.Clamp(Math.Round(v[13]), 0, 120),
        RegimeEmaPeriod           = (int)Math.Clamp(Math.Round(v[14]), 100, 500),
        RegimeSlopeLookback       = (int)Math.Clamp(Math.Round(v[15]), 10, 60),
    }.WithPayoffConstrained(BoundsLowVol);

    // Low-vol variant: seed-mutant uses LowVol clamp+mutate.
    public static FadeShortGenotype RandomLowVol(System.Random rng, FadeShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBoundsLowVol().MutateLowVol(rng, 0.5);

        return new FadeShortGenotype()
        {
            EmaPeriod        = rng.Next(20, 101),
            AdxThreshold     = 10.0 + rng.NextDouble() * 20.0,
            LookbackCandles  = rng.Next(12, 121),
            RsiOverbought    = 65.0 + rng.NextDouble() * 15.0,
            RsiDivThreshold  = 5.0  + rng.NextDouble() * 10.0,
            MinRallyAtrMult  = 5.0  + rng.NextDouble() * 7.0,
            StopLossAtrMult           = 0.3 + rng.NextDouble() * 0.7,
            MaeAtrMult                = 1.5 + rng.NextDouble() * 1.5,
            TakeProfitAtrMult         = 2.0 + rng.NextDouble() * 3.0,
            TrailingActivationAtrMult = 1.0 + rng.NextDouble() * 2.0,
            TrailingStopAtrMult       = 1.0 + rng.NextDouble() * 2.0,
            MaxHoldCandles            = rng.Next(72, 201),
            PositionSizePct           = 0.01 + rng.NextDouble() * 0.02,
            RegimeSustainBars         = rng.Next(0, 121),
            RegimeEmaPeriod           = rng.Next(100, 501),
            RegimeSlopeLookback       = rng.Next(10, 61),
        }.WithPayoffConstrained(BoundsLowVol);
    }

    public FadeShortGenotype ClampToBoundsLowVol() => new FadeShortGenotype()
    {
        EmaPeriod        = Math.Clamp(EmaPeriod,      20,  100),
        AdxThreshold     = Math.Clamp(AdxThreshold,  10.0, 30.0),
        LookbackCandles  = Math.Clamp(LookbackCandles,     12,  120),
        RsiOverbought    = Math.Clamp(RsiOverbought,  65.0, 80.0),
        RsiDivThreshold  = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinRallyAtrMult  = Math.Clamp(MinRallyAtrMult,    5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.3,  1.0),
        MaeAtrMult                = Math.Clamp(MaeAtrMult,                1.5,  3.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0,  5.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0,  3.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  3.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,             72,  200),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.03),
        RegimeSustainBars         = Math.Clamp(RegimeSustainBars,            0,  120),
        RegimeEmaPeriod           = Math.Clamp(RegimeEmaPeriod,            100,  500),
        RegimeSlopeLookback       = Math.Clamp(RegimeSlopeLookback,         10,   60),
        Fitness = Fitness,
    }.WithPayoffConstrained(BoundsLowVol);

    public FadeShortGenotype MutateLowVol(System.Random rng, double rate)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, double min, double max, int step = 3)
        {
            if (rng.NextDouble() > rate) return val;
            return (int)Math.Clamp(val + rng.Next(-step, step + 1), min, max);
        }
        return new FadeShortGenotype
        {
            EmaPeriod        = NudgeInt(EmaPeriod,       20, 100, 10),
            AdxThreshold     = Nudge(AdxThreshold,      10.0, 30.0, 3.0),
            LookbackCandles  = NudgeInt(LookbackCandles, 12, 120, 8),
            RsiOverbought    = Nudge(RsiOverbought,     65.0, 80.0, 3.0),
            RsiDivThreshold  = Nudge(RsiDivThreshold,   5.0, 15.0, 2.0),
            MinRallyAtrMult  = Nudge(MinRallyAtrMult,   5.0, 12.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.3,  1.0, 0.15),
            MaeAtrMult                = Nudge(MaeAtrMult,                1.5,  3.0, 0.3),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0,  5.0, 0.8),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0,  3.0, 0.4),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  3.0, 0.4),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 72, 200, 12),
            PositionSizePct           = Nudge(PositionSizePct, 0.01, 0.03, 0.004),
            RegimeSustainBars         = NudgeInt(RegimeSustainBars, 0, 120, 8),
            RegimeEmaPeriod           = NudgeInt(RegimeEmaPeriod, 100, 500, 40),
            RegimeSlopeLookback       = NudgeInt(RegimeSlopeLookback, 10, 60, 6),
        }.WithPayoffConstrained(BoundsLowVol);
    }

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI(7,OB={RsiOverbought:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Rally≥{MinRallyAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A MAE={MaeAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} RegSust={RegimeSustainBars} Regime(ema{RegimeEmaPeriod},sl{RegimeSlopeLookback}) F={Fitness:F4}";
}
