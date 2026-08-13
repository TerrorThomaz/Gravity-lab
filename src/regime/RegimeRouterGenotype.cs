namespace TradingGA;

// Genotype for the RegimeRouterGA.
// Twelve genes that control when each strategy is activated based on BTC regime state.
//
// Interpretation:
//   BullMinBars          — DipLong/SwingLong only activates after BTC has been in Bull for ≥N h1 bars
//   BullMinConf          — and the blended BTC+ETH confidence ≥ this value
//   BearMinBars          — FadeLong: same pattern for Bear regime
//   BearMinConf
//   RipShortBearMinBars  — RipShort's own confirmed-bear gate (kept separate from FadeLong's
//   RipShortBearMinConf    since sharing one gene pair let a disabled FadeLong drag the threshold)
//   GridMaxConf          — Grid fires when blended confidence < GridMaxConf (ambiguous market)
//   EthBlendWeight       — how much ETH agreement/disagreement adjusts BTC confidence
//
// The four "Mult"/"Carry" genes below are ON/OFF switches at routing time — the router asks only
// `gene > 0`, never multiplies by the value. Their magnitude is read in exactly one place:
// RegimeRouterGA.FilterActive scales the trade's capital fraction by it while scoring candidate
// routers, so the value still shapes the GA's own fitness landscape. No live or backtest path
// sizes on it. (StrategyActivation.SizeMult, which did multiply by them, was deleted as unused.)
//   TransitionSizeMult   — > 0 forces Grid/GridShort ON during the early-regime window
//                          (duration < MinBars), Bull or Bear
//   EarlyBullFromBearMult    — > 0 lets DipLong/SwingLong fire in the early-bull window
//                              ONLY when previous regime was Bear (genuine reversal)
//   EarlyBullFromRangingMult — same for Ranging→Bull transition (range breakout)
//   EarlyBullBearCarry       — > 0 keeps FadeLong on in early-bull when it came from Bear
//                              (bearish carry-over: dead-cat fades before the bull confirms)
public class RegimeRouterGenotype
{
    public double BullMinBars             { get; set; }  // [50, 500]
    public double BullMinConf             { get; set; }  // [0.10, 0.80]
    public double BearMinBars             { get; set; }  // [24, 200]  — FadeLong's confirmed-bear gate
    public double BearMinConf             { get; set; }  // [0.10, 0.80]
    public double RipShortBearMinBars     { get; set; }  // [50, 500]  — RipShort's own confirmed-bear gate
    public double RipShortBearMinConf     { get; set; }  // [0.10, 0.80]

    // FadeShort's own confirmed-bear gate. Separate from RipShort's and FadeLong's for the same
    // reason those two were split: one shared threshold gets dragged to a compromise that suits
    // none of them.
    //
    // FadeShort's edge is REGIME-SPLIT, not weak. Measured on the time-embargoed held-out window,
    // six retrains running (widened bounds, router gate, FreqW=0, WrW x2, RegimeSustain, and the
    // corrected three-gene regime):
    //     Bear  PF 5.28  WR 71%  45 trades  +2.28%/trade
    //     Bull  PF 0.38  WR 13%  84 trades  -0.86%/trade
    // The blended PF near 1.2 is those two cancelling. Bull carries ~65% of the trades and
    // subtracts 72 points from a 102-point gross.
    //
    // The old gate was `NOT (Bull AND confident)`, i.e. FadeShort ran in Bear, Ranging, HighVol
    // AND unconfident Bull — which is where most of the losses are, since "unconfident Bull" is
    // exactly the ambiguous tape a fade gets run over in. FadeShortBearOnly (a >0 ON/OFF switch,
    // like the other transition genes) flips it to "only in confirmed Bear", with the bar/conf
    // thresholds TRAINED rather than assumed: how strict "confirmed" should be is precisely what
    // routertrain is for.
    public double FadeShortBearMinBars    { get; set; }  // [20, 400]
    public double FadeShortBearMinConf    { get; set; }  // [0.10, 0.80]
    public double FadeShortBearOnly       { get; set; }  // >0 = Bear-only; 0 = legacy not-confirmed-Bull
    public double GridMaxConf             { get; set; }  // [0.10, 0.70]
    public double EthBlendWeight          { get; set; }  // [0.00, 0.50]
    public double TransitionSizeMult      { get; set; }  // [0.00, 1.00]
    public double EarlyBullFromBearMult   { get; set; }  // [0.00, 1.00]
    public double EarlyBullFromRangingMult{ get; set; }  // [0.00, 1.00]
    public double EarlyBullBearCarry      { get; set; }  // [0.00, 1.00]

    public double Fitness { get; set; }

    // ── Search space ─────────────────────────────────────────────────────────
    public static readonly double[,] Bounds =
    {
        {  50, 500 },   // BullMinBars
        { 0.10, 0.80 }, // BullMinConf
        {  24, 200 },   // BearMinBars
        { 0.10, 0.80 }, // BearMinConf
        {  50, 500 },   // RipShortBearMinBars
        { 0.10, 0.80 }, // RipShortBearMinConf
        { 0.10, 0.70 }, // GridMaxConf
        { 0.00, 0.50 }, // EthBlendWeight
        { 0.00, 1.00 }, // TransitionSizeMult
        { 0.00, 1.00 }, // EarlyBullFromBearMult
        { 0.00, 1.00 }, // EarlyBullFromRangingMult
        { 0.00, 1.00 }, // EarlyBullBearCarry
        {  20, 400 },   // FadeShortBearMinBars
        { 0.10, 0.80 }, // FadeShortBearMinConf
        { 0.00, 1.00 }, // FadeShortBearOnly — >0 = ON/OFF switch, like the transition genes
    };

    // ── BO / vector interface ─────────────────────────────────────────────────
    public double[] ToVector() =>
    [
        BullMinBars, BullMinConf, BearMinBars, BearMinConf,
        RipShortBearMinBars, RipShortBearMinConf, GridMaxConf,
        EthBlendWeight, TransitionSizeMult,
        EarlyBullFromBearMult, EarlyBullFromRangingMult, EarlyBullBearCarry,
        FadeShortBearMinBars, FadeShortBearMinConf, FadeShortBearOnly,
    ];

    public static RegimeRouterGenotype FromVector(double[] v) => new()
    {
        BullMinBars              = Math.Clamp(v[0],  50, 500),
        BullMinConf              = Math.Clamp(v[1], 0.10, 0.80),
        BearMinBars              = Math.Clamp(v[2],  24, 200),
        BearMinConf              = Math.Clamp(v[3], 0.10, 0.80),
        RipShortBearMinBars      = Math.Clamp(v[4],  50, 500),
        RipShortBearMinConf      = Math.Clamp(v[5], 0.10, 0.80),
        GridMaxConf              = Math.Clamp(v[6], 0.10, 0.70),
        EthBlendWeight           = Math.Clamp(v[7], 0.00, 0.50),
        TransitionSizeMult       = Math.Clamp(v[8], 0.00, 1.00),
        EarlyBullFromBearMult    = Math.Clamp(v[9], 0.00, 1.00),
        EarlyBullFromRangingMult = Math.Clamp(v[10], 0.00, 1.00),
        FadeShortBearMinBars     = Math.Clamp(v[12],  20, 400),
        FadeShortBearMinConf     = Math.Clamp(v[13], 0.10, 0.80),
        FadeShortBearOnly        = Math.Clamp(v[14], 0.00, 1.00),
        EarlyBullBearCarry       = Math.Clamp(v[11], 0.00, 1.00),
    };

    // ── GA operators ─────────────────────────────────────────────────────────
    public static RegimeRouterGenotype Random(System.Random rng, RegimeRouterGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < 0.3)
            return seed.Mutate(rng, 0.5);

        return new()
        {
            BullMinBars              = rng.NextDouble() * 450 + 50,
            BullMinConf              = rng.NextDouble() * 0.70 + 0.10,
            BearMinBars              = rng.NextDouble() * 176 + 24,
            BearMinConf              = rng.NextDouble() * 0.70 + 0.10,
            FadeShortBearMinBars     = rng.NextDouble() * 380 + 20,
            FadeShortBearMinConf     = rng.NextDouble() * 0.70 + 0.10,
            FadeShortBearOnly        = rng.NextDouble(),
            RipShortBearMinBars      = rng.NextDouble() * 450 + 50,
            RipShortBearMinConf      = rng.NextDouble() * 0.70 + 0.10,
            GridMaxConf              = rng.NextDouble() * 0.60 + 0.10,
            EthBlendWeight           = rng.NextDouble() * 0.50,
            TransitionSizeMult       = rng.NextDouble(),
            EarlyBullFromBearMult    = rng.NextDouble(),
            EarlyBullFromRangingMult = rng.NextDouble(),
            EarlyBullBearCarry       = rng.NextDouble(),
        };
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

        return new()
        {
            BullMinBars              = G(BullMinBars,              50,  500),
            BullMinConf              = G(BullMinConf,             0.10, 0.80),
            BearMinBars              = G(BearMinBars,              24,  200),
            BearMinConf              = G(BearMinConf,             0.10, 0.80),
            FadeShortBearMinBars     = G(FadeShortBearMinBars,     20,  400),
            FadeShortBearMinConf     = G(FadeShortBearMinConf,    0.10, 0.80),
            FadeShortBearOnly        = G(FadeShortBearOnly,       0.00, 1.00),
            RipShortBearMinBars      = G(RipShortBearMinBars,      50,  500),
            RipShortBearMinConf      = G(RipShortBearMinConf,     0.10, 0.80),
            GridMaxConf              = G(GridMaxConf,             0.10, 0.70),
            EthBlendWeight           = G(EthBlendWeight,          0.00, 0.50),
            TransitionSizeMult       = G(TransitionSizeMult,      0.00, 1.00),
            EarlyBullFromBearMult    = G(EarlyBullFromBearMult,   0.00, 1.00),
            EarlyBullFromRangingMult = G(EarlyBullFromRangingMult,0.00, 1.00),
            EarlyBullBearCarry       = G(EarlyBullBearCarry,      0.00, 1.00),
        };
    }

    public static RegimeRouterGenotype Crossover(RegimeRouterGenotype a, RegimeRouterGenotype b, System.Random rng) =>
        new()
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

    public override string ToString() =>
        $"Bull≥{BullMinBars:F0}bars/conf{BullMinConf:F2}  Bear≥{BearMinBars:F0}bars/conf{BearMinConf:F2}  " +
        $"RipBear≥{RipShortBearMinBars:F0}bars/conf{RipShortBearMinConf:F2}  " +
        $"FsBear{(FadeShortBearOnly > 0 ? $"≥{FadeShortBearMinBars:F0}b/c{FadeShortBearMinConf:F2}" : "OFF")}  " +
        $"GridIfConf<{GridMaxConf:F2}  EthW={EthBlendWeight:F2}  TransMult={TransitionSizeMult:F2}  " +
        $"EBear={EarlyBullFromBearMult:F2}  ERng={EarlyBullFromRangingMult:F2}  BCarry={EarlyBullBearCarry:F2}  " +
        $"F={Fitness:F4}";
}

// ── JSON DTO ─────────────────────────────────────────────────────────────────

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
    // FadeShortBearOnly defaults to 0 = the legacy not-confirmed-Bull gate, so a router genotype
    // saved before these genes existed deserializes to bit-identical behaviour.
    double FadeShortBearMinBars = 60.0,
    double FadeShortBearMinConf = 0.30,
    double FadeShortBearOnly    = 0.0,
    // Defaults match the old shared BearMinBars/BearMinConf so genotype files saved
    // before RipShort got its own gate keep their exact prior behavior on load.
    double RipShortBearMinBars = 143.0,
    double RipShortBearMinConf = 0.74)
{
    // NOTE: EarlyBearFromBullMult / EarlyBearFromRangingMult were removed along with
    // StrategyActivation.SizeMult — they only ever scaled that (never-consumed) multiplier
    // and never gated activation. Genotype JSON written before the removal still carries
    // those two keys; System.Text.Json ignores unmapped members by default, so such files
    // continue to deserialize unchanged. Do not re-add them without a consumer.
    public RegimeRouterGenotype ToGenotype() => new()
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
    };

    public static RegimeRouterGenotypeDto From(RegimeRouterGenotype g) =>
        new(g.BullMinBars, g.BullMinConf, g.BearMinBars, g.BearMinConf,
            g.GridMaxConf, g.EthBlendWeight, g.Fitness,
            g.TransitionSizeMult, g.EarlyBullFromBearMult,
            g.EarlyBullFromRangingMult, g.EarlyBullBearCarry,
            g.FadeShortBearMinBars, g.FadeShortBearMinConf, g.FadeShortBearOnly,
            g.RipShortBearMinBars, g.RipShortBearMinConf);
}
