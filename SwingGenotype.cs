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
public class SwingGenotype
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
    public double TakeProfitAtrMult         { get; set; }   // 2.0–10.0  realistic target in an 8-day hold (~10–20% move)
    public double TrailingActivationAtrMult { get; set; }   // 1.0–4.0   arm trail after this profit (was 1–8; 8A=15% almost never fired)
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from peak (1.0A min to breathe)
    public int    MaxHoldCandles            { get; set; }   // 24–120    h1 bars: 24=1d, 48=2d, 120=5d

    public double Fitness { get; set; } = double.MinValue;

    public static SwingGenotype Random(System.Random rng, SwingGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            EmaPeriod        = Seed(rng.Next(20, 101),                    seed?.EmaPeriod        ?? 50),
            AdxThreshold     = Seed(22.0 + rng.NextDouble() * 23.0,       seed?.AdxThreshold     ?? 27.0),
            LookbackCandles  = Seed(rng.Next(12, 121),                    seed?.LookbackCandles  ?? 48),
            RsiOverbought    = Seed(65.0 + rng.NextDouble() * 15.0,       seed?.RsiOverbought    ?? 70.0),
            RsiDivThreshold  = Seed(5.0  + rng.NextDouble() * 10.0,       seed?.RsiDivThreshold  ?? 8.0),
            MinRallyAtrMult  = Seed(5.0  + rng.NextDouble() * 7.0,        seed?.MinRallyAtrMult  ?? 7.0),
            StopLossAtrMult           = Seed(0.3 + rng.NextDouble() * 1.7,  seed?.StopLossAtrMult           ?? 0.8),
            TakeProfitAtrMult         = Seed(2.0 + rng.NextDouble() * 8.0,  seed?.TakeProfitAtrMult         ?? 5.0),
            TrailingActivationAtrMult = Seed(1.0 + rng.NextDouble() * 3.0,  seed?.TrailingActivationAtrMult ?? 2.0),
            TrailingStopAtrMult       = Seed(1.0 + rng.NextDouble() * 4.0,  seed?.TrailingStopAtrMult       ?? 2.0),
            MaxHoldCandles            = Seed(rng.Next(24, 121),             seed?.MaxHoldCandles            ?? 42),
        };
    }

    public static SwingGenotype Crossover(SwingGenotype a, SwingGenotype b, System.Random rng)
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
            EmaPeriod        = NudgeInt(EmaPeriod,       20, 100, 10),
            AdxThreshold     = Nudge(AdxThreshold,      22.0, 45.0, 4.0),
            LookbackCandles  = NudgeInt(LookbackCandles, 12, 120, 8),
            RsiOverbought    = Nudge(RsiOverbought,     65.0, 80.0, 3.0),
            RsiDivThreshold  = Nudge(RsiDivThreshold,   5.0, 15.0, 2.0),
            MinRallyAtrMult  = Nudge(MinRallyAtrMult,   5.0, 12.0, 1.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.3,  2.0, 0.3),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0, 10.0, 1.5),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.0,  4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 24, 120, 12),
        };
    }

    public SwingGenotype ClampToBounds() => new()
    {
        EmaPeriod        = Math.Clamp(EmaPeriod,      20,  100),
        AdxThreshold     = Math.Clamp(AdxThreshold,  22.0, 45.0),
        LookbackCandles  = Math.Clamp(LookbackCandles, 12, 120),
        RsiOverbought    = Math.Clamp(RsiOverbought,  65.0, 80.0),
        RsiDivThreshold  = Math.Clamp(RsiDivThreshold, 5.0, 15.0),
        MinRallyAtrMult  = Math.Clamp(MinRallyAtrMult,  5.0, 12.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.3,  2.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0, 10.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.0,  4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,             24,  120),
        Fitness = Fitness,
    };

    public override string ToString() =>
        $"EMA{EmaPeriod} RSI(7,OB={RsiOverbought:F0},div≥{RsiDivThreshold:F0}pts) " +
        $"ADX(7,{AdxThreshold:F0}) Look={LookbackCandles} Rally≥{MinRallyAtrMult:F1}A " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars F={Fitness:F4}";
}
