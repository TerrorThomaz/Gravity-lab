namespace TradingGA;

// Bull long genotype — symmetric counterpart to SwingGenotype.
//
// Setup (1h candles): uptrend (ADX + EMA) · minimum pullback from a recent swing high
//   · RSI bullish recovery: RSI dipped below RsiOversold at a swing low,
//     then recovered by at least RsiDivThreshold pts
// Entry (15m candles): close above previous 15m candle's high (bullish BoS)
// Exit  (15m candles): ATR hard stop · ATR fixed target · trailing stop · hold timeout
//
// Gene units:
//   LookbackCandles / MaxHoldCandles  in h1 bars
//   ATR multiples use the h4 ATR at entry (same scale as SwingGenotype)
//   MinPullbackAtrMult uses h1 ATR — matches swing's MinRallyAtrMult
//
// Fixed: RsiPeriod=7 and AdxPeriod=7 (consistent with swing).
public class BullGenotype
{
    // ── Regime genes ──────────────────────────────────────────────────────────────
    public int    EmaPeriod     { get; set; }   // 20–100   trend-direction EMA
    public double AdxThreshold  { get; set; }   // 15–35    uptrend gate

    // ── Entry signal genes ────────────────────────────────────────────────────────
    public int    LookbackCandles    { get; set; }   // 12–120  h1 bars to locate swing low
    public double RsiOversold        { get; set; }   // 20–45   RSI ceiling the swing low must break
    public double RsiDivThreshold    { get; set; }   // 3–15    RSI must have recovered by this many pts from swing-low RSI
    public double MinPullbackAtrMult { get; set; }   // 2–10    min pullback (h1 ATR) from swing high to swing low

    // ── Exit genes ────────────────────────────────────────────────────────────────
    public double StopLossAtrMult           { get; set; }   // 0.3–2.0   ATR buffer below swing low (stop invalidates bull thesis)
    public double MaeAtrMult                { get; set; }   // 1.5–4.0   max adverse excursion ceiling = entry − mult×ATR
    public double TakeProfitAtrMult         { get; set; }   // 2.0–10.0  fixed profit target above entry
    public double TrailingActivationAtrMult { get; set; }   // 1.0–4.0   arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from peak
    public int    MaxHoldCandles            { get; set; }   // 24–120    h1 bars before forced exit
    public double PositionSizePct           { get; set; }   // 0.01–0.05 fraction of capital per trade in fitness sim

    public double Fitness { get; set; } = double.MinValue;

    public static BullGenotype Random(System.Random rng, BullGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            EmaPeriod             = Seed(rng.Next(20, 101),                     seed?.EmaPeriod             ?? 50),
            AdxThreshold          = Seed(15.0 + rng.NextDouble() * 20.0,        seed?.AdxThreshold          ?? 22.0),
            LookbackCandles       = Seed(rng.Next(12, 121),                     seed?.LookbackCandles       ?? 48),
            RsiOversold           = Seed(20.0 + rng.NextDouble() * 25.0,        seed?.RsiOversold           ?? 35.0),
            RsiDivThreshold       = Seed(3.0  + rng.NextDouble() * 12.0,        seed?.RsiDivThreshold       ?? 8.0),
            MinPullbackAtrMult    = Seed(2.0  + rng.NextDouble() * 8.0,         seed?.MinPullbackAtrMult    ?? 5.0),
            StopLossAtrMult           = Seed(0.3  + rng.NextDouble() * 1.7,     seed?.StopLossAtrMult           ?? 0.8),
            MaeAtrMult                = Seed(1.5  + rng.NextDouble() * 2.5,     seed?.MaeAtrMult                ?? 2.5),
            TakeProfitAtrMult         = Seed(2.0  + rng.NextDouble() * 8.0,     seed?.TakeProfitAtrMult         ?? 5.0),
            TrailingActivationAtrMult = Seed(1.0  + rng.NextDouble() * 3.0,     seed?.TrailingActivationAtrMult ?? 2.0),
            TrailingStopAtrMult       = Seed(1.0  + rng.NextDouble() * 4.0,     seed?.TrailingStopAtrMult       ?? 2.0),
            MaxHoldCandles            = Seed(rng.Next(24, 121),                  seed?.MaxHoldCandles            ?? 42),
            PositionSizePct           = Seed(0.01 + rng.NextDouble() * 0.04,    seed?.PositionSizePct           ?? 0.03),
        };
    }

    public static BullGenotype Crossover(BullGenotype a, BullGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            EmaPeriod             = Pick(a.EmaPeriod,             b.EmaPeriod),
            AdxThreshold          = Pick(a.AdxThreshold,          b.AdxThreshold),
            LookbackCandles       = Pick(a.LookbackCandles,       b.LookbackCandles),
            RsiOversold           = Pick(a.RsiOversold,           b.RsiOversold),
            RsiDivThreshold       = Pick(a.RsiDivThreshold,       b.RsiDivThreshold),
            MinPullbackAtrMult    = Pick(a.MinPullbackAtrMult,    b.MinPullbackAtrMult),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            MaeAtrMult                = Pick(a.MaeAtrMult,                b.MaeAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
            PositionSizePct           = Pick(a.PositionSizePct,           b.PositionSizePct),
        };
    }

    public BullGenotype Mutate(System.Random rng, double rate)
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
        return new BullGenotype
        {
            EmaPeriod             = NudgeInt(EmaPeriod,          20, 100, 10),
            AdxThreshold          = Nudge(AdxThreshold,          15.0, 35.0, 3.0),
            LookbackCandles       = NudgeInt(LookbackCandles,    12, 120, 8),
            RsiOversold           = Nudge(RsiOversold,           20.0, 45.0, 3.0),
            RsiDivThreshold       = Nudge(RsiDivThreshold,        3.0, 15.0, 2.0),
            MinPullbackAtrMult    = Nudge(MinPullbackAtrMult,     2.0, 10.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.3,  2.0, 0.3),
            MaeAtrMult                = Nudge(MaeAtrMult,                1.5,  4.0, 0.4),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0, 10.0, 1.5),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0,  4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles,  24, 120, 12),
            PositionSizePct           = Nudge(PositionSizePct,   0.01, 0.05, 0.005),
        };
    }

    public BullGenotype ClampToBounds() => new()
    {
        EmaPeriod             = Math.Clamp(EmaPeriod,           20, 100),
        AdxThreshold          = Math.Clamp(AdxThreshold,        15.0, 35.0),
        LookbackCandles       = Math.Clamp(LookbackCandles,     12, 120),
        RsiOversold           = Math.Clamp(RsiOversold,         20.0, 45.0),
        RsiDivThreshold       = Math.Clamp(RsiDivThreshold,      3.0, 15.0),
        MinPullbackAtrMult    = Math.Clamp(MinPullbackAtrMult,   2.0, 10.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.3,  2.0),
        MaeAtrMult                = Math.Clamp(MaeAtrMult,                1.5,  4.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,             24,  120),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.05),
        Fitness = Fitness,
    };

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI(7,OS={RsiOversold:F0},rec≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Pull≥{MinPullbackAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A MAE={MaeAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} F={Fitness:F4}";
}
