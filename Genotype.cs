namespace TradingGA;

public class Genotype
{
    public int    RsiPeriod         { get; set; }
    public double RsiOverbought     { get; set; }   // min 65 — below that is noise, not a pump
    public int    BosCandlesWait    { get; set; }
    public double GridStepAtrMult   { get; set; }   // trailing stop / grid step width (×ATR)
    public double DcaTriggerAtrMult { get; set; }
    public int    EmaPeriod         { get; set; }
    public double BreakEvenAtrMult  { get; set; }
    public int    MaxDcaLevels      { get; set; }
    public double BosThreshold      { get; set; }   // lower-high sensitivity (0.990–0.999)
    public double VolumeMultiplier  { get; set; }   // volume spike filter (1.0–2.5)
    public int    RegimeAdxPeriod   { get; set; }   // ADX lookback for regime detection (7–21)
    public double RegimeAdxThreshold{ get; set; }   // ADX level separating ranging vs trending (15–35)

    public double Fitness { get; set; } = double.MinValue;

    public static Genotype Random(System.Random rng, bool atrMode = true) => new()
    {
        RsiPeriod          = rng.Next(7, 22),
        RsiOverbought      = rng.NextDouble() * 15 + 65,   // 65–80
        BosCandlesWait     = rng.Next(1, 6),
        GridStepAtrMult    = atrMode ? rng.NextDouble() * 4.7 + 0.3 : rng.NextDouble() * 1.5 + 0.3,
        DcaTriggerAtrMult  = atrMode ? rng.NextDouble() * 7.7 + 0.3 : rng.NextDouble() * 2.7 + 0.3,
        EmaPeriod          = rng.Next(10, 51),
        BreakEvenAtrMult   = rng.NextDouble() * 2.0,
        MaxDcaLevels       = rng.Next(0, 6),
        BosThreshold       = 0.990 + rng.NextDouble() * 0.009,
        VolumeMultiplier   = 1.0   + rng.NextDouble() * 1.5,
        RegimeAdxPeriod    = rng.Next(7, 22),
        RegimeAdxThreshold = 15.0  + rng.NextDouble() * 20.0,  // 15–35
    };

    public static Genotype Crossover(Genotype a, Genotype b, System.Random rng) => new()
    {
        RsiPeriod          = rng.NextDouble() < 0.5 ? a.RsiPeriod          : b.RsiPeriod,
        RsiOverbought      = rng.NextDouble() < 0.5 ? a.RsiOverbought      : b.RsiOverbought,
        BosCandlesWait     = rng.NextDouble() < 0.5 ? a.BosCandlesWait     : b.BosCandlesWait,
        GridStepAtrMult    = rng.NextDouble() < 0.5 ? a.GridStepAtrMult    : b.GridStepAtrMult,
        DcaTriggerAtrMult  = rng.NextDouble() < 0.5 ? a.DcaTriggerAtrMult  : b.DcaTriggerAtrMult,
        EmaPeriod          = rng.NextDouble() < 0.5 ? a.EmaPeriod          : b.EmaPeriod,
        BreakEvenAtrMult   = rng.NextDouble() < 0.5 ? a.BreakEvenAtrMult   : b.BreakEvenAtrMult,
        MaxDcaLevels       = rng.NextDouble() < 0.5 ? a.MaxDcaLevels       : b.MaxDcaLevels,
        BosThreshold       = rng.NextDouble() < 0.5 ? a.BosThreshold       : b.BosThreshold,
        VolumeMultiplier   = rng.NextDouble() < 0.5 ? a.VolumeMultiplier   : b.VolumeMultiplier,
        RegimeAdxPeriod    = rng.NextDouble() < 0.5 ? a.RegimeAdxPeriod    : b.RegimeAdxPeriod,
        RegimeAdxThreshold = rng.NextDouble() < 0.5 ? a.RegimeAdxThreshold : b.RegimeAdxThreshold,
    };

    public Genotype Mutate(System.Random rng, double mutationRate, bool atrMode = true)
    {
        double Nudge(double val, double min, double max, double scale)
        {
            if (rng.NextDouble() > mutationRate) return val;
            return Math.Clamp(val + (rng.NextDouble() - 0.5) * scale, min, max);
        }
        int NudgeInt(int val, int min, int max)
        {
            if (rng.NextDouble() > mutationRate) return val;
            return Math.Clamp(val + rng.Next(-2, 3), min, max);
        }

        return new Genotype
        {
            RsiPeriod          = NudgeInt(RsiPeriod, 7, 21),
            RsiOverbought      = Nudge(RsiOverbought, 65, 80, 3),    // floor at 65
            BosCandlesWait     = NudgeInt(BosCandlesWait, 1, 5),
            GridStepAtrMult    = atrMode ? Nudge(GridStepAtrMult,   0.3, 5.0, 0.4) : Nudge(GridStepAtrMult,   0.3, 1.8, 0.2),
            DcaTriggerAtrMult  = atrMode ? Nudge(DcaTriggerAtrMult, 0.3, 8.0, 0.6) : Nudge(DcaTriggerAtrMult, 0.3, 3.0, 0.3),
            EmaPeriod          = NudgeInt(EmaPeriod, 10, 50),
            BreakEvenAtrMult   = Nudge(BreakEvenAtrMult,  0.0, 2.0, 0.20),
            MaxDcaLevels       = NudgeInt(MaxDcaLevels, 0, 5),
            BosThreshold       = Nudge(BosThreshold,      0.990, 0.999, 0.002),
            VolumeMultiplier   = Nudge(VolumeMultiplier,   1.0,   2.5,  0.20),
            RegimeAdxPeriod    = NudgeInt(RegimeAdxPeriod, 7, 21),
            RegimeAdxThreshold = Nudge(RegimeAdxThreshold, 15.0, 35.0, 3.0),
        };
    }

    public string ToString(bool atrMode) =>
        $"RSI({RsiPeriod},OB={RsiOverbought:F1}) Bwait={BosCandlesWait} " +
        (atrMode
            ? $"G={GridStepAtrMult:F2}A D={DcaTriggerAtrMult:F2}A"
            : $"G={GridStepAtrMult:F2}% D={DcaTriggerAtrMult:F2}%") +
        $" E{EmaPeriod} BE={BreakEvenAtrMult:F2} " +
        $"DCA≤{MaxDcaLevels} BoS={BosThreshold:F3} Vol={VolumeMultiplier:F2}x " +
        $"Adx({RegimeAdxPeriod},{RegimeAdxThreshold:F0}) F={Fitness:F4}";

    public override string ToString() => ToString(atrMode: true);
}
