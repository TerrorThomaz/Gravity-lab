namespace TradingGA;

// Regime  genes = entry filter (when/whether to take a trade)
// Exit    genes = pump-short trade management
// Ranging genes = grid-long trade management (decoupled from pump)
public enum GeneBlock { All, Regime, Exit, Ranging }

public class Genotype
{
    // ── Regime / entry-filter genes ───────────────────────────────────────────
    public int    RsiPeriod         { get; set; }
    public double RsiOverbought     { get; set; }   // min 68 — gives execution headroom vs live fills
    public int    BosCandlesWait    { get; set; }
    public int    EmaPeriod         { get; set; }
    public double BosThreshold      { get; set; }   // lower-high sensitivity (0.990–0.999)
    public double VolumeMultiplier  { get; set; }   // volume spike filter (1.0–2.5)
    public int    RegimeAdxPeriod   { get; set; }   // ADX lookback for regime detection (7–21)
    public double RegimeAdxThreshold{ get; set; }   // ADX level separating ranging vs trending (15–35)

    // ── Pump-short exit genes ─────────────────────────────────────────────────
    public double GridStepAtrMult   { get; set; }   // pump trailing stop width (×ATR)
    public double DcaTriggerAtrMult { get; set; }   // pump DCA trigger — retrace before adding (×ATR, min 2.0)
    public double GridDcaAtrMult    { get; set; }   // pump recovery-leg exit trigger (×ATR, 0.3–4.0)
    public double BreakEvenAtrMult  { get; set; }   // pump break-even arm (×ATR)
    public int    MaxDcaLevels      { get; set; }   // pump DCA depth (0–5)
    public double HardStopAtrMult   { get; set; }   // pump/grid hard stop (×ATR, 4–15)

    // ── Ranging / grid-long genes (decoupled from pump) ───────────────────────
    public double RangeGridStep     { get; set; }   // grid trailing TP width (×ATR, 0.3–2.0)
    public double RangeBreakEven    { get; set; }   // grid break-even arm (×ATR, 0.0–1.0)
    public double RangeDcaStep      { get; set; }   // grid entry/DCA dip trigger (×ATR, 0.3–4.0)
    public int    RangeMaxDca       { get; set; }   // grid DCA depth (0–6)

    public double Fitness { get; set; } = double.MinValue;

    public static Genotype Random(System.Random rng, bool atrMode = true,
        Genotype? anchor = null, GeneBlock block = GeneBlock.All)
    {
        bool regimeFree  = block is GeneBlock.All or GeneBlock.Regime  || anchor == null;
        bool exitFree    = block is GeneBlock.All or GeneBlock.Exit    || anchor == null;
        bool rangingFree = block is GeneBlock.All or GeneBlock.Ranging || anchor == null;

        return new()
        {
            RsiPeriod          = regimeFree ? rng.Next(7, 22)                      : anchor!.RsiPeriod,
            RsiOverbought      = regimeFree ? rng.NextDouble() * 12 + 68            : anchor!.RsiOverbought,
            BosCandlesWait     = regimeFree ? rng.Next(1, 6)                        : anchor!.BosCandlesWait,
            EmaPeriod          = regimeFree ? rng.Next(10, 51)                      : anchor!.EmaPeriod,
            BosThreshold       = regimeFree ? 0.990 + rng.NextDouble() * 0.009      : anchor!.BosThreshold,
            VolumeMultiplier   = regimeFree ? 1.0   + rng.NextDouble() * 1.5        : anchor!.VolumeMultiplier,
            RegimeAdxPeriod    = regimeFree ? rng.Next(7, 22)                       : anchor!.RegimeAdxPeriod,
            RegimeAdxThreshold = regimeFree ? 15.0  + rng.NextDouble() * 20.0       : anchor!.RegimeAdxThreshold,

            GridStepAtrMult    = exitFree ? (atrMode ? rng.NextDouble() * 4.4 + 0.6 : rng.NextDouble() * 1.2 + 0.6) : anchor!.GridStepAtrMult,
            DcaTriggerAtrMult  = exitFree ? (atrMode ? rng.NextDouble() * 6.0 + 2.0 : rng.NextDouble() * 1.0 + 2.0) : anchor!.DcaTriggerAtrMult,
            GridDcaAtrMult     = exitFree ? rng.NextDouble() * 3.7 + 0.3            : anchor!.GridDcaAtrMult,
            BreakEvenAtrMult   = exitFree ? rng.NextDouble() * 2.0                  : anchor!.BreakEvenAtrMult,
            MaxDcaLevels       = exitFree ? rng.Next(0, 6)                          : anchor!.MaxDcaLevels,
            HardStopAtrMult    = exitFree ? 4.0 + rng.NextDouble() * 11.0           : anchor!.HardStopAtrMult,

            RangeGridStep      = rangingFree ? rng.NextDouble() * 1.7 + 0.3         : anchor!.RangeGridStep,
            RangeBreakEven     = rangingFree ? rng.NextDouble() * 1.0               : anchor!.RangeBreakEven,
            RangeDcaStep       = rangingFree ? rng.NextDouble() * 3.7 + 0.3         : anchor!.RangeDcaStep,
            RangeMaxDca        = rangingFree ? rng.Next(0, 7)                       : anchor!.RangeMaxDca,
        };
    }

    public static Genotype Crossover(Genotype a, Genotype b, System.Random rng, GeneBlock block = GeneBlock.All)
    {
        bool regimeFree  = block is GeneBlock.All or GeneBlock.Regime;
        bool exitFree    = block is GeneBlock.All or GeneBlock.Exit;
        bool rangingFree = block is GeneBlock.All or GeneBlock.Ranging;

        T Pick<T>(bool free, T va, T vb) => free ? (rng.NextDouble() < 0.5 ? va : vb) : va;

        return new()
        {
            RsiPeriod          = Pick(regimeFree, a.RsiPeriod,          b.RsiPeriod),
            RsiOverbought      = Pick(regimeFree, a.RsiOverbought,      b.RsiOverbought),
            BosCandlesWait     = Pick(regimeFree, a.BosCandlesWait,     b.BosCandlesWait),
            EmaPeriod          = Pick(regimeFree, a.EmaPeriod,          b.EmaPeriod),
            BosThreshold       = Pick(regimeFree, a.BosThreshold,       b.BosThreshold),
            VolumeMultiplier   = Pick(regimeFree, a.VolumeMultiplier,   b.VolumeMultiplier),
            RegimeAdxPeriod    = Pick(regimeFree, a.RegimeAdxPeriod,    b.RegimeAdxPeriod),
            RegimeAdxThreshold = Pick(regimeFree, a.RegimeAdxThreshold, b.RegimeAdxThreshold),

            GridStepAtrMult    = Pick(exitFree, a.GridStepAtrMult,   b.GridStepAtrMult),
            DcaTriggerAtrMult  = Pick(exitFree, a.DcaTriggerAtrMult, b.DcaTriggerAtrMult),
            GridDcaAtrMult     = Pick(exitFree, a.GridDcaAtrMult,    b.GridDcaAtrMult),
            BreakEvenAtrMult   = Pick(exitFree, a.BreakEvenAtrMult,  b.BreakEvenAtrMult),
            MaxDcaLevels       = Pick(exitFree, a.MaxDcaLevels,      b.MaxDcaLevels),
            HardStopAtrMult    = Pick(exitFree, a.HardStopAtrMult,   b.HardStopAtrMult),

            RangeGridStep      = Pick(rangingFree, a.RangeGridStep,  b.RangeGridStep),
            RangeBreakEven     = Pick(rangingFree, a.RangeBreakEven, b.RangeBreakEven),
            RangeDcaStep       = Pick(rangingFree, a.RangeDcaStep,   b.RangeDcaStep),
            RangeMaxDca        = Pick(rangingFree, a.RangeMaxDca,    b.RangeMaxDca),
        };
    }

    public Genotype Mutate(System.Random rng, double mutationRate, bool atrMode = true, GeneBlock block = GeneBlock.All)
    {
        bool regimeFree  = block is GeneBlock.All or GeneBlock.Regime;
        bool exitFree    = block is GeneBlock.All or GeneBlock.Exit;
        bool rangingFree = block is GeneBlock.All or GeneBlock.Ranging;

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
            RsiPeriod          = regimeFree ? NudgeInt(RsiPeriod, 7, 21)                         : RsiPeriod,
            RsiOverbought      = regimeFree ? Nudge(RsiOverbought,      68, 80,    3)             : RsiOverbought,
            BosCandlesWait     = regimeFree ? NudgeInt(BosCandlesWait, 1, 5)                      : BosCandlesWait,
            EmaPeriod          = regimeFree ? NudgeInt(EmaPeriod, 10, 50)                         : EmaPeriod,
            BosThreshold       = regimeFree ? Nudge(BosThreshold,      0.990, 0.999, 0.002)       : BosThreshold,
            VolumeMultiplier   = regimeFree ? Nudge(VolumeMultiplier,   1.0,   2.5,  0.20)        : VolumeMultiplier,
            RegimeAdxPeriod    = regimeFree ? NudgeInt(RegimeAdxPeriod, 7, 21)                    : RegimeAdxPeriod,
            RegimeAdxThreshold = regimeFree ? Nudge(RegimeAdxThreshold, 15.0, 35.0,  3.0)        : RegimeAdxThreshold,

            GridStepAtrMult    = exitFree ? (atrMode
                                    ? Nudge(GridStepAtrMult,   0.6, 5.0, 0.4)
                                    : Nudge(GridStepAtrMult,   0.6, 1.8, 0.2))                   : GridStepAtrMult,
            DcaTriggerAtrMult  = exitFree ? (atrMode
                                    ? Nudge(DcaTriggerAtrMult, 2.0, 8.0, 0.6)
                                    : Nudge(DcaTriggerAtrMult, 2.0, 3.0, 0.3))                   : DcaTriggerAtrMult,
            GridDcaAtrMult     = exitFree ? Nudge(GridDcaAtrMult,    0.3, 4.0,  0.3)             : GridDcaAtrMult,
            BreakEvenAtrMult   = exitFree ? Nudge(BreakEvenAtrMult,  0.0, 2.0,  0.20)            : BreakEvenAtrMult,
            MaxDcaLevels       = exitFree ? NudgeInt(MaxDcaLevels, 0, 5)                          : MaxDcaLevels,
            HardStopAtrMult    = exitFree ? Nudge(HardStopAtrMult,  4.0, 15.0,  1.0)             : HardStopAtrMult,

            RangeGridStep      = rangingFree ? Nudge(RangeGridStep,  0.3, 2.0, 0.2)              : RangeGridStep,
            RangeBreakEven     = rangingFree ? Nudge(RangeBreakEven, 0.0, 1.0, 0.1)              : RangeBreakEven,
            RangeDcaStep       = rangingFree ? Nudge(RangeDcaStep,   0.3, 4.0, 0.3)              : RangeDcaStep,
            RangeMaxDca        = rangingFree ? NudgeInt(RangeMaxDca, 0, 6)                        : RangeMaxDca,
        };
    }

    public Genotype ClampToBounds(bool atrMode = true) => new()
    {
        RsiPeriod          = Math.Clamp(RsiPeriod,          7,    21),
        RsiOverbought      = Math.Clamp(RsiOverbought,      68.0, 80.0),
        BosCandlesWait     = Math.Clamp(BosCandlesWait,     1,    5),
        EmaPeriod          = Math.Clamp(EmaPeriod,          10,   50),
        BosThreshold       = Math.Clamp(BosThreshold,       0.990, 0.999),
        VolumeMultiplier   = Math.Clamp(VolumeMultiplier,   1.0,  2.5),
        RegimeAdxPeriod    = Math.Clamp(RegimeAdxPeriod,    7,    21),
        RegimeAdxThreshold = Math.Clamp(RegimeAdxThreshold, 15.0, 35.0),
        GridStepAtrMult    = atrMode
                             ? Math.Clamp(GridStepAtrMult,  0.6,  5.0)
                             : Math.Clamp(GridStepAtrMult,  0.6,  1.8),
        DcaTriggerAtrMult  = atrMode
                             ? Math.Clamp(DcaTriggerAtrMult, 2.0, 8.0)
                             : Math.Clamp(DcaTriggerAtrMult, 2.0, 3.0),
        GridDcaAtrMult     = Math.Clamp(GridDcaAtrMult,     0.3,  4.0),
        BreakEvenAtrMult   = Math.Clamp(BreakEvenAtrMult,   0.0,  2.0),
        MaxDcaLevels       = Math.Clamp(MaxDcaLevels,       0,    5),
        HardStopAtrMult    = HardStopAtrMult > 0
                             ? Math.Clamp(HardStopAtrMult,  4.0, 15.0)
                             : 8.0,
        RangeGridStep      = RangeGridStep  > 0 ? Math.Clamp(RangeGridStep,  0.3, 2.0) : 1.0,
        RangeBreakEven     = Math.Clamp(RangeBreakEven, 0.0, 1.0),
        RangeDcaStep       = RangeDcaStep   > 0 ? Math.Clamp(RangeDcaStep,   0.3, 4.0) : 1.0,
        RangeMaxDca        = RangeMaxDca    > 0 ? Math.Clamp(RangeMaxDca,    0,   6)   : 3,
        Fitness            = Fitness,
    };

    public string ToString(bool atrMode) =>
        $"RSI({RsiPeriod},OB={RsiOverbought:F1}) Bwait={BosCandlesWait} " +
        (atrMode
            ? $"G={GridStepAtrMult:F2}A D={DcaTriggerAtrMult:F2}A GD={GridDcaAtrMult:F2}A"
            : $"G={GridStepAtrMult:F2}% D={DcaTriggerAtrMult:F2}% GD={GridDcaAtrMult:F2}%") +
        $" E{EmaPeriod} BE={BreakEvenAtrMult:F2} HS={HardStopAtrMult:F1}A " +
        $"DCA≤{MaxDcaLevels} BoS={BosThreshold:F3} Vol={VolumeMultiplier:F2}x " +
        $"Adx({RegimeAdxPeriod},{RegimeAdxThreshold:F0}) " +
        $"Rng(G={RangeGridStep:F2}A BE={RangeBreakEven:F2} Dca={RangeDcaStep:F2}A ≤{RangeMaxDca}) " +
        $"F={Fitness:F4}";

    public override string ToString() => ToString(atrMode: true);
}
