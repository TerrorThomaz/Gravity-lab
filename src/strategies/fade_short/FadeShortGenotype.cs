namespace TradingGA;

// Swing trading genotype — 1h setup + 15m entry/exit.
//
// Setup (1h candles): strong uptrend (ADX + EMA) · min rally from recent low
//   · RSI bearish divergence (new price high but lower RSI = buyers losing steam)
// Entry (15m candles): close below previous 15m candle's low (BoS on 15m precision)
// Exit  (15m candles): ATR hard stop · ATR fixed target · trailing stop · hold timeout
//
// Gene units:
//   LookbackCandles / MaxHoldCandles  in h1 bars (24 = 1d, 120 = 5d)
//   ATR multiples for exits (SL, TP, trail) use the h4 ATR at entry — matches
//     the multi-day holding timeframe and restores the original 4h-strategy scale.
//   MinRallyAtrMult uses h1 ATR — right scale for detecting h1 price structure.
//
// Fixed (not genes): RsiPeriod=7 and AdxPeriod=7 — GA always converges to these.
//   ADX(7) is faster than ADX(14) and better at catching trend onset. Grid uses
//   ADX(14) for regime but also requires BB compression, making practical overlap
//   with swing nearly impossible despite different ADX periods.
public class FadeShortGenotype
{
    // ── Regime genes ──────────────────────────────────────────────────────────────
    public int    EmaPeriod     { get; set; }   // 20–100   trend-direction EMA
    public double AdxThreshold  { get; set; }   // 22–45    uptrend gate (22 = weakly trending minimum)

    // ── Entry signal genes ────────────────────────────────────────────────────────
    public int    LookbackCandles  { get; set; }   // 12–120  h1 bars to locate swing high (12=0.5d, 120=5d)
    public double RsiOverbought    { get; set; }   // 65–80   RSI floor the swing high must clear (65 = genuinely elevated, not just mid-range)
    public double RsiDivThreshold  { get; set; }   // 5–15    RSI must be this many pts below swing-high RSI (5 = real divergence, not noise)
    public double MinRallyAtrMult  { get; set; }   // 5–12    min rally (h1 ATR units) from recent low to high (~3.75–9% at typical h1 ATR)

    // ── Exit genes ────────────────────────────────────────────────────────────────
    public double StopLossAtrMult           { get; set; }   // 0.3–2.0   ATR buffer above the swing high (stop = swingHigh + mult×ATR; invalidates thesis if exceeded)
    public double MaeAtrMult                { get; set; }   // 1.5–4.0   max adverse excursion ceiling = entry + mult×ATR; caps slow-grind rally losses
    public double TakeProfitAtrMult         { get; set; }   // 2.0–10.0  realistic target in an 8-day hold (~10–20% move)
    public double TrailingActivationAtrMult { get; set; }   // 1.0–4.0   arm trail after this profit (was 1–8; 8A=15% almost never fired)
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from peak (1.0A min to breathe)
    public int    MaxHoldCandles            { get; set; }   // 24–120    h1 bars: 24=1d, 48=2d, 120=5d
    public double PositionSizePct           { get; set; }   // 0.01–0.05 fraction of capital per trade in fitness sim

    public double Fitness { get; set; } = double.MinValue;

    // ── Seeded initialisation ────────────────────────────────────────────────────
    // Probability that a seeded Random* draw returns a LOOSE mutant of the seed
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

    public static FadeShortGenotype Random(System.Random rng, FadeShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBounds().Mutate(rng, 0.5);

        return new()
        {
            EmaPeriod        = rng.Next(20, 101),
            AdxThreshold     = 22.0 + rng.NextDouble() * 23.0,
            LookbackCandles  = rng.Next(12, 121),
            RsiOverbought    = 65.0 + rng.NextDouble() * 15.0,
            RsiDivThreshold  = 5.0  + rng.NextDouble() * 10.0,
            MinRallyAtrMult  = 5.0  + rng.NextDouble() * 7.0,
            StopLossAtrMult           = 0.3 + rng.NextDouble() * 1.7,
            MaeAtrMult                = 1.5 + rng.NextDouble() * 2.5,
            TakeProfitAtrMult         = 2.0 + rng.NextDouble() * 8.0,
            TrailingActivationAtrMult = 1.0 + rng.NextDouble() * 3.0,
            TrailingStopAtrMult       = 1.0 + rng.NextDouble() * 4.0,
            MaxHoldCandles            = rng.Next(24, 121),
            PositionSizePct           = 0.01 + rng.NextDouble() * 0.04,
        };
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
        };
    }

    public FadeShortGenotype Mutate(System.Random rng, double rate)
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
        return new FadeShortGenotype
        {
            EmaPeriod        = NudgeInt(EmaPeriod,       20, 100, 10),
            AdxThreshold     = Nudge(AdxThreshold,      22.0, 45.0, 4.0),
            LookbackCandles  = NudgeInt(LookbackCandles, 12, 120, 8),
            RsiOverbought    = Nudge(RsiOverbought,     65.0, 80.0, 3.0),
            RsiDivThreshold  = Nudge(RsiDivThreshold,   5.0, 15.0, 2.0),
            MinRallyAtrMult  = Nudge(MinRallyAtrMult,   5.0, 12.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.3,  2.0, 0.3),
            MaeAtrMult                = Nudge(MaeAtrMult,                1.5,  4.0, 0.4),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0, 10.0, 1.5),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0,  4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 24, 120, 12),
            PositionSizePct           = Nudge(PositionSizePct, 0.01, 0.05, 0.005),
        };
    }

    public FadeShortGenotype ClampToBounds() => new()
    {
        EmaPeriod        = Math.Clamp(EmaPeriod,      20,  100),
        AdxThreshold     = Math.Clamp(AdxThreshold,  22.0, 45.0),
        LookbackCandles  = Math.Clamp(LookbackCandles, 12, 120),
        RsiOverbought    = Math.Clamp(RsiOverbought,  65.0, 80.0),
        RsiDivThreshold  = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinRallyAtrMult  = Math.Clamp(MinRallyAtrMult,  5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.3,  2.0),
        MaeAtrMult                = Math.Clamp(MaeAtrMult,                1.5,  4.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,             24,  120),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.05),
        Fitness = Fitness,
    };

    // ── Bayesian optimiser interface ──────────────────────────────────────────
    // Order matches ToVector / FromVector.
    public static readonly double[,] Bounds =
    {
        {  20, 100  }, // EmaPeriod
        {  22,  45  }, // AdxThreshold
        {  12, 120  }, // LookbackCandles
        {  65,  80  }, // RsiOverbought
        { 5.0, 15.0 }, // RsiDivThreshold
        { 5.0, 12.0 }, // MinRallyAtrMult
        { 0.3,  2.0 }, // StopLossAtrMult
        { 1.5,  4.0 }, // MaeAtrMult
        { 2.0, 10.0 }, // TakeProfitAtrMult
        { 1.0,  4.0 }, // TrailingActivationAtrMult
        { 1.0,  5.0 }, // TrailingStopAtrMult
        {  24, 120  }, // MaxHoldCandles
        {0.01, 0.05 }, // PositionSizePct
    };

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
    };

    public static readonly string[] ParameterNames =
    [
        "EmaPeriod", "AdxThreshold", "LookbackCandles",
        "RsiOverbought", "RsiDivThreshold", "MinRallyAtrMult",
        "StopLossAtrMult", "MaeAtrMult", "TakeProfitAtrMult",
        "TrailingActivationAtrMult", "TrailingStopAtrMult",
        "MaxHoldCandles", "PositionSizePct",
    ];

    // Same seeded-init contract as Random (see SeedMutantProbability), but the
    // seed-mutant branch is confined to BoundsHighVol via ClampToBoundsHighVol +
    // MutateHighVol — never the normal-regime Mutate, which would propose
    // genotypes outside the region the high-vol variant is defined on.
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
        };
    }

    // ── High-vol variant operators ───────────────────────────────────────────────
    // The low-vol variant already had ClampToBoundsLowVol / MutateLowVol; the
    // high-vol variant had only BoundsHighVol, so seeded RandomHighVol had no
    // in-region mutation operator to call. Both are driven off the BoundsHighVol
    // table so they can never drift out of the high-vol region.
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
    };

    public double[] ToVector() =>
    [
        EmaPeriod, AdxThreshold, LookbackCandles,
        RsiOverbought, RsiDivThreshold, MinRallyAtrMult,
        StopLossAtrMult, MaeAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult,
        MaxHoldCandles, PositionSizePct,
    ];

    public static FadeShortGenotype FromVector(double[] v) => new()
    {
        EmaPeriod        = Math.Clamp((int)Math.Round(v[0]),  20, 100),
        AdxThreshold     = Math.Clamp(v[1],  22.0, 45.0),
        LookbackCandles  = Math.Clamp((int)Math.Round(v[2]),  12, 120),
        RsiOverbought    = Math.Clamp(v[3],  65.0, 80.0),
        RsiDivThreshold  = Math.Clamp(v[4],   5.0, 15.0),
        MinRallyAtrMult  = Math.Clamp(v[5],   5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(v[6],  0.3,  2.0),
        MaeAtrMult                = Math.Clamp(v[7],  1.5,  4.0),
        TakeProfitAtrMult         = Math.Clamp(v[8],  2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(v[9],  1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(v[10], 1.0,  5.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[11]), 24, 120),
        PositionSizePct           = Math.Clamp(v[12], 0.01, 0.05),
    };

    public static FadeShortGenotype FromVectorLowVol(double[] v) => new()
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
    };

    // Same seeded-init contract as Random (see SeedMutantProbability), but the
    // seed-mutant branch uses the low-vol clamp + mutate pair so it stays inside
    // BoundsLowVol.
    public static FadeShortGenotype RandomLowVol(System.Random rng, FadeShortGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBoundsLowVol().MutateLowVol(rng, 0.5);

        return new()
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
        };
    }

    public FadeShortGenotype ClampToBoundsLowVol() => new()
    {
        EmaPeriod        = Math.Clamp(EmaPeriod,      20,  100),
        AdxThreshold     = Math.Clamp(AdxThreshold,  10.0, 30.0),
        LookbackCandles  = Math.Clamp(LookbackCandles, 12, 120),
        RsiOverbought    = Math.Clamp(RsiOverbought,  65.0, 80.0),
        RsiDivThreshold  = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinRallyAtrMult  = Math.Clamp(MinRallyAtrMult,  5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.3,  1.0),
        MaeAtrMult                = Math.Clamp(MaeAtrMult,                1.5,  3.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0,  5.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0,  3.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  3.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,             72,  200),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.03),
        Fitness = Fitness,
    };

    public FadeShortGenotype MutateLowVol(System.Random rng, double rate)
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
        };
    }

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI(7,OB={RsiOverbought:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Rally≥{MinRallyAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A MAE={MaeAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} F={Fitness:F4}";
}
