namespace TradingGA;

// FadeLong genotype: bear-regime oversold bounce. Mirror of FadeShort.
// Fixed: RsiPeriod=7, AdxPeriod=7. Exit ATR uses h4; MinDrop uses h1.
public class FadeLongGenotype
{
    public int    RegimePeriod { get; set; }   // 100–300  slow EMA for bear regime
    public int    EmaPeriod    { get; set; }   // 20–100   trend EMA
    public double AdxThreshold { get; set; }   // 22–45    downtrend gate
    public int    LookbackCandles { get; set; } // 12–120  h1 bars to locate swing low
    public double RsiOversold     { get; set; } // 20–40   RSI ceiling for swing low
    public double RsiDivThreshold { get; set; } // 5–15    min RSI recovery from swing low
    public double MinDropAtrMult  { get; set; } // 5–12    min drop (h1 ATR)
    public double StopLossAtrMult { get; set; } // 0.3–2.0 ATR buffer below swing low
    public double MaeAtrMult      { get; set; } // 1.5–4.0 max adverse excursion floor
    public double TakeProfitAtrMult { get; set; } // 2.0–15.0 fixed target
    public double TrailingActivationAtrMult { get; set; } // 1.0–4.0 arm trail
    public double TrailingStopAtrMult { get; set; } // 1.0–5.0 trail distance
    public int    MaxHoldCandles  { get; set; } // 24–120  h1 bars before forced exit
    public double PositionSizePct { get; set; } // 0.01–0.05 capital fraction
    public int    RegimeSustainedBars { get; set; } // 10–100 min bear-regime bars before trade counts

    public double Fitness { get; set; } = double.MinValue;

    // Seeded init: 30% loose mutant, ~45% anchored / ~55% exploration.
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
