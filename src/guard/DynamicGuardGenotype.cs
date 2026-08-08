namespace TradingGA;

public record DynamicGuardGenotype(
    double AtrLookback,    // 4h-bars for rolling avg ATR reference  (6–96)
    double AtrTrigger,     // ATR expansion ratio to start reducing   (1.0–3.5)
    double MomLookback,    // 4h-bars for momentum window             (2–48)
    double MomThreshold,   // negative return threshold               (-0.15–-0.01)
    double SizeFloor,      // minimum multiplier at max stress        (0.0–0.80)
    double PanicTrigger,   // ATR ratio → full halt (mult=0)          (1.5–5.0)
    double RecoveryBars,   // 4H bars to hold at floor after trigger  (0–48)
    double BullMomBypass,  // 8-day 4H momentum > this → no guard for DipLong/SwingLong (0.0–0.30)
    double EntryAtrGate,    // ATR ratio above which DipLong/SwingLong entries are blocked (1.5–5.0)
                            // NOT bypassed by BullMomBypass — high ATR = wide stops regardless of trend
    double DdEntryGatePct,  // EFFECTIVE portfolio peak-to-trough DD FRACTION above which
                            // DipLong/SwingLong entries are blocked. Invariant (enforced by the
                            // property below, so it holds however the record was constructed):
                            // this is either a live threshold in [0.02, 0.15] or exactly
                            // DdGateDisabled (1.0 = 100% DD ⇒ the gate can never fire).
                            // Consumers pass this straight to Simulator's ddLongEntryGatePct.
                            // The on/off decision lives in a separate SEARCH gene (index 15,
                            // see DdGateEnableGeneIndex) that is folded into this one field —
                            // see ToGenes/FromGenes.
    double ConfLossCapMin,  // max loss cap for long trades at conf=0 (0.03–0.15)
    double ConfLossCapMax,  // max loss cap for long trades at conf=1 (0.10–0.40)
                            // effective cap = ConfLossCapMin + (ConfLossCapMax - ConfLossCapMin) * conf
                            // low-confidence entries get tight stops, high-confidence entries get room
    double ProfitProtectThreshold, // portfolio gain fraction that arms protection mode (0.05–0.50); 1.0 = disabled
    double ProfitProtectDrawback,  // drawback from peak that triggers protection (0.01–0.20)
    double ProfitProtectFactor,    // position size multiplier when in protection mode (0.20–1.00); 1.0 = no reduction
    double Fitness = 0)
{
    // "Never fires" value for DdEntryGatePct. The simulator compares a drawdown FRACTION
    // (peak - balance) / peak, which cannot exceed 1.0, so 1.0 == gate permanently off.
    public const double DdGateDisabled = 1.0;

    // Index of the DD-gate THRESHOLD in Bounds / ToGenes / FromGenes.
    public const int DdGateGeneIndex = 9;

    // Index of the DD-gate ON/OFF switch in Bounds / ToGenes / FromGenes.
    //
    // Why a separate switch gene instead of widening Bounds[9] to {0.02, 1.0}:
    // the GA must be able to select "off" for a risk control, or the control is imposed
    // rather than validated. But a single continuous gene spanning [0.02, 1.0] makes the
    // live band ([0.02, 0.15]) only 13% of the range, and — worse — ties every mutation
    // step to that range. DynamicGuardGA mutates with sigma = 0.1 * range (= 0.098) and
    // BayesianOptimizer's KDE bandwidth is clamped to [3%, 20%] of range (= [0.029, 0.196]);
    // both are wider than the entire 0.13-wide live band, so neither optimiser could ever
    // tune the threshold — every perturbation of a live gene would fling it out of the band.
    // Splitting the decision keeps Bounds[9] at its natural scale (mutation sigma 0.013,
    // TPE bandwidth [0.004, 0.026]) and gives the on/off choice a clean 50/50 prior under
    // uniform initialisation, at the cost of one extra — binary — search dimension.
    public const int DdGateEnableGeneIndex = 15;

    // Gene 15 >= this ⇒ gate live at gene 9; below ⇒ gate off.
    public const double DdGateEnableThreshold = 0.5;

    // Canonical values ToGenes emits for the switch. They sit at the midpoints of the two
    // halves rather than saturating at 0.0/1.0 so the boundary stays crossable: at 0.25/0.75
    // it is 2.5 mutation sigmas away for the GA and inside one TPE bandwidth. Saturated
    // 0/1 values would make the switch effectively unflippable after initialisation.
    public const double DdGateEnabledGene  = 0.75;
    public const double DdGateDisabledGene = 0.25;

    // NUMBER OF SEARCH GENES — deliberately one MORE than the record's 15 value fields:
    // the DD gate is a single effective field (the threshold) driven by TWO search genes
    // (threshold at index 9 + on/off switch at index 15), folded back together by FromGenes.
    // Everything that walks the search space (DynamicGuardGA.RandomGenes / MutateGene,
    // BayesianOptimizer.Refine) sizes itself off Bounds.GetLength(0), so Bounds, ToGenes and
    // this constant must agree exactly — the static constructor below enforces that, and
    // DynamicGuardTests pins it from the outside.
    public const int GeneCount = 16;

    static DynamicGuardGenotype()
    {
        // Static assertion: a Bounds row count that drifts away from ToGenes' arity would make
        // the GA mutate genes that do not exist, or leave the last gene(s) frozen at their
        // initial draw. Fail loudly at type-load rather than silently mis-optimise.
        if (Bounds.GetLength(0) != GeneCount)
            throw new InvalidOperationException(
                $"DynamicGuardGenotype gene-layout mismatch: Bounds has {Bounds.GetLength(0)} rows, " +
                $"GeneCount is {GeneCount}.");
        if (Bounds.GetLength(1) != 2)
            throw new InvalidOperationException("DynamicGuardGenotype.Bounds must be [n, 2] (min, max).");
        if (DdGateGeneIndex >= GeneCount || DdGateEnableGeneIndex >= GeneCount)
            throw new InvalidOperationException("DynamicGuardGenotype DD-gate gene indices fall outside Bounds.");
        if (Bounds[DdGateEnableGeneIndex, 0] > DdGateDisabledGene || Bounds[DdGateEnableGeneIndex, 1] < DdGateEnabledGene)
            throw new InvalidOperationException(
                "DynamicGuardGenotype switch-gene bounds cannot represent both canonical states — " +
                "the GA clamps into Bounds, so one of the two gate states would be unreachable.");
    }

    public static readonly double[,] Bounds =
    {
        {  6,    96   },   // AtrLookback
        {  1.0,   3.5 },   // AtrTrigger
        {  2,    48   },   // MomLookback
        { -0.15, -0.01},   // MomThreshold
        {  0.0,   0.80},   // SizeFloor
        {  1.5,   5.0 },   // PanicTrigger
        {  0,    48   },   // RecoveryBars
        {  0.0,   0.30},   // BullMomBypass
        {  1.5,   5.0 },   // EntryAtrGate
        {  0.02,  0.15},   // DdEntryGatePct  — fraction, matches the simulator's currentDd units
        {  0.03,  0.15},   // ConfLossCapMin
        {  0.10,  0.40},   // ConfLossCapMax
        {  0.05,  0.50},   // ProfitProtectThreshold
        {  0.01,  0.20},   // ProfitProtectDrawback
        {  0.20,  1.00},   // ProfitProtectFactor
        {  0.0,   1.00},   // DdGateEnabled  — >= 0.5 ⇒ DD gate live at gene 9, else off
    };

    // THE CLAMP POLICY, in one sentence:
    //   a DdEntryGatePct is kept verbatim only if it already lies inside the live band
    //   [Bounds[9,0], Bounds[9,1]]; EVERY other value — NaN, infinity, negative, zero,
    //   below the band, above the band, or a legacy percent-scale number — coerces the
    //   gate OFF (DdGateDisabled), and only the exact sentinel does so without being
    //   flagged as coerced.
    //
    // Note this is NOT "clamp into bounds" for any input, including 0.005 and 0.90, which sit
    // just outside the band and would once have been snapped to 0.02 / 0.15. Under the two-gene
    // design the GA never needs the snapping behaviour — it says "off" with the switch gene
    // (index 15) and only ever emits in-band thresholds at gene 9, so the ONLY callers that can
    // present an out-of-band number are deserialisation of a stale/hand-edited genotype file
    // and a hand-written `new`/`with`. For those, an out-of-band number is evidence of a bug or
    // a unit mix-up, not a preference about where the threshold belongs.
    //
    // Why "off" and never "clamp to the nearest edge": until 2026-08 this gene was bounded
    // {2, 15} and defaulted to 15.0, i.e. it was stored on a PERCENT scale while
    // Simulator.SimulatePortfolioExposureCapped compared it against a fraction in [0, 1].
    // Every such value demanded a >200% drawdown, so the gate was inert across its whole
    // search space. A gene the GA selected while the gate was provably inert carries no
    // evidence about where the threshold belongs, so snapping it to an edge would silently
    // switch on a never-validated long-entry blocker — at 0.02 that is close to "block all
    // DipLong/SwingLong entries permanently". Coercing off is the only choice that changes
    // no backtest number. Retrain to get a real threshold.
    public static double ClampDdEntryGate(double raw, out bool wasCoerced)
    {
        double lo = Bounds[DdGateGeneIndex, 0], hi = Bounds[DdGateGeneIndex, 1];
        if (raw >= lo && raw <= hi)          // NaN fails both comparisons and falls through
        {
            wasCoerced = false;
            return raw;
        }
        // Anything else is unusable as a threshold. The explicit sentinel is a legitimate
        // way to say "off", so it is not reported as a coercion.
        wasCoerced = raw != DdGateDisabled;
        return DdGateDisabled;
    }

    public static double ClampDdEntryGate(double raw) => ClampDdEntryGate(raw, out _);

    // Enforces the DdEntryGatePct invariant for every construction path — primary
    // constructor, object initialiser and `with` expression alike.
    private readonly double _ddEntryGatePct = ClampDdEntryGate(DdEntryGatePct);
    public double DdEntryGatePct
    {
        get => _ddEntryGatePct;
        init => _ddEntryGatePct = ClampDdEntryGate(value);
    }

    public bool DdGateIsLive => DdEntryGatePct < DdGateDisabled;

    // ToGenes/FromGenes are exact inverses on the record: the two DD-gate genes are emitted
    // in canonical form (an off individual reports the LOOSEST live threshold, so flipping
    // the switch on is the mildest possible perturbation rather than a jump to the harshest
    // 2% gate), and FromGenes folds them back into the single effective field.
    public double[] ToGenes() =>
    [
        AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars,
        BullMomBypass, EntryAtrGate,
        DdGateIsLive ? DdEntryGatePct : Bounds[DdGateGeneIndex, 1],
        ConfLossCapMin, ConfLossCapMax, ProfitProtectThreshold, ProfitProtectDrawback, ProfitProtectFactor,
        DdGateIsLive ? DdGateEnabledGene : DdGateDisabledGene,
    ];

    public static DynamicGuardGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6],
            g.Length > 7  ? g[7]  : 0.0,
            g.Length > 8  ? g[8]  : 999.0,  // default: ATR gate disabled
            DdGateFromGenes(g),             // default (short array): DD gate disabled (100% DD)
            g.Length > 10 ? g[10] : 1.0,    // default: loss cap disabled (100% cap = never fires)
            g.Length > 11 ? g[11] : 1.0,
            g.Length > 12 ? g[12] : 1.0,    // default: profit protect disabled (need 100% gain to arm)
            g.Length > 13 ? g[13] : 0.10,
            g.Length > 14 ? g[14] : 1.0,    // default: no size reduction
            fitness);

    // Folds the threshold gene and the switch gene into the single effective field.
    // A legacy 15-gene vector (no switch) is read by its threshold alone — ClampDdEntryGate
    // already turns anything unusable there into "off".
    private static double DdGateFromGenes(double[] g)
    {
        if (g.Length <= DdGateGeneIndex) return DdGateDisabled;
        bool enabled = g.Length <= DdGateEnableGeneIndex
                    || g[DdGateEnableGeneIndex] >= DdGateEnableThreshold;
        return enabled ? ClampDdEntryGate(g[DdGateGeneIndex]) : DdGateDisabled;
    }

    // Returns multiplier in [SizeFloor, 1.0] based on ATR and momentum stress.
    // Panic and recovery are applied at the session level (DynamicGuardSession).
    public double ComputeMult(double atrRatio, double momentum)
    {
        double atrStress = Math.Clamp((atrRatio - AtrTrigger) / Math.Max(AtrTrigger, 0.1), 0, 1);
        double momStress = Math.Clamp((-momentum - Math.Abs(MomThreshold)) / Math.Max(Math.Abs(MomThreshold), 1e-9), 0, 1);
        double stress    = Math.Max(atrStress, momStress);
        return SizeFloor + (1.0 - SizeFloor) * (1.0 - stress);
    }

    public override string ToString() =>
        $"ATR(lk={(int)AtrLookback}h4 trig={AtrTrigger:F2} panic={PanicTrigger:F2} atrGate={EntryAtrGate:F2}) " +
        $"Mom(lk={(int)MomLookback}h4 thr={MomThreshold:P0}) " +
        $"Floor={SizeFloor:F2} Rec={(int)RecoveryBars}bars  BullBypass={BullMomBypass:F3}  " +
        $"DdGate={(DdEntryGatePct >= DdGateDisabled ? "off" : DdEntryGatePct.ToString("P1"))}  " +
        $"ConfCap=[{ConfLossCapMin:P0},{ConfLossCapMax:P0}]  " +
        $"ProfProt(thr={ProfitProtectThreshold:P0} dd={ProfitProtectDrawback:P0} f={ProfitProtectFactor:F2})  F={Fitness:F4}";
}

public record DynamicGuardGenotypeDto(
    double AtrLookback, double AtrTrigger,
    double MomLookback, double MomThreshold,
    double SizeFloor,   double Fitness,
    double PanicTrigger            = 999.0,  // backward-compat default: panic disabled
    double RecoveryBars            = 0.0,
    double BullMomBypass           = 0.0,
    double EntryAtrGate            = 999.0,  // backward-compat default: ATR gate disabled
    double DdEntryGatePct          = DynamicGuardGenotype.DdGateDisabled,  // backward-compat default: DD gate disabled
    double ConfLossCapMin          = 1.0,    // backward-compat default: cap disabled
    double ConfLossCapMax          = 1.0,
    double ProfitProtectThreshold  = 1.0,    // backward-compat default: disabled (need 100% gain)
    double ProfitProtectDrawback   = 0.10,
    double ProfitProtectFactor     = 1.0)    // backward-compat default: no size reduction
{
    public DynamicGuardGenotype ToGenotype()
    {
        double ddGate = DynamicGuardGenotype.ClampDdEntryGate(DdEntryGatePct, out bool coerced);
        if (coerced)
            Console.Error.WriteLine(
                $"  [DynamicGuard] WARNING: DdEntryGatePct={DdEntryGatePct:F4} is outside the live fraction band " +
                $"[{DynamicGuardGenotype.Bounds[DynamicGuardGenotype.DdGateGeneIndex, 0]:F2}, " +
                $"{DynamicGuardGenotype.Bounds[DynamicGuardGenotype.DdGateGeneIndex, 1]:F2}] — the gate is " +
                $"coerced OFF ({ddGate:F2}) rather than snapped to a never-validated threshold. " +
                "This genotype predates the DD-gate unit fix; RETRAIN the dynamic guard " +
                "(`dotnet run -- dynamicguardtrain`) before relying on it.");
        return new(AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars, BullMomBypass, EntryAtrGate, ddGate, ConfLossCapMin, ConfLossCapMax, ProfitProtectThreshold, ProfitProtectDrawback, ProfitProtectFactor, Fitness);
    }
}
