namespace TradingGA;

// DipLong genotype: bull-regime RSI dip + bullish BoS. Mirror of RipShort.
// Fixed: RsiPeriod=7, AdxPeriod=7, SwingLowLookback=20.
public class DipLongGenotype
{
    public int    RegimeLongEmaPeriod { get; set; }   // 100–500  bull-regime gate EMA
    public int    RegimeSlopeLookback { get; set; }   // 10–60    EMA slope direction window
    public int    EmaPeriod           { get; set; }   // 20–100   short-term trend EMA
    public double AdxThreshold        { get; set; }   // 15–35    trend strength gate
    public double RsiDipThreshold     { get; set; }   // 35–55    RSI ceiling for dip entry
    public double StopLossAtrMult     { get; set; }   // 0.5–2.0  ATR buffer below swing low
    public double TakeProfitAtrMult   { get; set; }   // 2.0–15.0 fixed TP above entry
    public double TrailingActivationAtrMult { get; set; } // 1.5–5.0 arm trail
    public double TrailingStopAtrMult { get; set; }   // 1.0–5.0  trail distance
    public int    MaxHoldCandles      { get; set; }   // 10–60    h1 bars before forced exit
    public double PositionSizePct     { get; set; }   // 0.01–0.05 capital fraction
    public int    TimeStopBars        { get; set; }   // 5–50     h1 bars before time-decay
    public double TimeStopLossPct     { get; set; }   // 0.02–0.25 max loss at TimeStopBars
    public int    RegimeSustainedBars { get; set; }   // 10–100   min bull-regime bars before trade counts

    public double Fitness { get; set; } = double.MinValue;

    // Seeded init: 30% loose mutant, ~45% anchored / ~55% exploration.
    private const double SeedMutantProbability = 0.3;

    public static DipLongGenotype Random(System.Random rng, DipLongGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBounds().Mutate(rng, 0.5);

        return new()
        {
            RegimeLongEmaPeriod = rng.Next(100, 501),
            RegimeSlopeLookback = rng.Next(10, 61),
            EmaPeriod           = rng.Next(20, 101),
            AdxThreshold        = 15.0 + rng.NextDouble() * 20.0,
            RsiDipThreshold     = 35.0 + rng.NextDouble() * 20.0,
            StopLossAtrMult           = 0.5 + rng.NextDouble() * 1.5,
            TakeProfitAtrMult         = 2.0 + rng.NextDouble() * 13.0,
            TrailingActivationAtrMult = 1.5 + rng.NextDouble() * 3.5,
            TrailingStopAtrMult       = 1.0 + rng.NextDouble() * 4.0,
            MaxHoldCandles            = rng.Next(10, 61),
            PositionSizePct           = 0.01 + rng.NextDouble() * 0.04,
            TimeStopBars              = rng.Next(5, 51),
            TimeStopLossPct           = 0.02 + rng.NextDouble() * 0.23,
            RegimeSustainedBars  = rng.Next(10, 101),
        };
    }

    public static DipLongGenotype Crossover(DipLongGenotype a, DipLongGenotype b, System.Random rng)
    {
        T Pick<T>(T va, T vb) => rng.NextDouble() < 0.5 ? va : vb;
        return new()
        {
            RegimeLongEmaPeriod = Pick(a.RegimeLongEmaPeriod, b.RegimeLongEmaPeriod),
            RegimeSlopeLookback = Pick(a.RegimeSlopeLookback, b.RegimeSlopeLookback),
            EmaPeriod           = Pick(a.EmaPeriod,           b.EmaPeriod),
            AdxThreshold        = Pick(a.AdxThreshold,        b.AdxThreshold),
            RsiDipThreshold     = Pick(a.RsiDipThreshold,     b.RsiDipThreshold),
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

    public DipLongGenotype Mutate(System.Random rng, double rate)
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
        return new DipLongGenotype
        {
            RegimeLongEmaPeriod = NudgeInt(RegimeLongEmaPeriod, 100, 500, 30),
            RegimeSlopeLookback = NudgeInt(RegimeSlopeLookback,  10,  60,  5),
            EmaPeriod           = NudgeInt(EmaPeriod,            20, 100, 10),
            AdxThreshold        = Nudge(AdxThreshold,           15.0, 35.0, 3.0),
            RsiDipThreshold     = Nudge(RsiDipThreshold,        35.0, 55.0, 3.0),
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

    public DipLongGenotype ClampToBounds() => new()
    {
        RegimeLongEmaPeriod = Math.Clamp(RegimeLongEmaPeriod, 100, 500),
        RegimeSlopeLookback = Math.Clamp(RegimeSlopeLookback,  10,  60),
        EmaPeriod           = Math.Clamp(EmaPeriod,            20, 100),
        AdxThreshold        = Math.Clamp(AdxThreshold,        15.0, 35.0),
        RsiDipThreshold     = Math.Clamp(RsiDipThreshold,     35.0, 55.0),
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

    public static readonly double[,] Bounds =
    {
        { 100,  500 }, // RegimeLongEmaPeriod
        {  10,   60 }, // RegimeSlopeLookback
        {  20,  100 }, // EmaPeriod
        {  15,   35 }, // AdxThreshold
        {  35,   55 }, // RsiDipThreshold
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

    public static readonly double[,] BoundsLowVol =
    {
        { 100,  500 }, // RegimeLongEmaPeriod
        {  10,   60 }, // RegimeSlopeLookback
        {  20,  100 }, // EmaPeriod
        {  10,   30 }, // AdxThreshold (lower for low-vol)
        {  35,   55 }, // RsiDipThreshold
        { 0.5,  1.5 }, // StopLossAtrMult (tighter for low-vol)
        { 2.0,  6.0 }, // TakeProfitAtrMult (smaller for low-vol)
        { 1.5,  4.0 }, // TrailingActivationAtrMult
        { 1.0,  3.0 }, // TrailingStopAtrMult (tighter for low-vol)
        {  72, 200 }, // MaxHoldCandles (longer for low-vol)
        { 0.01,0.03 }, // PositionSizePct (smaller for low-vol)
        {  20,  80 }, // TimeStopBars (longer for low-vol)
        { 0.02,0.20 }, // TimeStopLossPct
        {  10,  100 }, // RegimeSustainedBars
    };

    public static readonly double[,] BoundsHighVol =
    {
        { 150,  400 }, // RegimeLongEmaPeriod
        {  15,   45 }, // RegimeSlopeLookback
        {  30,   80 }, // EmaPeriod
        {  30,   50 }, // AdxThreshold
        {  38,   52 }, // RsiDipThreshold
        { 1.5,  2.5 }, // StopLossAtrMult
        { 5.0, 15.0 }, // TakeProfitAtrMult
        { 2.5,  5.0 }, // TrailingActivationAtrMult
        { 2.0,  5.0 }, // TrailingStopAtrMult
        {  24,   72 }, // MaxHoldCandles
        { 0.03, 0.07 }, // PositionSizePct
        {  15,   45 }, // TimeStopBars
        { 0.08, 0.20 }, // TimeStopLossPct
        {  15,   80 }, // RegimeSustainedBars
    };

    public static readonly string[] ParameterNames =
    [
        "RegimeLongEmaPeriod", "RegimeSlopeLookback", "EmaPeriod",
        "AdxThreshold", "RsiDipThreshold",
        "StopLossAtrMult", "TakeProfitAtrMult",
        "TrailingActivationAtrMult", "TrailingStopAtrMult",
        "MaxHoldCandles", "PositionSizePct",
        "TimeStopBars", "TimeStopLossPct",
        "RegimeSustainedBars",
    ];

    // High-vol variant: seed-mutant uses HighVol clamp+mutate.
    public static DipLongGenotype RandomHighVol(System.Random rng, DipLongGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.MutateHighVol(rng, 0.5);

        return new()
        {
            RegimeLongEmaPeriod = rng.Next(150, 401),
            RegimeSlopeLookback = rng.Next(15, 46),
            EmaPeriod           = rng.Next(30, 81),
            AdxThreshold        = 30.0 + rng.NextDouble() * 20.0,
            RsiDipThreshold     = 38.0 + rng.NextDouble() * 14.0,
            StopLossAtrMult           = 1.5 + rng.NextDouble() * 1.0,
            TakeProfitAtrMult         = 5.0 + rng.NextDouble() * 10.0,
            TrailingActivationAtrMult = 2.5 + rng.NextDouble() * 2.5,
            TrailingStopAtrMult       = 2.0 + rng.NextDouble() * 3.0,
            MaxHoldCandles            = rng.Next(24, 73),
            PositionSizePct           = 0.03 + rng.NextDouble() * 0.04,
            TimeStopBars              = rng.Next(15, 46),
            TimeStopLossPct           = 0.08 + rng.NextDouble() * 0.12,
            RegimeSustainedBars  = rng.Next(15, 81),
        };
    }

    // High-vol variant operators — driven off BoundsHighVol.
    public static DipLongGenotype FromVectorHighVol(double[] v) => new()
    {
        RegimeLongEmaPeriod       = (int)Math.Clamp(Math.Round(v[0]),  BoundsHighVol[0, 0],  BoundsHighVol[0, 1]),
        RegimeSlopeLookback       = (int)Math.Clamp(Math.Round(v[1]),  BoundsHighVol[1, 0],  BoundsHighVol[1, 1]),
        EmaPeriod                 = (int)Math.Clamp(Math.Round(v[2]),  BoundsHighVol[2, 0],  BoundsHighVol[2, 1]),
        AdxThreshold              = Math.Clamp(v[3],  BoundsHighVol[3, 0],  BoundsHighVol[3, 1]),
        RsiDipThreshold           = Math.Clamp(v[4],  BoundsHighVol[4, 0],  BoundsHighVol[4, 1]),
        StopLossAtrMult           = Math.Clamp(v[5],  BoundsHighVol[5, 0],  BoundsHighVol[5, 1]),
        TakeProfitAtrMult         = Math.Clamp(v[6],  BoundsHighVol[6, 0],  BoundsHighVol[6, 1]),
        TrailingActivationAtrMult = Math.Clamp(v[7],  BoundsHighVol[7, 0],  BoundsHighVol[7, 1]),
        TrailingStopAtrMult       = Math.Clamp(v[8],  BoundsHighVol[8, 0],  BoundsHighVol[8, 1]),
        MaxHoldCandles            = (int)Math.Clamp(Math.Round(v[9]),  BoundsHighVol[9, 0],  BoundsHighVol[9, 1]),
        PositionSizePct           = Math.Clamp(v[10], BoundsHighVol[10, 0], BoundsHighVol[10, 1]),
        TimeStopBars              = (int)Math.Clamp(Math.Round(v[11]), BoundsHighVol[11, 0], BoundsHighVol[11, 1]),
        TimeStopLossPct           = Math.Clamp(v[12], BoundsHighVol[12, 0], BoundsHighVol[12, 1]),
        RegimeSustainedBars       = (int)Math.Clamp(Math.Round(v[13]), BoundsHighVol[13, 0], BoundsHighVol[13, 1]),
    };

    public DipLongGenotype ClampToBoundsHighVol()
    {
        var g = FromVectorHighVol(ToVector());
        g.Fitness = Fitness;
        return g;
    }

    public DipLongGenotype MutateHighVol(System.Random rng, double rate)
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

    public double[] ToVector() =>
    [
        RegimeLongEmaPeriod, RegimeSlopeLookback, EmaPeriod,
        AdxThreshold, RsiDipThreshold,
        StopLossAtrMult, TakeProfitAtrMult,
        TrailingActivationAtrMult, TrailingStopAtrMult,
        MaxHoldCandles, PositionSizePct,
        TimeStopBars, TimeStopLossPct,
        RegimeSustainedBars,
    ];

    public static DipLongGenotype FromVector(double[] v) => new()
    {
        RegimeLongEmaPeriod       = Math.Clamp((int)Math.Round(v[0]),  100, 500),
        RegimeSlopeLookback       = Math.Clamp((int)Math.Round(v[1]),   10,  60),
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[2]),   20, 100),
        AdxThreshold              = Math.Clamp(v[3],  15.0, 35.0),
        RsiDipThreshold           = Math.Clamp(v[4],  35.0, 55.0),
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

    public static DipLongGenotype FromVectorLowVol(double[] v) => new()
    {
        RegimeLongEmaPeriod       = Math.Clamp((int)Math.Round(v[0]),  100, 500),
        RegimeSlopeLookback       = Math.Clamp((int)Math.Round(v[1]),   10,  60),
        EmaPeriod                 = Math.Clamp((int)Math.Round(v[2]),   20, 100),
        AdxThreshold              = Math.Clamp(v[3],  10.0, 30.0),
        RsiDipThreshold           = Math.Clamp(v[4],  35.0, 55.0),
        StopLossAtrMult           = Math.Clamp(v[5],   0.5,  1.5),
        TakeProfitAtrMult         = Math.Clamp(v[6],   2.0,  6.0),
        TrailingActivationAtrMult = Math.Clamp(v[7],   1.5,  4.0),
        TrailingStopAtrMult       = Math.Clamp(v[8],   1.0,  3.0),
        MaxHoldCandles            = Math.Clamp((int)Math.Round(v[9]),   72, 200),
        PositionSizePct           = Math.Clamp(v[10], 0.01, 0.03),
        TimeStopBars              = Math.Clamp((int)Math.Round(v[11]),  20,  80),
        TimeStopLossPct           = Math.Clamp(v[12], 0.02, 0.20),
        RegimeSustainedBars       = Math.Clamp((int)Math.Round(v[13]), 10, 100),
    };

    // Low-vol variant: seed-mutant uses LowVol clamp+mutate.
    public static DipLongGenotype RandomLowVol(System.Random rng, DipLongGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < SeedMutantProbability)
            return seed.ClampToBoundsLowVol().MutateLowVol(rng, 0.5);

        return new()
        {
            RegimeLongEmaPeriod = rng.Next(100, 501),
            RegimeSlopeLookback = rng.Next(10, 61),
            EmaPeriod           = rng.Next(20, 101),
            AdxThreshold        = 10.0 + rng.NextDouble() * 20.0,
            RsiDipThreshold     = 35.0 + rng.NextDouble() * 20.0,
            StopLossAtrMult           = 0.5 + rng.NextDouble() * 1.0,
            TakeProfitAtrMult         = 2.0 + rng.NextDouble() * 4.0,
            TrailingActivationAtrMult = 1.5 + rng.NextDouble() * 2.5,
            TrailingStopAtrMult       = 1.0 + rng.NextDouble() * 2.0,
            MaxHoldCandles            = rng.Next(72, 201),
            PositionSizePct           = 0.01 + rng.NextDouble() * 0.02,
            TimeStopBars              = rng.Next(20, 81),
            TimeStopLossPct           = 0.02 + rng.NextDouble() * 0.18,
            RegimeSustainedBars  = rng.Next(10, 101),
        };
    }

    public DipLongGenotype ClampToBoundsLowVol() => new()
    {
        RegimeLongEmaPeriod = Math.Clamp(RegimeLongEmaPeriod, 100, 500),
        RegimeSlopeLookback = Math.Clamp(RegimeSlopeLookback,  10,  60),
        EmaPeriod           = Math.Clamp(EmaPeriod,            20, 100),
        AdxThreshold        = Math.Clamp(AdxThreshold,        10.0, 30.0),
        RsiDipThreshold     = Math.Clamp(RsiDipThreshold,     35.0, 55.0),
        StopLossAtrMult           = Math.Clamp(StopLossAtrMult,           0.5,  1.5),
        TakeProfitAtrMult         = Math.Clamp(TakeProfitAtrMult,         2.0,  6.0),
        TrailingActivationAtrMult = Math.Clamp(TrailingActivationAtrMult, 1.5,  4.0),
        TrailingStopAtrMult       = Math.Clamp(TrailingStopAtrMult,       1.0,  3.0),
        MaxHoldCandles            = Math.Clamp(MaxHoldCandles,            72, 200),
        PositionSizePct           = Math.Clamp(PositionSizePct,           0.01, 0.03),
        TimeStopBars              = Math.Clamp(TimeStopBars,               20,  80),
        TimeStopLossPct           = Math.Clamp(TimeStopLossPct,           0.02, 0.20),
        RegimeSustainedBars  = Math.Clamp(RegimeSustainedBars,  10,  100),
        Fitness = Fitness,
    };

    public DipLongGenotype MutateLowVol(System.Random rng, double rate)
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
        return new DipLongGenotype
        {
            RegimeLongEmaPeriod = NudgeInt(RegimeLongEmaPeriod, 100, 500, 30),
            RegimeSlopeLookback = NudgeInt(RegimeSlopeLookback,  10,  60,  5),
            EmaPeriod           = NudgeInt(EmaPeriod,            20, 100, 10),
            AdxThreshold        = Nudge(AdxThreshold,           10.0, 30.0, 3.0),
            RsiDipThreshold     = Nudge(RsiDipThreshold,        35.0, 55.0, 3.0),
            StopLossAtrMult           = Nudge(StopLossAtrMult,           0.5,  1.5, 0.2),
            TakeProfitAtrMult         = Nudge(TakeProfitAtrMult,         2.0,  6.0, 1.0),
            TrailingActivationAtrMult = Nudge(TrailingActivationAtrMult, 1.5,  4.0, 0.5),
            TrailingStopAtrMult       = Nudge(TrailingStopAtrMult,       1.0,  3.0, 0.4),
            MaxHoldCandles            = NudgeInt(MaxHoldCandles, 72, 200, 10),
            PositionSizePct           = Nudge(PositionSizePct,   0.01, 0.03, 0.004),
            TimeStopBars              = NudgeInt(TimeStopBars,    20, 80, 5),
            TimeStopLossPct           = Nudge(TimeStopLossPct,   0.02, 0.20, 0.03),
            RegimeSustainedBars  = NudgeInt(RegimeSustainedBars, 10, 100, 10),
        };
    }

    public override string ToString() =>
        $"BullEMA{RegimeLongEmaPeriod}(slope{RegimeSlopeLookback}) EMA{EmaPeriod} ADX(7,≥{AdxThreshold:F0}) " +
        $"RSI(7,dip≤{RsiDipThreshold:F0}) " +
        $"SL={StopLossAtrMult:F2}A TP={TakeProfitAtrMult:F2}A " +
        $"Trail({TrailingActivationAtrMult:F2}A/{TrailingStopAtrMult:F2}A) " +
        $"MaxH={MaxHoldCandles}bars Pos={PositionSizePct:P0} " +
        $"TStop({TimeStopBars}bars/{TimeStopLossPct:P0}) " +
        $"RegSust={RegimeSustainedBars} " +
        $"F={Fitness:F4}";
}
