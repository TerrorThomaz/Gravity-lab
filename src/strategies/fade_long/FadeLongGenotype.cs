namespace TradingGA;

// Fade-long genotype — symmetric counterpart to FadeShortGenotype.
//
// Setup (1h): strong downtrend (ADX + EMA) · minimum drop from a recent swing low
//   · RSI bullish divergence: RSI was oversold at the swing low, then recovered
//     by at least RsiDivThreshold pts (sellers losing steam)
// Entry (15m): close above previous 15m candle's high (bullish BoS)
// Exit  (15m): ATR hard stop · MAE ceiling · fixed ATR target · trailing stop · hold timeout
//
// Gene units:
//   LookbackCandles / MaxHoldCandles  in h1 bars
//   ATR multiples use h4 ATR at entry — same scale as FadeShortGenotype.
//   MinDropAtrMult uses h1 ATR — right scale for detecting h1 price structure.
//
// Fixed: RsiPeriod=7, AdxPeriod=7 (consistent with swing).
public class FadeLongGenotype
{
    // ── Regime genes ──────────────────────────────────────────────────────────────
    public int    RegimePeriod { get; set; }   // 100–300  slow EMA — long-term bear regime gate
    public int    EmaPeriod    { get; set; }   // 20–100   medium-term trend EMA
    public double AdxThreshold { get; set; }   // 22–45    downtrend gate

    // ── Entry signal genes ────────────────────────────────────────────────────────
    public int    LookbackCandles { get; set; }   // 12–120  h1 bars to locate swing low
    public double RsiOversold     { get; set; }   // 20–40   RSI ceiling the swing low must break
    public double RsiDivThreshold { get; set; }   // 5–15    RSI must have recovered by this many pts from swing-low RSI
    public double MinDropAtrMult  { get; set; }   // 5–12    min drop (h1 ATR) from swing high to low

    // ── Exit genes ────────────────────────────────────────────────────────────────
    public double StopLossAtrMult           { get; set; }   // 0.3–2.0   ATR buffer below swing low (stop invalidates thesis if broken)
    public double MaeAtrMult                { get; set; }   // 1.5–4.0   max adverse excursion floor = entry − mult×ATR
    public double TakeProfitAtrMult         { get; set; }   // 2.0–15.0  fixed profit target above entry
    public double TrailingActivationAtrMult { get; set; }   // 1.0–4.0   arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from peak
    public int    MaxHoldCandles            { get; set; }   // 24–120    h1 bars before forced exit
    public double PositionSizePct           { get; set; }   // 0.01–0.05 fraction of capital per trade

    // ── Regime-conditional fitness gene ──────────────────────────────────────────
    public int    RegimeSustainedBars  { get; set; }   // 10–100    min consecutive bear-regime h1 bars before a trade counts
    // Protection mode (ProfitLockThreshold, DrawbackTolerance, ProtectedSizeFactor) moved to DynamicGuardGenotype.

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

    public static FadeLongGenotype Random(System.Random rng, FadeLongGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBounds().Mutate(rng, 0.5);

        return new()
        {
            RegimePeriod     = rng.Next(100, 301),
            EmaPeriod        = rng.Next(20, 101),
            AdxThreshold     = 22.0 + rng.NextDouble() * 23.0,
            LookbackCandles  = rng.Next(12, 121),
            RsiOversold      = 20.0 + rng.NextDouble() * 20.0,
            RsiDivThreshold  = 5.0  + rng.NextDouble() * 10.0,
            MinDropAtrMult   = 5.0  + rng.NextDouble() * 7.0,
            StopLossAtrMult           = 0.3 + rng.NextDouble() * 1.7,
            MaeAtrMult                = 1.5 + rng.NextDouble() * 2.5,
            TakeProfitAtrMult         = 2.0 + rng.NextDouble() * 13.0,
            TrailingActivationAtrMult = 1.0 + rng.NextDouble() * 3.0,
            TrailingStopAtrMult       = 1.0 + rng.NextDouble() * 4.0,
            MaxHoldCandles            = rng.Next(24, 121),
            PositionSizePct           = 0.01 + rng.NextDouble() * 0.04,
            RegimeSustainedBars  = rng.Next(10, 101),
        };
    }

    public static FadeLongGenotype Crossover(FadeLongGenotype a, FadeLongGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            RegimePeriod     = Pick(a.RegimePeriod,    b.RegimePeriod),
            EmaPeriod        = Pick(a.EmaPeriod,       b.EmaPeriod),
            AdxThreshold     = Pick(a.AdxThreshold,    b.AdxThreshold),
            LookbackCandles  = Pick(a.LookbackCandles, b.LookbackCandles),
            RsiOversold      = Pick(a.RsiOversold,     b.RsiOversold),
            RsiDivThreshold  = Pick(a.RsiDivThreshold, b.RsiDivThreshold),
            MinDropAtrMult   = Pick(a.MinDropAtrMult,  b.MinDropAtrMult),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            MaeAtrMult                = Pick(a.MaeAtrMult,                b.MaeAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
            PositionSizePct           = Pick(a.PositionSizePct,           b.PositionSizePct),
            RegimeSustainedBars  = Pick(a.RegimeSustainedBars,  b.RegimeSustainedBars),
        };
    }

    public FadeLongGenotype Mutate(System.Random rng, double rate)
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
        return new FadeLongGenotype
        {
            RegimePeriod     = NudgeInt(RegimePeriod, 100, 300, 20),
            EmaPeriod        = NudgeInt(EmaPeriod,       20, 100, 10),
            AdxThreshold     = Nudge(AdxThreshold,       22.0, 45.0, 4.0),
            LookbackCandles  = NudgeInt(LookbackCandles, 12, 120, 8),
            RsiOversold      = Nudge(RsiOversold,        20.0, 40.0, 3.0),
            RsiDivThreshold  = Nudge(RsiDivThreshold,     5.0, 15.0, 2.0),
            MinDropAtrMult   = Nudge(MinDropAtrMult,      5.0, 12.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.3,  2.0, 0.3),
            MaeAtrMult                = Nudge(MaeAtrMult,                1.5,  4.0, 0.4),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0, 15.0, 2.0),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0,  4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 24, 120, 12),
            PositionSizePct           = Nudge(PositionSizePct, 0.01, 0.05, 0.005),
            RegimeSustainedBars  = NudgeInt(RegimeSustainedBars, 10, 100, 10),
        };
    }

    public FadeLongGenotype ClampToBounds() => new()
    {
        RegimePeriod     = Math.Clamp(RegimePeriod, 100, 300),
        EmaPeriod        = Math.Clamp(EmaPeriod,      20,  100),
        AdxThreshold     = Math.Clamp(AdxThreshold,  22.0, 45.0),
        LookbackCandles  = Math.Clamp(LookbackCandles, 12, 120),
        RsiOversold      = Math.Clamp(RsiOversold,   20.0, 40.0),
        RsiDivThreshold  = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinDropAtrMult   = Math.Clamp(MinDropAtrMult,  5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.3,  2.0),
        MaeAtrMult                = Math.Clamp(MaeAtrMult,                1.5,  4.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0, 15.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,             24,  120),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.05),
        RegimeSustainedBars  = Math.Clamp(RegimeSustainedBars,  10,  100),
        Fitness = Fitness,
    };

    // ── Bayesian optimiser interface ──────────────────────────────────────────
    public static readonly double[,] Bounds =
    {
        { 100, 300 }, // RegimePeriod
        {  20, 100 }, // EmaPeriod
        {  22,  45 }, // AdxThreshold
        {  12, 120 }, // LookbackCandles
        {  20,  40 }, // RsiOversold
        { 5.0, 15.0}, // RsiDivThreshold
        { 5.0, 12.0}, // MinDropAtrMult
        { 0.3,  2.0}, // StopLossAtrMult
        { 1.5,  4.0}, // MaeAtrMult
        { 2.0, 15.0}, // TakeProfitAtrMult
        { 1.0,  4.0}, // TrailingActivationAtrMult
        { 1.0,  5.0}, // TrailingStopAtrMult
        {  24, 120 }, // MaxHoldCandles
        {0.01, 0.05}, // PositionSizePct
        {  10, 100 }, // RegimeSustainedBars
    };

    public static readonly string[] ParameterNames =
    [
        "RegimePeriod", "EmaPeriod", "AdxThreshold", "LookbackCandles",
        "RsiOversold", "RsiDivThreshold", "MinDropAtrMult",
        "StopLossAtrMult", "MaeAtrMult", "TakeProfitAtrMult",
        "TrailingActivationAtrMult", "TrailingStopAtrMult",
        "MaxHoldCandles", "PositionSizePct",
        "RegimeSustainedBars",
    ];

    public double[] ToVector() =>
    [
        RegimePeriod, EmaPeriod, AdxThreshold, LookbackCandles,
        RsiOversold, RsiDivThreshold, MinDropAtrMult,
        StopLossAtrMult, MaeAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult,
        MaxHoldCandles, PositionSizePct,
        RegimeSustainedBars,
    ];

    public static FadeLongGenotype FromVector(double[] v) => new()
    {
        RegimePeriod              = Math.Clamp((int)Math.Round(v[0]),  100, 300),
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[1]),   20, 100),
        AdxThreshold              = Math.Clamp(v[2],  22.0, 45.0),
        LookbackCandles           = Math.Clamp((int)Math.Round(v[3]),   12, 120),
        RsiOversold               = Math.Clamp(v[4],  20.0, 40.0),
        RsiDivThreshold           = Math.Clamp(v[5],   5.0, 15.0),
        MinDropAtrMult            = Math.Clamp(v[6],   5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(v[7],   0.3,  2.0),
        MaeAtrMult                = Math.Clamp(v[8],   1.5,  4.0),
        TakeProfitAtrMult         = Math.Clamp(v[9],   2.0, 15.0),
        TrailingActivationAtrMult = Math.Clamp(v[10],  1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(v[11],  1.0,  5.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[12]),  24, 120),
        PositionSizePct           = Math.Clamp(v[13], 0.01, 0.05),
        RegimeSustainedBars       = Math.Clamp((int)Math.Round(v[14]), 10, 100),
    };

    public override string ToString() =>
        $"Regime{RegimePeriod} EMA{EmaPeriod} RSI(7,OS={RsiOversold:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Drop≥{MinDropAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A MAE={MaeAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} " +
        $"RegSust={RegimeSustainedBars} " +
        $"F={Fitness:F4}";
}
