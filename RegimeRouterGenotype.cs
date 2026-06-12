namespace TradingGA;

// Genotype for the RegimeRouterGA.
// Seven genes that control when each strategy is activated based on BTC regime state.
//
// The GA learns from historical data: what BTC feature state maximised the
// combined portfolio Calmar when each strategy was active?
//
// Interpretation:
//   BullMinBars       — DipLong only activates after BTC has been in Bull for ≥N h1 bars
//   BullMinConf       — and the blended BTC+ETH confidence ≥ this value
//   BearMinBars       — FadeLong: same pattern for Bear regime
//   BearMinConf
//   GridMaxConf       — Grid is always on in Ranging; in directional regimes it also fires
//                       when blended confidence < GridMaxConf (ambiguous market = range-bound)
//   EthBlendWeight    — how much ETH agreement/disagreement adjusts BTC confidence
//   TransitionSizeMult — position-size fraction applied during the early-regime window
//                        (duration < BullMinBars or BearMinBars). 0 = hard block (old behaviour);
//                        >0 = let strategies fire at reduced size during the confirmation wait.
public class RegimeRouterGenotype
{
    public double BullMinBars        { get; set; }  // [50, 500]
    public double BullMinConf        { get; set; }  // [0.10, 0.80]
    public double BearMinBars        { get; set; }  // [50, 500]
    public double BearMinConf        { get; set; }  // [0.10, 0.80]
    public double GridMaxConf        { get; set; }  // [0.10, 0.70] — Grid fires when conf < this
    public double EthBlendWeight     { get; set; }  // [0.00, 0.50]
    public double TransitionSizeMult { get; set; }  // [0.00, 1.00]

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
    };

    // ── BO / vector interface ─────────────────────────────────────────────────
    public double[] ToVector() =>
    [
        BullMinBars, BullMinConf, BearMinBars, BearMinConf, GridMaxConf, EthBlendWeight, TransitionSizeMult,
    ];

    public static RegimeRouterGenotype FromVector(double[] v) => new()
    {
        BullMinBars        = Math.Clamp(v[0],  50, 500),
        BullMinConf        = Math.Clamp(v[1], 0.10, 0.80),
        BearMinBars        = Math.Clamp(v[2],  50, 500),
        BearMinConf        = Math.Clamp(v[3], 0.10, 0.80),
        GridMaxConf        = Math.Clamp(v[4], 0.10, 0.70),
        EthBlendWeight     = Math.Clamp(v[5], 0.00, 0.50),
        TransitionSizeMult = Math.Clamp(v[6], 0.00, 1.00),
    };

    // ── GA operators ─────────────────────────────────────────────────────────
    public static RegimeRouterGenotype Random(System.Random rng, RegimeRouterGenotype? seed = null)
    {
        if (seed != null && rng.NextDouble() < 0.3)
            return seed.Mutate(rng, 0.5);

        return new()
        {
            BullMinBars        = rng.NextDouble() * 450 + 50,
            BullMinConf        = rng.NextDouble() * 0.70 + 0.10,
            BearMinBars        = rng.NextDouble() * 450 + 50,
            BearMinConf        = rng.NextDouble() * 0.70 + 0.10,
            GridMaxConf        = rng.NextDouble() * 0.60 + 0.10,
            EthBlendWeight     = rng.NextDouble() * 0.50,
            TransitionSizeMult = rng.NextDouble(),
        };
    }

    public RegimeRouterGenotype Mutate(System.Random rng, double rate)
    {
        double G(double v, double lo, double hi)
        {
            if (rng.NextDouble() > rate) return v;
            double range  = hi - lo;
            double delta  = rng.NextGaussian() * range * 0.15;
            return Math.Clamp(v + delta, lo, hi);
        }

        return new()
        {
            BullMinBars        = G(BullMinBars,         50,  500),
            BullMinConf        = G(BullMinConf,        0.10, 0.80),
            BearMinBars        = G(BearMinBars,         50,  500),
            BearMinConf        = G(BearMinConf,        0.10, 0.80),
            GridMaxConf        = G(GridMaxConf,        0.10, 0.70),
            EthBlendWeight     = G(EthBlendWeight,     0.00, 0.50),
            TransitionSizeMult = G(TransitionSizeMult, 0.00, 1.00),
        };
    }

    public static RegimeRouterGenotype Crossover(RegimeRouterGenotype a, RegimeRouterGenotype b, System.Random rng) =>
        new()
        {
            BullMinBars        = rng.NextDouble() < 0.5 ? a.BullMinBars        : b.BullMinBars,
            BullMinConf        = rng.NextDouble() < 0.5 ? a.BullMinConf        : b.BullMinConf,
            BearMinBars        = rng.NextDouble() < 0.5 ? a.BearMinBars        : b.BearMinBars,
            BearMinConf        = rng.NextDouble() < 0.5 ? a.BearMinConf        : b.BearMinConf,
            GridMaxConf        = rng.NextDouble() < 0.5 ? a.GridMaxConf        : b.GridMaxConf,
            EthBlendWeight     = rng.NextDouble() < 0.5 ? a.EthBlendWeight     : b.EthBlendWeight,
            TransitionSizeMult = rng.NextDouble() < 0.5 ? a.TransitionSizeMult : b.TransitionSizeMult,
        };

    public override string ToString() =>
        $"Bull≥{BullMinBars:F0}bars/conf{BullMinConf:F2}  Bear≥{BearMinBars:F0}bars/conf{BearMinConf:F2}  " +
        $"GridIfConf<{GridMaxConf:F2}  EthW={EthBlendWeight:F2}  TransMult={TransitionSizeMult:F2}  F={Fitness:F4}";
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
    double TransitionSizeMult = 0.0)  // default 0 = backward-compatible (hard block, old behaviour)
{
    public RegimeRouterGenotype ToGenotype() => new()
    {
        BullMinBars        = BullMinBars,
        BullMinConf        = BullMinConf,
        BearMinBars        = BearMinBars,
        BearMinConf        = BearMinConf,
        GridMaxConf        = GridMaxConf,
        EthBlendWeight     = EthBlendWeight,
        Fitness            = Fitness,
        TransitionSizeMult = TransitionSizeMult,
    };

    public static RegimeRouterGenotypeDto From(RegimeRouterGenotype g) =>
        new(g.BullMinBars, g.BullMinConf, g.BearMinBars, g.BearMinConf,
            g.GridMaxConf, g.EthBlendWeight, g.Fitness, g.TransitionSizeMult);
}
