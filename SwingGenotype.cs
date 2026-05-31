namespace TradingGA;

// Swing trading genotype — 4h candles, short-only profit-taking thesis.
//
// Entry logic: strong uptrend + min rally established + RSI divergence (price makes new
// high but RSI makes lower high = buyers losing steam) + volume spike (distribution) +
// close below previous candle's low (structure break confirms reversal).
//
// Exit: ATR hard stop · ATR fixed target · trailing stop once armed · hold timeout.
//
// Range notes vs 5m day-trade strategy:
//   StopLossAtrMult    1.5–6.0   (was 0.5–4.0): 4h ATR≈3%, 0.5A stop was inside normal wick noise
//   TrailingStopAtrMult 1.0–5.0  (was 0.5–5.0): 0.5A trail exits on trivial consolidations
//   MaxHoldCandles     24–120    (was 6–120):    6 bars=24h; min 24 bars=4 days is the swing minimum
//   TakeProfitAtrMult  2.0–20.0  (was 1.5–12.0): GA hit the ceiling; allow wider targets
//   AdxThreshold       22–45     (was 18–45):    ADX<22 is near-noise on 4h; 25+ is a real trend
//   LookbackCandles    12–60     (was 5–40):     5 bars=20h is not a swing high; extend ceiling too
public class SwingGenotype
{
    // ── Regime genes ──────────────────────────────────────────────────────────────
    public int    EmaPeriod     { get; set; }   // 20–100   trend-direction EMA
    public int    RsiPeriod     { get; set; }   // 7–21
    public int    AdxPeriod     { get; set; }   // 7–21
    public double AdxThreshold  { get; set; }   // 22–45    uptrend gate (22 = weakly trending minimum)

    // ── Entry signal genes ────────────────────────────────────────────────────────
    public int    LookbackCandles  { get; set; }   // 12–60   window to locate the swing high (12=2d, 60=10d)
    public double RsiOverbought    { get; set; }   // 60–80   RSI floor the swing high must clear
    public double RsiDivThreshold  { get; set; }   // 2–15    RSI must be this many pts below swing-high RSI
    public double MinRallyAtrMult  { get; set; }   // 2–8     min rally (ATR units) from recent low to high

    // ── Exit genes ────────────────────────────────────────────────────────────────
    public double StopLossAtrMult           { get; set; }   // 1.5–6.0   (4h ATR≈3%; 1.5A≈4.5% clears wick noise)
    public double TakeProfitAtrMult         { get; set; }   // 2.0–20.0  trail handles most exits; TP is upper bound
    public double TrailingActivationAtrMult { get; set; }   // 1.0–8.0   arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from peak (1.0A min to breathe)
    public int    MaxHoldCandles            { get; set; }   // 24–120    4h bars: 24=4d, 42=7d, 120=20d

    public double Fitness { get; set; } = double.MinValue;

    public static SwingGenotype Random(System.Random rng, SwingGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            EmaPeriod      = Seed(rng.Next(20, 101),                      seed?.EmaPeriod    ?? 50),
            RsiPeriod      = Seed(rng.Next(7, 22),                        seed?.RsiPeriod    ?? 14),
            AdxPeriod      = Seed(rng.Next(7, 22),                        seed?.AdxPeriod    ?? 14),
            AdxThreshold   = Seed(22.0 + rng.NextDouble() * 23.0,         seed?.AdxThreshold ?? 27.0),
            LookbackCandles  = Seed(rng.Next(12, 61),                     seed?.LookbackCandles  ?? 20),
            RsiOverbought    = Seed(60.0 + rng.NextDouble() * 20.0,       seed?.RsiOverbought    ?? 68.0),
            RsiDivThreshold  = Seed(2.0  + rng.NextDouble() * 13.0,       seed?.RsiDivThreshold  ?? 5.0),
            MinRallyAtrMult  = Seed(2.0  + rng.NextDouble() * 6.0,        seed?.MinRallyAtrMult  ?? 4.0),
            StopLossAtrMult           = Seed(1.5 + rng.NextDouble() * 4.5,  seed?.StopLossAtrMult           ?? 2.5),
            TakeProfitAtrMult         = Seed(2.0 + rng.NextDouble() * 18.0, seed?.TakeProfitAtrMult         ?? 6.0),
            TrailingActivationAtrMult = Seed(1.0 + rng.NextDouble() * 7.0,  seed?.TrailingActivationAtrMult ?? 3.0),
            TrailingStopAtrMult       = Seed(1.0 + rng.NextDouble() * 4.0,  seed?.TrailingStopAtrMult       ?? 2.0),
            MaxHoldCandles            = Seed(rng.Next(24, 121),             seed?.MaxHoldCandles            ?? 42),
        };
    }

    public static SwingGenotype Crossover(SwingGenotype a, SwingGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            EmaPeriod      = Pick(a.EmaPeriod,     b.EmaPeriod),
            RsiPeriod      = Pick(a.RsiPeriod,     b.RsiPeriod),
            AdxPeriod      = Pick(a.AdxPeriod,     b.AdxPeriod),
            AdxThreshold   = Pick(a.AdxThreshold,  b.AdxThreshold),
            LookbackCandles  = Pick(a.LookbackCandles,  b.LookbackCandles),
            RsiOverbought    = Pick(a.RsiOverbought,    b.RsiOverbought),
            RsiDivThreshold  = Pick(a.RsiDivThreshold,  b.RsiDivThreshold),
            MinRallyAtrMult  = Pick(a.MinRallyAtrMult,  b.MinRallyAtrMult),
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
            EmaPeriod      = NudgeInt(EmaPeriod,      20, 100, 10),
            RsiPeriod      = NudgeInt(RsiPeriod,       7,  21),
            AdxPeriod      = NudgeInt(AdxPeriod,       7,  21),
            AdxThreshold   = Nudge(AdxThreshold,      22.0, 45.0, 4.0),
            LookbackCandles  = NudgeInt(LookbackCandles, 12, 60, 6),
            RsiOverbought    = Nudge(RsiOverbought,   60.0, 80.0, 4.0),
            RsiDivThreshold  = Nudge(RsiDivThreshold,  2.0, 15.0, 2.0),
            MinRallyAtrMult  = Nudge(MinRallyAtrMult,  2.0,  8.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           1.5,  6.0, 0.75),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0, 20.0, 2.0),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0,  8.0, 1.0),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 24, 120, 12),
        };
    }

    public SwingGenotype ClampToBounds() => new()
    {
        EmaPeriod      = Math.Clamp(EmaPeriod,     20,   100),
        RsiPeriod      = Math.Clamp(RsiPeriod,      7,    21),
        AdxPeriod      = Math.Clamp(AdxPeriod,      7,    21),
        AdxThreshold   = Math.Clamp(AdxThreshold,  22.0, 45.0),
        LookbackCandles  = Math.Clamp(LookbackCandles,  12,   60),
        RsiOverbought    = Math.Clamp(RsiOverbought,   60.0, 80.0),
        RsiDivThreshold  = Math.Clamp(RsiDivThreshold,  2.0, 15.0),
        MinRallyAtrMult  = Math.Clamp(MinRallyAtrMult,  2.0,  8.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,            1.5,  6.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,          2.0, 20.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult,  1.0,  8.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,        1.0,  5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,              24,  120),
        Fitness = Fitness,
    };

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI({RsiPeriod},OB={RsiOverbought:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX({AdxPeriod},{AdxThreshold:F0}) Look={LookbackCandles} Rally≥{MinRallyAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars F={Fitness:F4}";
}
