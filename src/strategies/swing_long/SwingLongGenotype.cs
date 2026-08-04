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

    // Time-decay stop: after TimeStopBars h1 bars, tolerated loss narrows linearly from
    // TimeStopLossPct down to 0% at MaxHoldCandles. Kills slow-bleeding losing positions.
    public int    TimeStopBars    { get; set; }   // 10–80   h1 bars before decay activates
    public double TimeStopLossPct { get; set; }   // 0.02–0.25  max loss ratio at TimeStopBars

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
        {  10,  80  }, // TimeStopBars
        {0.02, 0.25 }, // TimeStopLossPct
    };

    public static readonly double[,] BoundsLowVol =
    {
        {  20, 100  }, // EmaPeriod
        {  10,  30  }, // AdxThreshold (lower for low-vol)
        {  12, 120  }, // LookbackCandles
        {  20,  40  }, // RsiOversold
        { 5.0, 15.0 }, // RsiDivThreshold
        { 3.0, 10.0 }, // MinDeclineAtrMult
        { 0.3,  1.0 }, // StopLossAtrMult (tighter for low-vol)
        { 2.0,  5.0 }, // TakeProfitAtrMult (smaller for low-vol)
        { 1.0,  3.0 }, // TrailingActivationAtrMult
        { 1.0,  3.0 }, // TrailingStopAtrMult (tighter for low-vol)
        {  72, 200  }, // MaxHoldCandles (longer for low-vol)
        {0.01, 0.03 }, // PositionSizePct (smaller for low-vol)
        {  20,  100 }, // TimeStopBars (longer for low-vol)
        {0.02, 0.20 }, // TimeStopLossPct
    };

    public static readonly double[,] BoundsHighVol =
    {
        {  30,  80  }, // EmaPeriod
        {  30,  50  }, // AdxThreshold
        {  48,  96  }, // LookbackCandles
        {  22,  38  }, // RsiOversold
        { 6.0, 16.0 }, // RsiDivThreshold
        { 5.0, 12.0 }, // MinDeclineAtrMult
        { 1.5,  2.5 }, // StopLossAtrMult
        { 5.0, 15.0 }, // TakeProfitAtrMult
        { 2.0,  5.0 }, // TrailingActivationAtrMult
        { 2.5,  5.5 }, // TrailingStopAtrMult
        {  24,  72  }, // MaxHoldCandles
        {0.03, 0.07 }, // PositionSizePct
        {  20,  60  }, // TimeStopBars
        {0.08, 0.20 }, // TimeStopLossPct
    };

    public static readonly string[] ParameterNames =
    [
        "EmaPeriod", "AdxThreshold", "LookbackCandles", "RsiOversold", "RsiDivThreshold",
        "MinDeclineAtrMult", "StopLossAtrMult", "TakeProfitAtrMult",
        "TrailingActivationAtrMult", "TrailingStopAtrMult", "MaxHoldCandles", "PositionSizePct",
        "TimeStopBars", "TimeStopLossPct",
    ];

    public static SwingLongGenotype RandomHighVol(System.Random rng, SwingLongGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            EmaPeriod                 = Seed(rng.Next(30, 81),                     seed?.EmaPeriod                 ?? 50),
            AdxThreshold              = Seed(30.0 + rng.NextDouble() * 20.0,       seed?.AdxThreshold              ?? 38.0),
            LookbackCandles           = Seed(rng.Next(48, 97),                     seed?.LookbackCandles           ?? 70),
            RsiOversold               = Seed(22.0 + rng.NextDouble() * 16.0,       seed?.RsiOversold               ?? 30.0),
            RsiDivThreshold           = Seed(6.0  + rng.NextDouble() * 10.0,       seed?.RsiDivThreshold           ?? 10.0),
            MinDeclineAtrMult         = Seed(5.0  + rng.NextDouble() * 7.0,        seed?.MinDeclineAtrMult         ?? 8.0),
            StopLossAtrMult           = Seed(1.5  + rng.NextDouble() * 1.0,        seed?.StopLossAtrMult           ?? 2.0),
            TakeProfitAtrMult         = Seed(5.0  + rng.NextDouble() * 10.0,       seed?.TakeProfitAtrMult         ?? 10.0),
            TrailingActivationAtrMult = Seed(2.0  + rng.NextDouble() * 3.0,        seed?.TrailingActivationAtrMult ?? 3.5),
            TrailingStopAtrMult       = Seed(2.5  + rng.NextDouble() * 3.0,        seed?.TrailingStopAtrMult       ?? 3.5),
            MaxHoldCandles            = Seed(rng.Next(24, 73),                     seed?.MaxHoldCandles            ?? 48),
            PositionSizePct           = Seed(0.03 + rng.NextDouble() * 0.04,       seed?.PositionSizePct           ?? 0.05),
            TimeStopBars              = Seed(rng.Next(20, 61),                     seed?.TimeStopBars              ?? 35),
            TimeStopLossPct           = Seed(0.08 + rng.NextDouble() * 0.12,       seed?.TimeStopLossPct           ?? 0.15),
        };
    }

    public double[] ToVector() =>
    [
        EmaPeriod, AdxThreshold, LookbackCandles, RsiOversold, RsiDivThreshold,
        MinDeclineAtrMult, StopLossAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult, MaxHoldCandles, PositionSizePct,
        TimeStopBars, TimeStopLossPct,
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
        TimeStopBars              = Math.Clamp((int)Math.Round(v[12]), 10,  80),
        TimeStopLossPct           = Math.Clamp(v[13], 0.02, 0.25),
    };

    public static SwingLongGenotype FromVectorLowVol(double[] v) => new()
    {
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[0]),  20, 100),
        AdxThreshold              = Math.Clamp(v[1],  10.0, 30.0),
        LookbackCandles           = Math.Clamp((int)Math.Round(v[2]),  12, 120),
        RsiOversold               = Math.Clamp(v[3],  20.0, 40.0),
        RsiDivThreshold           = Math.Clamp(v[4],   5.0, 15.0),
        MinDeclineAtrMult         = Math.Clamp(v[5],   3.0, 10.0),
        StopLossAtrMult           = Math.Clamp(v[6],   0.3,  1.0),
        TakeProfitAtrMult         = Math.Clamp(v[7],   2.0,  5.0),
        TrailingActivationAtrMult = Math.Clamp(v[8],   1.0,  3.0),
        TrailingStopAtrMult       = Math.Clamp(v[9],   1.0,  3.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[10]), 72, 200),
        PositionSizePct           = Math.Clamp(v[11], 0.01, 0.03),
        TimeStopBars              = Math.Clamp((int)Math.Round(v[12]), 20, 100),
        TimeStopLossPct           = Math.Clamp(v[13], 0.02, 0.20),
    };

    public static SwingLongGenotype RandomLowVol(System.Random rng, SwingLongGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            EmaPeriod                 = Seed(rng.Next(20, 101),                    seed?.EmaPeriod                 ?? 50),
            AdxThreshold              = Seed(10.0 + rng.NextDouble() * 20.0,       seed?.AdxThreshold              ?? 20.0),
            LookbackCandles           = Seed(rng.Next(12, 121),                    seed?.LookbackCandles           ?? 72),
            RsiOversold               = Seed(20.0 + rng.NextDouble() * 20.0,       seed?.RsiOversold               ?? 35.0),
            RsiDivThreshold           = Seed(5.0  + rng.NextDouble() * 10.0,       seed?.RsiDivThreshold           ?? 8.0),
            MinDeclineAtrMult         = Seed(3.0  + rng.NextDouble() * 7.0,        seed?.MinDeclineAtrMult         ?? 5.0),
            StopLossAtrMult           = Seed(0.3  + rng.NextDouble() * 0.7,        seed?.StopLossAtrMult           ?? 0.8),
            TakeProfitAtrMult         = Seed(2.0  + rng.NextDouble() * 3.0,        seed?.TakeProfitAtrMult         ?? 4.0),
            TrailingActivationAtrMult = Seed(1.0  + rng.NextDouble() * 2.0,        seed?.TrailingActivationAtrMult ?? 2.5),
            TrailingStopAtrMult       = Seed(1.0  + rng.NextDouble() * 2.0,        seed?.TrailingStopAtrMult       ?? 1.8),
            MaxHoldCandles            = Seed(rng.Next(72, 201),                    seed?.MaxHoldCandles            ?? 120),
            PositionSizePct           = Seed(0.01 + rng.NextDouble() * 0.02,       seed?.PositionSizePct           ?? 0.02),
            TimeStopBars              = Seed(rng.Next(20, 101),                     seed?.TimeStopBars              ?? 40),
            TimeStopLossPct           = Seed(0.02 + rng.NextDouble() * 0.18,       seed?.TimeStopLossPct           ?? 0.12),
        };
    }

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
            TimeStopBars              = Seed(rng.Next(10, 81),                      seed?.TimeStopBars              ?? 999),
            TimeStopLossPct           = Seed(0.02 + rng.NextDouble() * 0.23,       seed?.TimeStopLossPct           ?? 0.99),
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
            TimeStopBars              = Pick(a.TimeStopBars,              b.TimeStopBars),
            TimeStopLossPct           = Pick(a.TimeStopLossPct,           b.TimeStopLossPct),
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
            TimeStopBars              = NudgeInt(TimeStopBars,    10, 80, 8),
            TimeStopLossPct           = Nudge(TimeStopLossPct,   0.02, 0.25, 0.03),
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
        TimeStopBars              = Math.Clamp(TimeStopBars,    10,  80),
        TimeStopLossPct           = Math.Clamp(TimeStopLossPct, 0.02, 0.25),
        Fitness = Fitness,
    };

    public SwingLongGenotype ClampToBoundsLowVol() => new()
    {
        EmaPeriod                 = Math.Clamp(EmaPeriod,       20, 100),
        AdxThreshold              = Math.Clamp(AdxThreshold,    10.0, 30.0),
        LookbackCandles           = Math.Clamp(LookbackCandles, 12, 120),
        RsiOversold               = Math.Clamp(RsiOversold,     20.0, 40.0),
        RsiDivThreshold           = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinDeclineAtrMult         = Math.Clamp(MinDeclineAtrMult, 3.0, 10.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,  0.3, 1.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult, 2.0, 5.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0, 3.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult, 1.0, 3.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,  72, 200),
        PositionSizePct           = Math.Clamp(PositionSizePct, 0.01, 0.03),
        TimeStopBars              = Math.Clamp(TimeStopBars,    20,  100),
        TimeStopLossPct           = Math.Clamp(TimeStopLossPct, 0.02, 0.20),
        Fitness = Fitness,
    };

    public SwingLongGenotype MutateLowVol(System.Random rng, double rate)
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
            AdxThreshold              = Nudge(AdxThreshold,       10.0, 30.0, 3.0),
            LookbackCandles           = NudgeInt(LookbackCandles, 12, 120, 8),
            RsiOversold               = Nudge(RsiOversold,        20.0, 40.0, 3.0),
            RsiDivThreshold           = Nudge(RsiDivThreshold,    5.0, 15.0, 2.0),
            MinDeclineAtrMult         = Nudge(MinDeclineAtrMult,  3.0, 10.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,    0.3, 1.0, 0.15),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,  2.0, 5.0, 0.8),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0, 3.0, 0.4),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult, 1.0, 3.0, 0.4),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles,  72, 200, 12),
            PositionSizePct           = Nudge(PositionSizePct,    0.01, 0.03, 0.004),
            TimeStopBars              = NudgeInt(TimeStopBars,    20, 100, 8),
            TimeStopLossPct           = Nudge(TimeStopLossPct,   0.02, 0.20, 0.03),
        };
    }

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI(7,OS={RsiOversold:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Decline≥{MinDeclineAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} " +
        $"TStop({TimeStopBars}bars/{TimeStopLossPct:P0}) F={Fitness:F4}";
}
