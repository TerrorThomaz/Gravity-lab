namespace TradingGA;

// Genotype for the RegimeRouterGA.
// Ten genes that control when each strategy is activated based on BTC regime state.
//
// Interpretation:
//   BullMinBars          — DipLong/SwingLong only activates after BTC has been in Bull for ≥N h1 bars
//   BullMinConf          — and the blended BTC+ETH confidence ≥ this value
//   BearMinBars          — FadeLong: same pattern for Bear regime
//   BearMinConf
//   GridMaxConf          — Grid fires when blended confidence < GridMaxConf (ambiguous market)
//   EthBlendWeight       — how much ETH agreement/disagreement adjusts BTC confidence
//   TransitionSizeMult   — during the early-regime window (duration < MinBars), Grid is forced
//                          ON at this multiplier; directional longs blocked unless early-bull genes fire
//   EarlyBullFromBearMult    — in early-bull window, DipLong/SwingLong fire at this fraction
//                              ONLY when previous regime was Bear (genuine reversal)
//   EarlyBullFromRangingMult — same for Ranging→Bull transition (range breakout)
//   EarlyBullBearCarry       — FadeLong stays at this fraction in early-bull when came from Bear
//                              (bearish carry-over: dead-cat fades before the bull confirms)
public class RegimeRouterGenotype
{
    public double BullMinBars             { get; set; }  // [50, 500]
    public double BullMinConf             { get; set; }  // [0.10, 0.80]
    public double BearMinBars             { get; set; }  // [50, 500]
    public double BearMinConf             { get; set; }  // [0.10, 0.80]
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
        {  50, 500 },   // BearMinBars
        { 0.10, 0.80 }, // BearMinConf
        { 0.10, 0.70 }, // GridMaxConf
        { 0.00, 0.50 }, // EthBlendWeight
        { 0.00, 1.00 }, // TransitionSizeMult
        { 0.00, 1.00 }, // EarlyBullFromBearMult
        { 0.00, 1.00 }, // EarlyBullFromRangingMult
        { 0.00, 1.00 }, // EarlyBullBearCarry
    };

    // ── BO / vector interface ─────────────────────────────────────────────────
    public double[] ToVector() =>
    [
        BullMinBars, BullMinConf, BearMinBars, BearMinConf, GridMaxConf,
        EthBlendWeight, TransitionSizeMult,
        EarlyBullFromBearMult, EarlyBullFromRangingMult, EarlyBullBearCarry,
    ];

    public static RegimeRouterGenotype FromVector(double[] v) => new()
    {
        BullMinBars              = Math.Clamp(v[0],  50, 500),
        BullMinConf              = Math.Clamp(v[1], 0.10, 0.80),
        BearMinBars              = Math.Clamp(v[2],  50, 500),
        BearMinConf              = Math.Clamp(v[3], 0.10, 0.80),
        GridMaxConf              = Math.Clamp(v[4], 0.10, 0.70),
        EthBlendWeight           = Math.Clamp(v[5], 0.00, 0.50),
        TransitionSizeMult       = Math.Clamp(v[6], 0.00, 1.00),
        EarlyBullFromBearMult    = Math.Clamp(v[7], 0.00, 1.00),
        EarlyBullFromRangingMult = Math.Clamp(v[8], 0.00, 1.00),
        EarlyBullBearCarry       = Math.Clamp(v[9], 0.00, 1.00),
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
            BearMinBars              = rng.NextDouble() * 450 + 50,
            BearMinConf              = rng.NextDouble() * 0.70 + 0.10,
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
            BearMinBars              = G(BearMinBars,              50,  500),
            BearMinConf              = G(BearMinConf,             0.10, 0.80),
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
            GridMaxConf              = rng.NextDouble() < 0.5 ? a.GridMaxConf              : b.GridMaxConf,
            EthBlendWeight           = rng.NextDouble() < 0.5 ? a.EthBlendWeight           : b.EthBlendWeight,
            TransitionSizeMult       = rng.NextDouble() < 0.5 ? a.TransitionSizeMult       : b.TransitionSizeMult,
            EarlyBullFromBearMult    = rng.NextDouble() < 0.5 ? a.EarlyBullFromBearMult    : b.EarlyBullFromBearMult,
            EarlyBullFromRangingMult = rng.NextDouble() < 0.5 ? a.EarlyBullFromRangingMult : b.EarlyBullFromRangingMult,
            EarlyBullBearCarry       = rng.NextDouble() < 0.5 ? a.EarlyBullBearCarry       : b.EarlyBullBearCarry,
        };

    public override string ToString() =>
        $"Bull≥{BullMinBars:F0}bars/conf{BullMinConf:F2}  Bear≥{BearMinBars:F0}bars/conf{BearMinConf:F2}  " +
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
    double EarlyBullBearCarry       = 0.0)
{
    public RegimeRouterGenotype ToGenotype() => new()
    {
        BullMinBars              = BullMinBars,
        BullMinConf              = BullMinConf,
        BearMinBars              = BearMinBars,
        BearMinConf              = BearMinConf,
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
            g.EarlyBullFromRangingMult, g.EarlyBullBearCarry);
}
