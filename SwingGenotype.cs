namespace TradingGA;

// Swing trading genotype — optimised for daily candles, multi-day holds.
// Strategy: long on RSI-oversold bounces in uptrends; short on overbought-BoS fades.
// Exit: ATR-based hard stop + fixed profit target + trailing stop once activated.
public class SwingGenotype
{
    // ── Regime / entry genes ─────────────────────────────────────────────────
    public int    EmaPeriod           { get; set; }   // 10–100, trend direction EMA
    public int    RsiPeriod           { get; set; }   // 7–21
    public int    AdxPeriod           { get; set; }   // 7–21
    public double AdxThreshold        { get; set; }   // 18–45, trend-strength gate
    public double RsiOversold         { get; set; }   // 28–52, long-entry bounce trigger
    public double RsiOverbought       { get; set; }   // 65–80, short-entry fade trigger
    public double BosThreshold        { get; set; }   // 0.985–0.999, lower-high sensitivity
    public int    BosCandlesWait      { get; set; }   // 1–5, candles after BoS before short entry

    // ── Exit / sizing genes ──────────────────────────────────────────────────
    // All multiples of the 14-period ATR at entry time (absolute price units).
    public double StopLossAtrMult           { get; set; }   // 0.5–4.0
    public double TakeProfitAtrMult         { get; set; }   // 1.5–10.0
    public double TrailingActivationAtrMult { get; set; }   // 1.0–8.0, arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 0.5–5.0, trail distance from peak
    public int    MaxHoldCandles            { get; set; }   // 5–40, force-close timeout

    public double Fitness { get; set; } = double.MinValue;

    public static SwingGenotype Random(System.Random rng, SwingGenotype? seed = null)
    {
        return new()
        {
            EmaPeriod           = seed == null ? rng.Next(10, 101)                     : seed.EmaPeriod,
            RsiPeriod           = seed == null ? rng.Next(7, 22)                       : seed.RsiPeriod,
            AdxPeriod           = seed == null ? rng.Next(7, 22)                       : seed.AdxPeriod,
            AdxThreshold        = seed == null ? 18.0 + rng.NextDouble() * 27.0        : seed.AdxThreshold,
            RsiOversold         = seed == null ? 28.0 + rng.NextDouble() * 24.0        : seed.RsiOversold,
            RsiOverbought       = seed == null ? 65.0 + rng.NextDouble() * 15.0        : seed.RsiOverbought,
            BosThreshold        = seed == null ? 0.985 + rng.NextDouble() * 0.014      : seed.BosThreshold,
            BosCandlesWait      = seed == null ? rng.Next(1, 6)                        : seed.BosCandlesWait,
            StopLossAtrMult           = seed == null ? 0.5 + rng.NextDouble() * 3.5   : seed.StopLossAtrMult,
            TakeProfitAtrMult         = seed == null ? 1.5 + rng.NextDouble() * 8.5   : seed.TakeProfitAtrMult,
            TrailingActivationAtrMult = seed == null ? 1.0 + rng.NextDouble() * 7.0   : seed.TrailingActivationAtrMult,
            TrailingStopAtrMult       = seed == null ? 0.5 + rng.NextDouble() * 4.5   : seed.TrailingStopAtrMult,
            MaxHoldCandles            = seed == null ? rng.Next(5, 41)                 : seed.MaxHoldCandles,
        };
    }

    public static SwingGenotype Crossover(SwingGenotype a, SwingGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            EmaPeriod           = Pick(a.EmaPeriod,           b.EmaPeriod),
            RsiPeriod           = Pick(a.RsiPeriod,           b.RsiPeriod),
            AdxPeriod           = Pick(a.AdxPeriod,           b.AdxPeriod),
            AdxThreshold        = Pick(a.AdxThreshold,        b.AdxThreshold),
            RsiOversold         = Pick(a.RsiOversold,         b.RsiOversold),
            RsiOverbought       = Pick(a.RsiOverbought,       b.RsiOverbought),
            BosThreshold        = Pick(a.BosThreshold,        b.BosThreshold),
            BosCandlesWait      = Pick(a.BosCandlesWait,      b.BosCandlesWait),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
        };
    }

    public SwingGenotype Mutate(System.Random rng, double rate)
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
        return new SwingGenotype
        {
            EmaPeriod           = NudgeInt(EmaPeriod,      10, 100, 10),
            RsiPeriod           = NudgeInt(RsiPeriod,       7,  21),
            AdxPeriod           = NudgeInt(AdxPeriod,       7,  21),
            AdxThreshold        = Nudge(AdxThreshold,      18.0, 45.0, 4.0),
            RsiOversold         = Nudge(RsiOversold,       28.0, 52.0, 4.0),
            RsiOverbought       = Nudge(RsiOverbought,     65.0, 80.0, 3.0),
            BosThreshold        = Nudge(BosThreshold,    0.985, 0.999, 0.003),
            BosCandlesWait      = NudgeInt(BosCandlesWait,  1,   5),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.5,  4.0, 0.5),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         1.5, 10.0, 1.0),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0,  8.0, 1.0),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       0.5,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 5, 40, 5),
        };
    }

    public SwingGenotype ClampToBounds() => new()
    {
        EmaPeriod           = Math.Clamp(EmaPeriod,          10,   100),
        RsiPeriod           = Math.Clamp(RsiPeriod,           7,    21),
        AdxPeriod           = Math.Clamp(AdxPeriod,           7,    21),
        AdxThreshold        = Math.Clamp(AdxThreshold,       18.0, 45.0),
        RsiOversold         = Math.Clamp(RsiOversold,        28.0, 52.0),
        RsiOverbought       = Math.Clamp(RsiOverbought,      65.0, 80.0),
        BosThreshold        = Math.Clamp(BosThreshold,      0.985, 0.999),
        BosCandlesWait      = Math.Clamp(BosCandlesWait,        1,     5),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.5,  4.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         1.5, 10.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0,  8.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       0.5,  5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,              5,    40),
        Fitness = Fitness,
    };

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI({RsiPeriod},OS={RsiOversold:F0},OB={RsiOverbought:F0}) " +
        $"ADX({AdxPeriod},{AdxThreshold:F0}) BoS={BosThreshold:F3} Bwait={BosCandlesWait} " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}d F={Fitness:F4}";
}
