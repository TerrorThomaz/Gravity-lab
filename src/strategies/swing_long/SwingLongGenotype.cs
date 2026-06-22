namespace TradingGA;

// Bidirectional swing long — mirror of FadeShort using RSI bullish divergence + bullish BoS.
// Router-gated on DipLongActive (shared with DipLong — both are bull-regime long strategies).
//
// Entry:  strong bull trend (ADX + EMA) · min decline from recent high
//         · RSI bullish divergence (price lower low but RSI higher = sellers losing steam)
//         · 1h close above prev high (BoS) · 15m close above prev 15m high (precision entry)
// Exit:   swing-low stop · fixed ATR target · trailing stop · max-hold timeout
//
// Fixed: RsiPeriod=7, AdxPeriod=7 (mirrors FadeShort constants).
// ATR exit sizing uses h4 ATR (aggregated from h1) to match multi-day holding timeframe.
public class SwingLongGenotype
{
    public int    EmaPeriod     { get; set; }   // 20–100   trend-direction EMA
    public double AdxThreshold  { get; set; }   // 15–40    trend strength gate

    public int    LookbackCandles   { get; set; }   // 12–120  h1 bars to locate swing low
    public double RsiOversold       { get; set; }   // 20–40   RSI ceiling the swing low must clear
    public double RsiDivThreshold   { get; set; }   // 5–15    RSI must be this many pts above swing-low RSI
    public double MinDeclineAtrMult { get; set; }   // 3–10    min decline (h1 ATR) from recent high to low

    public double StopLossAtrMult           { get; set; }   // 0.3–2.0   ATR buffer below swing low
    public double TakeProfitAtrMult         { get; set; }   // 2.0–10.0  fixed profit target
    public double TrailingActivationAtrMult { get; set; }   // 1.0–4.0   arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from peak
    public int    MaxHoldCandles            { get; set; }   // 24–120    h1 bars before forced exit
    public double PositionSizePct           { get; set; }   // 0.01–0.05 fraction of capital per trade

    public double Fitness { get; set; } = double.MinValue;

    // Bounds for BayesianOptimizer — order matches ToVector/FromVector.
    public static readonly double[,] Bounds =
    {
        {  20, 100  }, // EmaPeriod
        {  15,  40  }, // AdxThreshold
        {  12, 120  }, // LookbackCandles
        {  20,  40  }, // RsiOversold
        { 5.0, 15.0 }, // RsiDivThreshold
        { 3.0, 10.0 }, // MinDeclineAtrMult
        { 0.3,  2.0 }, // StopLossAtrMult
        { 2.0, 10.0 }, // TakeProfitAtrMult
        { 1.0,  4.0 }, // TrailingActivationAtrMult
        { 1.0,  5.0 }, // TrailingStopAtrMult
        {  24, 120  }, // MaxHoldCandles
        {0.01, 0.05 }, // PositionSizePct
    };

    public double[] ToVector() =>
    [
        EmaPeriod, AdxThreshold, LookbackCandles, RsiOversold, RsiDivThreshold,
        MinDeclineAtrMult, StopLossAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult, MaxHoldCandles, PositionSizePct,
    ];

    public static SwingLongGenotype FromVector(double[] v) => new()
    {
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[0]),  20, 100),
        AdxThreshold              = Math.Clamp(v[1],  15.0, 40.0),
        LookbackCandles           = Math.Clamp((int)Math.Round(v[2]),  12, 120),
        RsiOversold               = Math.Clamp(v[3],  20.0, 40.0),
        RsiDivThreshold           = Math.Clamp(v[4],   5.0, 15.0),
        MinDeclineAtrMult         = Math.Clamp(v[5],   3.0, 10.0),
        StopLossAtrMult           = Math.Clamp(v[6],   0.3,  2.0),
        TakeProfitAtrMult         = Math.Clamp(v[7],   2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(v[8],   1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(v[9],   1.0,  5.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[10]), 24, 120),
        PositionSizePct           = Math.Clamp(v[11], 0.01, 0.05),
    };

    public static SwingLongGenotype Random(System.Random rng, SwingLongGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            EmaPeriod                 = Seed(rng.Next(20, 101),                    seed?.EmaPeriod                 ?? 50),
            AdxThreshold              = Seed(15.0 + rng.NextDouble() * 25.0,       seed?.AdxThreshold              ?? 25.0),
            LookbackCandles           = Seed(rng.Next(12, 121),                    seed?.LookbackCandles           ?? 48),
            RsiOversold               = Seed(20.0 + rng.NextDouble() * 20.0,       seed?.RsiOversold               ?? 30.0),
            RsiDivThreshold           = Seed(5.0  + rng.NextDouble() * 10.0,       seed?.RsiDivThreshold           ?? 8.0),
            MinDeclineAtrMult         = Seed(3.0  + rng.NextDouble() * 7.0,        seed?.MinDeclineAtrMult         ?? 5.0),
            StopLossAtrMult           = Seed(0.3  + rng.NextDouble() * 1.7,        seed?.StopLossAtrMult           ?? 0.8),
            TakeProfitAtrMult         = Seed(2.0  + rng.NextDouble() * 8.0,        seed?.TakeProfitAtrMult         ?? 5.0),
            TrailingActivationAtrMult = Seed(1.0  + rng.NextDouble() * 3.0,        seed?.TrailingActivationAtrMult ?? 2.0),
            TrailingStopAtrMult       = Seed(1.0  + rng.NextDouble() * 4.0,        seed?.TrailingStopAtrMult       ?? 2.0),
            MaxHoldCandles            = Seed(rng.Next(24, 121),                    seed?.MaxHoldCandles            ?? 42),
            PositionSizePct           = Seed(0.01 + rng.NextDouble() * 0.04,       seed?.PositionSizePct           ?? 0.03),
        };
    }

    public static SwingLongGenotype Crossover(SwingLongGenotype a, SwingLongGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            EmaPeriod                 = Pick(a.EmaPeriod,                 b.EmaPeriod),
            AdxThreshold              = Pick(a.AdxThreshold,              b.AdxThreshold),
            LookbackCandles           = Pick(a.LookbackCandles,           b.LookbackCandles),
            RsiOversold               = Pick(a.RsiOversold,               b.RsiOversold),
            RsiDivThreshold           = Pick(a.RsiDivThreshold,           b.RsiDivThreshold),
            MinDeclineAtrMult         = Pick(a.MinDeclineAtrMult,         b.MinDeclineAtrMult),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
            PositionSizePct           = Pick(a.PositionSizePct,           b.PositionSizePct),
        };
    }

    public SwingLongGenotype Mutate(System.Random rng, double rate)
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
        return new SwingLongGenotype
        {
            EmaPeriod                 = NudgeInt(EmaPeriod,       20, 100, 10),
            AdxThreshold              = Nudge(AdxThreshold,       15.0, 40.0, 4.0),
            LookbackCandles           = NudgeInt(LookbackCandles, 12, 120, 8),
            RsiOversold               = Nudge(RsiOversold,        20.0, 40.0, 3.0),
            RsiDivThreshold           = Nudge(RsiDivThreshold,    5.0, 15.0, 2.0),
            MinDeclineAtrMult         = Nudge(MinDeclineAtrMult,  3.0, 10.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,    0.3, 2.0, 0.3),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,  2.0, 10.0, 1.5),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0, 4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult, 1.0, 5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles,  24, 120, 12),
            PositionSizePct           = Nudge(PositionSizePct,    0.01, 0.05, 0.005),
        };
    }

    public SwingLongGenotype ClampToBounds() => new()
    {
        EmaPeriod                 = Math.Clamp(EmaPeriod,       20, 100),
        AdxThreshold              = Math.Clamp(AdxThreshold,    15.0, 40.0),
        LookbackCandles           = Math.Clamp(LookbackCandles, 12, 120),
        RsiOversold               = Math.Clamp(RsiOversold,     20.0, 40.0),
        RsiDivThreshold           = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinDeclineAtrMult         = Math.Clamp(MinDeclineAtrMult, 3.0, 10.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,  0.3, 2.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult, 2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0, 4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult, 1.0, 5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,  24, 120),
        PositionSizePct           = Math.Clamp(PositionSizePct, 0.01, 0.05),
        Fitness = Fitness,
    };

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI(7,OS={RsiOversold:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Decline≥{MinDeclineAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} F={Fitness:F4}";
}
