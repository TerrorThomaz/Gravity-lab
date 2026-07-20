namespace TradingGA;

// Rip-short genotype — regime-gated bear trend-continuation strategy.
//
// Direction-mirror of DipLong. DipLong buys RSI dips in established uptrends;
// RipShort shorts relief rallies ("rips") in established downtrends. Both are
// trend-continuation plays, opposite in direction and regime.
//
// Entry (1h setup + 15m trigger):
//   1. Bear regime  — close < RegimeLongEma AND RegimeLongEma is falling
//   2. Trend gate   — close < EmaPeriod EMA AND ADX ≥ threshold
//   3. Rally setup  — RSI(7) ≥ RsiRallyThreshold (relief rally within downtrend)
//   4. Bearish BoS  — 15m close < previous 15m low (committed sellers)
//
// Stop: recentSwingHigh + StopLossAtrMult × h4ATR (highest high in last 20 h1 bars)
//
// Fixed: RsiPeriod=7, AdxPeriod=7, SwingHighLookback=20 (consistent with siblings).
public class RipShortGenotype
{
    // ── Regime genes ──────────────────────────────────────────────────────────────
    public int    RegimeLongEmaPeriod { get; set; }   // 100–500  bear market gate EMA
    public int    RegimeSlopeLookback { get; set; }   // 10–60    bars to measure EMA slope direction
    public int    EmaPeriod           { get; set; }   // 20–100   short-term trend EMA
    public double AdxThreshold        { get; set; }   // 15–35    trend strength gate

    // ── Entry signal genes ────────────────────────────────────────────────────────
    public double RsiRallyThreshold { get; set; }   // 40–60  RSI floor for relief-rally entry (short the bounce)

    // ── Exit genes ────────────────────────────────────────────────────────────────
    public double StopLossAtrMult           { get; set; }   // 0.5–2.0   ATR buffer above recent swing high
    public double TakeProfitAtrMult         { get; set; }   // 2.0–15.0  fixed profit target below entry
    public double TrailingActivationAtrMult { get; set; }   // 1.5–5.0   arm trail after this profit
    public double TrailingStopAtrMult       { get; set; }   // 1.0–5.0   trail distance from trough
    public int    MaxHoldCandles            { get; set; }   // 10–60     h1 bars before forced exit
    public double PositionSizePct           { get; set; }   // 0.01–0.05 fraction of capital per trade

    // ── Time-decay stop genes ────────────────────────────────────────────────────
    // After TimeStopBars h1 bars, the max tolerated adverse move from entry tightens linearly
    // from TimeStopLossPct (at bar TimeStopBars) down to 0% (at bar MaxHoldCandles).
    // Prevents slow-bleeding positions that never hit the hard stop.
    public int    TimeStopBars     { get; set; }   // 5–50    h1 bars before time-decay activates
    public double TimeStopLossPct  { get; set; }   // 0.02–0.25  max adverse move allowed at TimeStopBars (ratio)

    // ── Regime-conditional fitness gene ──────────────────────────────────────────
    public int    RegimeSustainedBars  { get; set; }   // 10–100    min consecutive bear-regime h1 bars before a trade counts

    public double Fitness { get; set; } = double.MinValue;

    public static RipShortGenotype Random(System.Random rng, RipShortGenotype? seed = null)
    {
        T Seed<T>(T random, T seeded) => seed == null ? random : seeded;
        return new()
        {
            RegimeLongEmaPeriod = Seed(rng.Next(100, 501),                    seed?.RegimeLongEmaPeriod ?? 200),
            RegimeSlopeLookback = Seed(rng.Next(10, 61),                      seed?.RegimeSlopeLookback ?? 30),
            EmaPeriod           = Seed(rng.Next(20, 101),                     seed?.EmaPeriod           ?? 50),
            AdxThreshold        = Seed(15.0 + rng.NextDouble() * 20.0,        seed?.AdxThreshold        ?? 25.0),
            RsiRallyThreshold   = Seed(40.0 + rng.NextDouble() * 20.0,        seed?.RsiRallyThreshold   ?? 52.0),
            StopLossAtrMult           = Seed(0.5 + rng.NextDouble() * 1.5,    seed?.StopLossAtrMult           ?? 0.8),
            TakeProfitAtrMult         = Seed(2.0 + rng.NextDouble() * 13.0,   seed?.TakeProfitAtrMult         ?? 7.0),
            TrailingActivationAtrMult = Seed(1.5 + rng.NextDouble() * 3.5,    seed?.TrailingActivationAtrMult ?? 2.5),
            TrailingStopAtrMult       = Seed(1.0 + rng.NextDouble() * 4.0,    seed?.TrailingStopAtrMult       ?? 2.0),
            MaxHoldCandles            = Seed(rng.Next(10, 61),                 seed?.MaxHoldCandles            ?? 30),
            PositionSizePct           = Seed(0.01 + rng.NextDouble() * 0.04,  seed?.PositionSizePct           ?? 0.03),
            TimeStopBars              = Seed(rng.Next(5, 51),                  seed?.TimeStopBars              ?? 999),
            TimeStopLossPct           = Seed(0.02 + rng.NextDouble() * 0.23,  seed?.TimeStopLossPct           ?? 0.99),
            RegimeSustainedBars  = Seed(rng.Next(10, 101),                    seed?.RegimeSustainedBars  ?? 30),
        };
    }

    public static RipShortGenotype Crossover(RipShortGenotype a, RipShortGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            RegimeLongEmaPeriod = Pick(a.RegimeLongEmaPeriod, b.RegimeLongEmaPeriod),
            RegimeSlopeLookback = Pick(a.RegimeSlopeLookback, b.RegimeSlopeLookback),
            EmaPeriod           = Pick(a.EmaPeriod,           b.EmaPeriod),
            AdxThreshold        = Pick(a.AdxThreshold,        b.AdxThreshold),
            RsiRallyThreshold   = Pick(a.RsiRallyThreshold,   b.RsiRallyThreshold),
            StopLossAtrMult           = Pick(a.StopLossAtrMult,           b.StopLossAtrMult),
            TakeProfitAtrMult         = Pick(a.TakeProfitAtrMult,         b.TakeProfitAtrMult),
            TrailingActivationAtrMult = Pick(a.TrailingActivationAtrMult, b.TrailingActivationAtrMult),
            TrailingStopAtrMult       = Pick(a.TrailingStopAtrMult,       b.TrailingStopAtrMult),
            MaxHoldCandles            = Pick(a.MaxHoldCandles,            b.MaxHoldCandles),
            PositionSizePct           = Pick(a.PositionSizePct,           b.PositionSizePct),
            TimeStopBars              = Pick(a.TimeStopBars,              b.TimeStopBars),
            TimeStopLossPct           = Pick(a.TimeStopLossPct,           b.TimeStopLossPct),
            RegimeSustainedBars  = Pick(a.RegimeSustainedBars,  b.RegimeSustainedBars),
        };
    }

    public RipShortGenotype Mutate(System.Random rng, double rate)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, int min, int max, int step)
        {
            if (rng.NextDouble() > rate) return val;
            return Math.Clamp(val + rng.Next(-step, step + 1), min, max);
        }
        return new RipShortGenotype
        {
            RegimeLongEmaPeriod = NudgeInt(RegimeLongEmaPeriod, 100, 500, 30),
            RegimeSlopeLookback = NudgeInt(RegimeSlopeLookback,  10,  60,  5),
            EmaPeriod           = NudgeInt(EmaPeriod,            20, 100, 10),
            AdxThreshold        = Nudge(AdxThreshold,           15.0, 35.0, 3.0),
            RsiRallyThreshold   = Nudge(RsiRallyThreshold,      40.0, 60.0, 3.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.5,  2.0, 0.3),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0, 15.0, 2.0),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.5,  5.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  5.0, 0.5),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 10, 60, 5),
            PositionSizePct           = Nudge(PositionSizePct,   0.01, 0.05, 0.005),
            TimeStopBars              = NudgeInt(TimeStopBars,    5, 50, 5),
            TimeStopLossPct           = Nudge(TimeStopLossPct,   0.02, 0.25, 0.03),
            RegimeSustainedBars  = NudgeInt(RegimeSustainedBars, 10, 100, 10),
        };
    }

    public RipShortGenotype ClampToBounds() => new()
    {
        RegimeLongEmaPeriod = Math.Clamp(RegimeLongEmaPeriod, 100, 500),
        RegimeSlopeLookback = Math.Clamp(RegimeSlopeLookback,  10,  60),
        EmaPeriod           = Math.Clamp(EmaPeriod,            20, 100),
        AdxThreshold        = Math.Clamp(AdxThreshold,        15.0, 35.0),
        RsiRallyThreshold   = Math.Clamp(RsiRallyThreshold,   40.0, 60.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.5,  2.0),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0, 15.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.5,  5.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  5.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,            10,   60),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.05),
        TimeStopBars              = Math.Clamp(TimeStopBars,               5,   50),
        TimeStopLossPct           = Math.Clamp(TimeStopLossPct,           0.02, 0.25),
        RegimeSustainedBars  = Math.Clamp(RegimeSustainedBars,  10,  100),
        Fitness = Fitness,
    };

    // ── Bayesian optimiser interface ──────────────────────────────────────────
    // [gene, 0]=min  [gene, 1]=max — mirrors the Clamp bounds above.
    public static readonly double[,] Bounds =
    {
        { 100,  500 }, // RegimeLongEmaPeriod
        {  10,   60 }, // RegimeSlopeLookback
        {  20,  100 }, // EmaPeriod
        {  15,   35 }, // AdxThreshold
        {  40,   60 }, // RsiRallyThreshold
        { 0.5,  2.0 }, // StopLossAtrMult
        { 2.0, 15.0 }, // TakeProfitAtrMult
        { 1.5,  5.0 }, // TrailingActivationAtrMult
        { 1.0,  5.0 }, // TrailingStopAtrMult
        {  10,   60 }, // MaxHoldCandles
        { 0.01,0.05 }, // PositionSizePct
        {   5,   50 }, // TimeStopBars
        { 0.02,0.25 }, // TimeStopLossPct
        {  10,  100 }, // RegimeSustainedBars
    };

    public double[] ToVector() =>
    [
        RegimeLongEmaPeriod, RegimeSlopeLookback, EmaPeriod,
        AdxThreshold, RsiRallyThreshold,
        StopLossAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult,
        MaxHoldCandles, PositionSizePct,
        TimeStopBars, TimeStopLossPct,
        RegimeSustainedBars,
    ];

    public static RipShortGenotype FromVector(double[] v) => new()
    {
        RegimeLongEmaPeriod       = Math.Clamp((int)Math.Round(v[0]),  100, 500),
        RegimeSlopeLookback       = Math.Clamp((int)Math.Round(v[1]),   10,  60),
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[2]),   20, 100),
        AdxThreshold              = Math.Clamp(v[3],  15.0, 35.0),
        RsiRallyThreshold         = Math.Clamp(v[4],  40.0, 60.0),
        StopLossAtrMult           = Math.Clamp(v[5],   0.5,  2.0),
        TakeProfitAtrMult         = Math.Clamp(v[6],   2.0, 15.0),
        TrailingActivationAtrMult = Math.Clamp(v[7],   1.5,  5.0),
        TrailingStopAtrMult       = Math.Clamp(v[8],   1.0,  5.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[9]),   10,  60),
        PositionSizePct           = Math.Clamp(v[10], 0.01, 0.05),
        TimeStopBars              = Math.Clamp((int)Math.Round(v[11]),   5,  50),
        TimeStopLossPct           = Math.Clamp(v[12], 0.02, 0.25),
        RegimeSustainedBars       = Math.Clamp((int)Math.Round(v[13]), 10, 100),
    };

    public override string ToString() =>
        $"BearEMA{RegimeLongEmaPeriod}(slope{RegimeSlopeLookback}) EMA{EmaPeriod} ADX(7,≥{AdxThreshold:F0}) " +
        $"RSI(7,rally≥{RsiRallyThreshold:F0}) " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} " +
        $"TStop({TimeStopBars}bars/{TimeStopLossPct:P0}) " +
        $"RegSust={RegimeSustainedBars} " +
        $"F={Fitness:F4}";
}
