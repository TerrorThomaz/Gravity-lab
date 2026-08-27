namespace TradingGA;

// Router genotype: regime thresholds + transition/ETH-blend genes.
// The four "Mult"/"Carry" genes are ON/OFF switches at routing time (read as > 0 only).
// Their magnitude is used only by RegimeRouterGA.FilterActive for GA scoring.
public class RegimeRouterGenotype
{
    public const int MaxHmmStates = 6;
    public const int HmmStrategyRows = 8;
    public const int HmmFavorabilityLen = HmmStrategyRows * MaxHmmStates;

    public static readonly RegimeRouterGA.StrategyKind[] HmmStrategies =
    {
        RegimeRouterGA.StrategyKind.FadeShort,
        RegimeRouterGA.StrategyKind.Grid,
        RegimeRouterGA.StrategyKind.GridShort,
        RegimeRouterGA.StrategyKind.DipLong,
        RegimeRouterGA.StrategyKind.FadeLong,
        RegimeRouterGA.StrategyKind.RipShort,
        RegimeRouterGA.StrategyKind.SwingLong,
        RegimeRouterGA.StrategyKind.AccumulationGrid,
    };

    public double BullMinBars             { get; set; }  // [50, 500]
    public double BullMinConf             { get; set; }  // [0.10, 0.80]
    public double BearMinBars             { get; set; }  // [24, 200]  — FadeLong's confirmed-bear gate
    public double BearMinConf             { get; set; }  // [0.10, 0.80]
    public double RipShortBearMinBars     { get; set; }  // [50, 500]  — RipShort's own confirmed-bear gate
    public double RipShortBearMinConf     { get; set; }  // [0.10, 0.80]

    // FadeShort's own confirmed-bear gate. Separate so one shared threshold doesn't compromise all three.
    // FadeShortBearOnly: >0 = Bear-only mode; 0 = legacy not-confirmed-Bull.
    public double FadeShortBearMinBars    { get; set; }  // [20, 400]
    public double FadeShortBearMinConf    { get; set; }  // [0.10, 0.80]
    public double FadeShortBearOnly       { get; set; }  // >0 = Bear-only; 0 = legacy not-confirmed-Bull
    public double GridMaxConf             { get; set; }  // [0.10, 0.70]
    public double EthBlendWeight          { get; set; }  // [0.00, 0.50]
    public double TransitionSizeMult      { get; set; }  // [0.00, 1.00]
    public double EarlyBullFromBearMult   { get; set; }  // [0.00, 1.00]
    public double EarlyBullFromRangingMult{ get; set; }  // [0.00, 1.00]
    public double EarlyBullBearCarry      { get; set; }  // [0.00, 1.00]

    public double[]? Favorability { get; set; }
    public double[]? Biases { get; set; }
    public double HmmStatesN { get; set; } = 4;

    public double SmoothingAlpha { get; set; } = 0.75;
    public double BlendFactor { get; set; } = 0.75;
    public double StrategyFloorPct { get; set; } = 0.10;
    public double ActivateThreshold { get; set; } = 0.62;
    public double DeactivateThreshold { get; set; } = 0.42;
    public double MinHoldBars { get; set; } = 24;

    public double Fitness { get; set; }

    public bool IsHmmGenotype => Favorability is { Length: HmmFavorabilityLen }
                                 && Biases is { Length: HmmStrategyRows };


    public static readonly double[,] Bounds =
    {
        {  20, 100 },   // 0  BullMinBars
        { 0.10, 0.80 }, // 1  BullMinConf
        {  20, 100 },   // 2  BearMinBars
        { 0.10, 0.80 }, // 3  BearMinConf
        {  20, 100 },   // 4  RipShortBearMinBars
        { 0.10, 0.80 }, // 5  RipShortBearMinConf
        { 0.10, 0.70 }, // 6  GridMaxConf
        { 0.00, 0.50 }, // 7  EthBlendWeight
        { 0.00, 1.00 }, // 8  TransitionSizeMult
        { 0.00, 1.00 }, // 9  EarlyBullFromBearMult
        { 0.00, 1.00 }, // 10 EarlyBullFromRangingMult
        { 0.00, 1.00 }, // 11 EarlyBullBearCarry
        {  20, 100 },   // 12 FadeShortBearMinBars
        { 0.10, 0.80 }, // 13 FadeShortBearMinConf
        { 0.00, 1.00 }, // 14 FadeShortBearOnly
    };

    public static readonly double[,] HmmBounds = BuildHmmBounds();

    private static double[,] BuildHmmBounds()
    {
        int legacy = Bounds.GetLength(0);
        int hmmGenes = HmmFavorabilityLen + HmmStrategyRows + 1 + 6;
        var b = new double[legacy + hmmGenes, 2];
        for (int i = 0; i < legacy; i++) { b[i, 0] = Bounds[i, 0]; b[i, 1] = Bounds[i, 1]; }
        int off = legacy;
        for (int i = 0; i < HmmFavorabilityLen; i++) { b[off + i, 0] = -1.0; b[off + i, 1] = 1.0; }
        off += HmmFavorabilityLen;
        for (int i = 0; i < HmmStrategyRows; i++) { b[off + i, 0] = -1.0; b[off + i, 1] = 1.0; }
        off += HmmStrategyRows;
        b[off, 0] = 3.0; b[off, 1] = 6.0;
        off += 1;
        b[off, 0] = 0.50; b[off, 1] = 0.95;
        off += 1;
        b[off, 0] = 0.60; b[off, 1] = 0.90;
        off += 1;
        b[off, 0] = 0.10; b[off, 1] = 0.20;
        off += 1;
        b[off, 0] = 0.55; b[off, 1] = 0.70;
        off += 1;
        b[off, 0] = 0.35; b[off, 1] = 0.50;
        off += 1;
        b[off, 0] = 12; b[off, 1] = 144;
        return b;
    }


    public double[] ToVector()
    {
        int legacy = Bounds.GetLength(0);
        if (!IsHmmGenotype)
        {
            return
            [
                BullMinBars, BullMinConf, BearMinBars, BearMinConf,
                RipShortBearMinBars, RipShortBearMinConf, GridMaxConf,
                EthBlendWeight, TransitionSizeMult,
                EarlyBullFromBearMult, EarlyBullFromRangingMult, EarlyBullBearCarry,
                FadeShortBearMinBars, FadeShortBearMinConf, FadeShortBearOnly,
            ];
        }

        var v = new double[legacy + HmmFavorabilityLen + HmmStrategyRows + 1 + 6];
        v[0]  = BullMinBars; v[1]  = BullMinConf; v[2]  = BearMinBars; v[3]  = BearMinConf;
        v[4]  = RipShortBearMinBars; v[5] = RipShortBearMinConf; v[6] = GridMaxConf;
        v[7]  = EthBlendWeight; v[8] = TransitionSizeMult;
        v[9]  = EarlyBullFromBearMult; v[10] = EarlyBullFromRangingMult; v[11] = EarlyBullBearCarry;
        v[12] = FadeShortBearMinBars; v[13] = FadeShortBearMinConf; v[14] = FadeShortBearOnly;

        int off = legacy;
        for (int i = 0; i < HmmFavorabilityLen; i++) v[off + i] = Favorability![i];
        off += HmmFavorabilityLen;
        for (int i = 0; i < HmmStrategyRows; i++) v[off + i] = Biases![i];
        v[off + HmmStrategyRows] = HmmStatesN;
        off += HmmStrategyRows + 1;
        v[off] = SmoothingAlpha;
        v[off + 1] = BlendFactor;
        v[off + 2] = StrategyFloorPct;
        v[off + 3] = ActivateThreshold;
        v[off + 4] = DeactivateThreshold;
        v[off + 5] = MinHoldBars;
        return v;
    }

    public static RegimeRouterGenotype FromVector(double[] v)
    {
        var g = new RegimeRouterGenotype
        {
            BullMinBars              = Math.Clamp(v[0],  20, 100),
            BullMinConf              = Math.Clamp(v[1], 0.10, 0.80),
            BearMinBars              = Math.Clamp(v[2],  20, 100),
            BearMinConf              = Math.Clamp(v[3], 0.10, 0.80),
            RipShortBearMinBars      = Math.Clamp(v[4],  20, 100),
            RipShortBearMinConf      = Math.Clamp(v[5], 0.10, 0.80),
            GridMaxConf              = Math.Clamp(v[6], 0.10, 0.70),
            EthBlendWeight           = Math.Clamp(v[7], 0.00, 0.50),
            TransitionSizeMult       = Math.Clamp(v[8], 0.00, 1.00),
            EarlyBullFromBearMult    = Math.Clamp(v[9], 0.00, 1.00),
            EarlyBullFromRangingMult = Math.Clamp(v[10], 0.00, 1.00),
            FadeShortBearMinBars     = Math.Clamp(v[12],  20, 100),
            FadeShortBearMinConf     = Math.Clamp(v[13], 0.10, 0.80),
            FadeShortBearOnly        = Math.Clamp(v[14], 0.00, 1.00),
            EarlyBullBearCarry       = Math.Clamp(v[11], 0.00, 1.00),
        };

        int legacy = Bounds.GetLength(0);
        if (v.Length >= legacy + HmmFavorabilityLen + HmmStrategyRows + 1)
        {
            var fav = new double[HmmFavorabilityLen];
            int off = legacy;
            for (int i = 0; i < HmmFavorabilityLen; i++) fav[i] = Math.Clamp(v[off + i], -1.0, 1.0);
            off += HmmFavorabilityLen;
            var bias = new double[HmmStrategyRows];
            for (int i = 0; i < HmmStrategyRows; i++) bias[i] = Math.Clamp(v[off + i], -1.0, 1.0);
            double statesN = Math.Clamp(v[off + HmmStrategyRows], 3.0, 6.0);
            g.Favorability = fav;
            g.Biases = bias;
            g.HmmStatesN = statesN;
            off += HmmStrategyRows + 1;

            if (v.Length >= legacy + HmmFavorabilityLen + HmmStrategyRows + 1 + 6)
            {
                g.SmoothingAlpha      = Math.Clamp(v[off], 0.50, 0.95);
                g.BlendFactor         = Math.Clamp(v[off + 1], 0.60, 0.90);
                g.StrategyFloorPct    = Math.Clamp(v[off + 2], 0.10, 0.20);
                g.ActivateThreshold   = Math.Clamp(v[off + 3], 0.55, 0.70);
                g.DeactivateThreshold = Math.Clamp(v[off + 4], 0.35, 0.50);
                g.MinHoldBars         = Math.Clamp(v[off + 5], 12, 144);
            }
        }

        return g;
    }


    public static RegimeRouterGenotype Random(System.Random rng, RegimeRouterGenotype? seed = null, bool hmmGenes = false)
    {
        if (seed != null && rng.NextDouble() < 0.3)
        {
            var m = seed.Mutate(rng, 0.5);
            if (hmmGenes && !m.IsHmmGenotype) m = m.WithHmmInit(rng);
            return m;
        }

        var g = new RegimeRouterGenotype
        {
            BullMinBars              = rng.NextDouble() * 80  + 20,
            BullMinConf              = rng.NextDouble() * 0.70 + 0.10,
            BearMinBars              = rng.NextDouble() * 80  + 20,
            BearMinConf              = rng.NextDouble() * 0.70 + 0.10,
            FadeShortBearMinBars     = rng.NextDouble() * 80  + 20,
            FadeShortBearMinConf     = rng.NextDouble() * 0.70 + 0.10,
            FadeShortBearOnly        = rng.NextDouble(),
            RipShortBearMinBars      = rng.NextDouble() * 80  + 20,
            RipShortBearMinConf      = rng.NextDouble() * 0.70 + 0.10,
            GridMaxConf              = rng.NextDouble() * 0.60 + 0.10,
            EthBlendWeight           = rng.NextDouble() * 0.50,
            TransitionSizeMult       = rng.NextDouble(),
            EarlyBullFromBearMult    = rng.NextDouble(),
            EarlyBullFromRangingMult = rng.NextDouble(),
            EarlyBullBearCarry       = rng.NextDouble(),
        };

        if (hmmGenes || seed is { IsHmmGenotype: true })
        {
            var fav = new double[HmmFavorabilityLen];
            for (int i = 0; i < fav.Length; i++)
                fav[i] = Math.Clamp(rng.NextGaussian() * 0.2, -1.0, 1.0);
            var bias = new double[HmmStrategyRows];
            for (int i = 0; i < bias.Length; i++)
                bias[i] = Math.Clamp(rng.NextGaussian() * 0.2, -1.0, 1.0);
            g.Favorability = fav;
            g.Biases = bias;
            g.HmmStatesN = 3 + rng.NextDouble() * 3.0;
            g.SmoothingAlpha = 0.50 + rng.NextDouble() * 0.45;
            g.BlendFactor = 0.60 + rng.NextDouble() * 0.30;
            g.StrategyFloorPct = 0.10 + rng.NextDouble() * 0.10;
            g.ActivateThreshold = 0.55 + rng.NextDouble() * 0.15;
            g.DeactivateThreshold = 0.35 + rng.NextDouble() * 0.15;
            g.MinHoldBars = 12 + rng.NextDouble() * 132;
        }

        return g;
    }

    // Clone a legacy genotype and attach near-neutral HMM genes so hmmMode training can
    // start from an existing threshold router. Small N(0, 0.2) favorability ≈ all weights
    // near 0.5 — the GA then moves them.
    public RegimeRouterGenotype WithHmmInit(System.Random rng)
    {
        var g = (RegimeRouterGenotype)MemberwiseClone();
        var fav = new double[HmmFavorabilityLen];
        for (int i = 0; i < fav.Length; i++)
            fav[i] = Math.Clamp(rng.NextGaussian() * 0.2, -1.0, 1.0);
        var bias = new double[HmmStrategyRows];
        for (int i = 0; i < bias.Length; i++)
            bias[i] = Math.Clamp(rng.NextGaussian() * 0.2, -1.0, 1.0);
        g.Favorability = fav;
        g.Biases = bias;
        g.HmmStatesN = 4.0;
        g.SmoothingAlpha = 0.75;
        g.BlendFactor = 0.75;
        g.StrategyFloorPct = 0.10;
        g.ActivateThreshold = 0.62;
        g.DeactivateThreshold = 0.42;
        g.MinHoldBars = 24;
        return g;
    }

    // hmmMode routing reads ONLY the favorability matrix — the threshold genes never affect
    // fitness, so under mutation they drift to random values and silently corrupt the
    // GRAVITY_HMM=0 fallback (the "legacy" router stops being the trained legacy router).
    // Freeze them by copying from a template every generation.
    public void CopyThresholdsFrom(RegimeRouterGenotype src)
    {
        BullMinBars              = src.BullMinBars;
        BullMinConf              = src.BullMinConf;
        BearMinBars              = src.BearMinBars;
        BearMinConf              = src.BearMinConf;
        RipShortBearMinBars      = src.RipShortBearMinBars;
        RipShortBearMinConf      = src.RipShortBearMinConf;
        FadeShortBearMinBars     = src.FadeShortBearMinBars;
        FadeShortBearMinConf     = src.FadeShortBearMinConf;
        FadeShortBearOnly        = src.FadeShortBearOnly;
        GridMaxConf              = src.GridMaxConf;
        EthBlendWeight           = src.EthBlendWeight;
        TransitionSizeMult       = src.TransitionSizeMult;
        EarlyBullFromBearMult    = src.EarlyBullFromBearMult;
        EarlyBullFromRangingMult = src.EarlyBullFromRangingMult;
        EarlyBullBearCarry       = src.EarlyBullBearCarry;
    }

    public RegimeRouterGenotype Mutate(System.Random rng, double rate)
    {
        double G(double v, double lo, double hi)
        {
            if (rng.NextDouble() > rate) return v;
            double range = hi - lo;
            double delta = rng.NextGaussian() * range * 0.15;
            return Math.Clamp(v + delta, lo, hi);
        }

        var child = new RegimeRouterGenotype
        {
            BullMinBars              = G(BullMinBars,              20,  100),
            BullMinConf              = G(BullMinConf,             0.10, 0.80),
            BearMinBars              = G(BearMinBars,              20,  100),
            BearMinConf              = G(BearMinConf,             0.10, 0.80),
            FadeShortBearMinBars     = G(FadeShortBearMinBars,     20,  100),
            FadeShortBearMinConf     = G(FadeShortBearMinConf,    0.10, 0.80),
            FadeShortBearOnly        = G(FadeShortBearOnly,       0.00, 1.00),
            RipShortBearMinBars      = G(RipShortBearMinBars,      20,  100),
            RipShortBearMinConf      = G(RipShortBearMinConf,     0.10, 0.80),
            GridMaxConf              = G(GridMaxConf,             0.10, 0.70),
            EthBlendWeight           = G(EthBlendWeight,          0.00, 0.50),
            TransitionSizeMult       = G(TransitionSizeMult,      0.00, 1.00),
            EarlyBullFromBearMult    = G(EarlyBullFromBearMult,   0.00, 1.00),
            EarlyBullFromRangingMult = G(EarlyBullFromRangingMult,0.00, 1.00),
            EarlyBullBearCarry       = G(EarlyBullBearCarry,      0.00, 1.00),
        };

        if (IsHmmGenotype)
        {
            var fav = new double[HmmFavorabilityLen];
            for (int i = 0; i < HmmFavorabilityLen; i++)
                fav[i] = G(Favorability![i], -1.0, 1.0);
            var bias = new double[HmmStrategyRows];
            for (int i = 0; i < HmmStrategyRows; i++)
                bias[i] = G(Biases![i], -1.0, 1.0);
            child.Favorability = fav;
            child.Biases = bias;
            child.HmmStatesN = G(HmmStatesN, 3.0, 6.0);
            child.SmoothingAlpha = G(SmoothingAlpha, 0.50, 0.95);
            child.BlendFactor = G(BlendFactor, 0.60, 0.90);
            child.StrategyFloorPct = G(StrategyFloorPct, 0.10, 0.20);
            child.ActivateThreshold = G(ActivateThreshold, 0.55, 0.70);
            child.DeactivateThreshold = G(DeactivateThreshold, 0.35, 0.50);
            child.MinHoldBars = G(MinHoldBars, 12, 144);
        }

        return child;
    }

    public static RegimeRouterGenotype Crossover(RegimeRouterGenotype a, RegimeRouterGenotype b, System.Random rng)
    {
        var child = new RegimeRouterGenotype
        {
            BullMinBars              = rng.NextDouble() < 0.5 ? a.BullMinBars              : b.BullMinBars,
            BullMinConf              = rng.NextDouble() < 0.5 ? a.BullMinConf              : b.BullMinConf,
            BearMinBars              = rng.NextDouble() < 0.5 ? a.BearMinBars              : b.BearMinBars,
            BearMinConf              = rng.NextDouble() < 0.5 ? a.BearMinConf              : b.BearMinConf,
            FadeShortBearMinBars     = rng.NextDouble() < 0.5 ? a.FadeShortBearMinBars     : b.FadeShortBearMinBars,
            FadeShortBearMinConf     = rng.NextDouble() < 0.5 ? a.FadeShortBearMinConf     : b.FadeShortBearMinConf,
            FadeShortBearOnly        = rng.NextDouble() < 0.5 ? a.FadeShortBearOnly        : b.FadeShortBearOnly,
            RipShortBearMinBars      = rng.NextDouble() < 0.5 ? a.RipShortBearMinBars      : b.RipShortBearMinBars,
            RipShortBearMinConf      = rng.NextDouble() < 0.5 ? a.RipShortBearMinConf      : b.RipShortBearMinConf,
            GridMaxConf              = rng.NextDouble() < 0.5 ? a.GridMaxConf              : b.GridMaxConf,
            EthBlendWeight           = rng.NextDouble() < 0.5 ? a.EthBlendWeight           : b.EthBlendWeight,
            TransitionSizeMult       = rng.NextDouble() < 0.5 ? a.TransitionSizeMult       : b.TransitionSizeMult,
            EarlyBullFromBearMult    = rng.NextDouble() < 0.5 ? a.EarlyBullFromBearMult    : b.EarlyBullFromBearMult,
            EarlyBullFromRangingMult = rng.NextDouble() < 0.5 ? a.EarlyBullFromRangingMult : b.EarlyBullFromRangingMult,
            EarlyBullBearCarry       = rng.NextDouble() < 0.5 ? a.EarlyBullBearCarry       : b.EarlyBullBearCarry,
        };

        if (a.IsHmmGenotype && b.IsHmmGenotype)
        {
            var fav = new double[HmmFavorabilityLen];
            for (int i = 0; i < HmmFavorabilityLen; i++)
                fav[i] = rng.NextDouble() < 0.5 ? a.Favorability![i] : b.Favorability![i];
            var bias = new double[HmmStrategyRows];
            for (int i = 0; i < HmmStrategyRows; i++)
                bias[i] = rng.NextDouble() < 0.5 ? a.Biases![i] : b.Biases![i];
            child.Favorability = fav;
            child.Biases = bias;
            child.HmmStatesN = rng.NextDouble() < 0.5 ? a.HmmStatesN : b.HmmStatesN;
            child.SmoothingAlpha = rng.NextDouble() < 0.5 ? a.SmoothingAlpha : b.SmoothingAlpha;
            child.BlendFactor = rng.NextDouble() < 0.5 ? a.BlendFactor : b.BlendFactor;
            child.StrategyFloorPct = rng.NextDouble() < 0.5 ? a.StrategyFloorPct : b.StrategyFloorPct;
            child.ActivateThreshold = rng.NextDouble() < 0.5 ? a.ActivateThreshold : b.ActivateThreshold;
            child.DeactivateThreshold = rng.NextDouble() < 0.5 ? a.DeactivateThreshold : b.DeactivateThreshold;
            child.MinHoldBars = rng.NextDouble() < 0.5 ? a.MinHoldBars : b.MinHoldBars;
        }
        else if (a.IsHmmGenotype)
        {
            child.Favorability = (double[])a.Favorability!.Clone();
            child.Biases = (double[])a.Biases!.Clone();
            child.HmmStatesN = a.HmmStatesN;
            child.SmoothingAlpha = a.SmoothingAlpha;
            child.BlendFactor = a.BlendFactor;
            child.StrategyFloorPct = a.StrategyFloorPct;
            child.ActivateThreshold = a.ActivateThreshold;
            child.DeactivateThreshold = a.DeactivateThreshold;
            child.MinHoldBars = a.MinHoldBars;
        }
        else if (b.IsHmmGenotype)
        {
            child.Favorability = (double[])b.Favorability!.Clone();
            child.Biases = (double[])b.Biases!.Clone();
            child.HmmStatesN = b.HmmStatesN;
            child.SmoothingAlpha = b.SmoothingAlpha;
            child.BlendFactor = b.BlendFactor;
            child.StrategyFloorPct = b.StrategyFloorPct;
            child.ActivateThreshold = b.ActivateThreshold;
            child.DeactivateThreshold = b.DeactivateThreshold;
            child.MinHoldBars = b.MinHoldBars;
        }

        return child;
    }

    public override string ToString()
    {
        var s = $"Bull≥{BullMinBars:F0}bars/conf{BullMinConf:F2}  Bear≥{BearMinBars:F0}bars/conf{BearMinConf:F2}  " +
            $"RipBear≥{RipShortBearMinBars:F0}bars/conf{RipShortBearMinConf:F2}  " +
            $"FsBear{(FadeShortBearOnly > 0 ? $"≥{FadeShortBearMinBars:F0}b/c{FadeShortBearMinConf:F2}" : "OFF")}  " +
            $"GridIfConf<{GridMaxConf:F2}  EthW={EthBlendWeight:F2}  TransMult={TransitionSizeMult:F2}  " +
            $"EBear={EarlyBullFromBearMult:F2}  ERng={EarlyBullFromRangingMult:F2}  BCarry={EarlyBullBearCarry:F2}  ";
        if (IsHmmGenotype)
            s += $"HMM(statesN={HmmStatesN:F1} α={SmoothingAlpha:F2} blend={BlendFactor:F2} floor={StrategyFloorPct:F2} " +
                 $"act={ActivateThreshold:F2} deact={DeactivateThreshold:F2} hold={MinHoldBars:F0}) ";
        s += $"F={Fitness:F4}";
        return s;
    }
}



public record RegimeRouterGenotypeDto(
    double BullMinBars,
    double BullMinConf,
    double BearMinBars,
    double BearMinConf,
    double GridMaxConf,
    double EthBlendWeight,
    double Fitness,
    double TransitionSizeMult       = 0.0,
    double EarlyBullFromBearMult    = 0.0,
    double EarlyBullFromRangingMult = 0.0,
    double EarlyBullBearCarry       = 0.0,
    double FadeShortBearMinBars = 60.0,
    double FadeShortBearMinConf = 0.30,
    double FadeShortBearOnly    = 0.0,
    double RipShortBearMinBars = 143.0,
    double RipShortBearMinConf = 0.74,
    double[]? Favorability = null,
    double[]? Biases = null,
    int HmmStatesN = 4,
    double SmoothingAlpha = 0.75,
    double BlendFactor = 0.75,
    double StrategyFloorPct = 0.10,
    double ActivateThreshold = 0.62,
    double DeactivateThreshold = 0.42,
    double MinHoldBars = 24)
{
    public RegimeRouterGenotype ToGenotype()
    {
        var g = new RegimeRouterGenotype
        {
            BullMinBars              = BullMinBars,
            BullMinConf              = BullMinConf,
            BearMinBars              = BearMinBars,
            BearMinConf              = BearMinConf,
            RipShortBearMinBars      = RipShortBearMinBars,
            RipShortBearMinConf      = RipShortBearMinConf,
            FadeShortBearMinBars     = FadeShortBearMinBars,
            FadeShortBearMinConf     = FadeShortBearMinConf,
            FadeShortBearOnly        = FadeShortBearOnly,
            GridMaxConf              = GridMaxConf,
            EthBlendWeight           = EthBlendWeight,
            Fitness                  = Fitness,
            TransitionSizeMult       = TransitionSizeMult,
            EarlyBullFromBearMult    = EarlyBullFromBearMult,
            EarlyBullFromRangingMult = EarlyBullFromRangingMult,
            EarlyBullBearCarry       = EarlyBullBearCarry,
            SmoothingAlpha           = SmoothingAlpha,
            BlendFactor              = BlendFactor,
            StrategyFloorPct         = StrategyFloorPct,
            ActivateThreshold        = ActivateThreshold,
            DeactivateThreshold      = DeactivateThreshold,
            MinHoldBars              = MinHoldBars,
        };
        if (Favorability is { Length: RegimeRouterGenotype.HmmFavorabilityLen }
            && Biases is { Length: RegimeRouterGenotype.HmmStrategyRows })
        {
            g.Favorability = Favorability;
            g.Biases = Biases;
            g.HmmStatesN = HmmStatesN;
        }
        return g;
    }

    public static RegimeRouterGenotypeDto From(RegimeRouterGenotype g) =>
        new(g.BullMinBars, g.BullMinConf, g.BearMinBars, g.BearMinConf,
            g.GridMaxConf, g.EthBlendWeight, g.Fitness,
            g.TransitionSizeMult, g.EarlyBullFromBearMult,
            g.EarlyBullFromRangingMult, g.EarlyBullBearCarry,
            g.FadeShortBearMinBars, g.FadeShortBearMinConf, g.FadeShortBearOnly,
            g.RipShortBearMinBars, g.RipShortBearMinConf,
            g.Favorability, g.Biases, (int)Math.Round(g.HmmStatesN),
            g.SmoothingAlpha, g.BlendFactor, g.StrategyFloorPct,
            g.ActivateThreshold, g.DeactivateThreshold, g.MinHoldBars);
}
