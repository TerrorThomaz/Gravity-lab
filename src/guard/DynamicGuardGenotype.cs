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
    double DdEntryGatePct,  // portfolio peak-to-trough DD FRACTION above which DipLong/SwingLong
                            // entries are blocked. Search range 0.02–0.15 (= 2%–15% drawdown).
                            // DdGateDisabled (1.0 = 100% DD) means the gate can never fire.
                            // gate fires inside the simulator where portfolio state is live
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

    // Index of DdEntryGatePct in Bounds / ToGenes / FromGenes.
    public const int DdGateGeneIndex = 9;

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
    };

    // Coerces a persisted DdEntryGatePct into the fraction units the simulator expects.
    //
    // Until 2026-08 this gene was bounded {2, 15} and defaulted to 15.0, i.e. it was being
    // stored on a PERCENT scale while Simulator.SimulatePortfolioExposureCapped compared it
    // against a fraction in [0, 1]. Every such value demanded a >200% drawdown, so the gate
    // was inert. Anything at or above DdGateDisabled is therefore a legacy percent-scale
    // value (or the explicit "off" sentinel): it is mapped to DdGateDisabled rather than
    // rescaled, because a value the GA selected while the gate was inert carries no
    // evidence about where the gate should sit. Retrain to get a real threshold.
    public static double ClampDdEntryGate(double raw, out bool wasCoerced)
    {
        double lo = Bounds[DdGateGeneIndex, 0], hi = Bounds[DdGateGeneIndex, 1];
        if (double.IsNaN(raw) || raw >= DdGateDisabled)
        {
            wasCoerced = raw != DdGateDisabled;
            return DdGateDisabled;
        }
        double clamped = Math.Clamp(raw, lo, hi);
        wasCoerced = clamped != raw;
        return clamped;
    }

    public static double ClampDdEntryGate(double raw) => ClampDdEntryGate(raw, out _);

    public double[] ToGenes() => [AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars, BullMomBypass, EntryAtrGate, DdEntryGatePct, ConfLossCapMin, ConfLossCapMax, ProfitProtectThreshold, ProfitProtectDrawback, ProfitProtectFactor];

    public static DynamicGuardGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6],
            g.Length > 7  ? g[7]  : 0.0,
            g.Length > 8  ? g[8]  : 999.0,  // default: ATR gate disabled
            g.Length > 9  ? ClampDdEntryGate(g[9]) : DdGateDisabled,  // default: DD gate disabled (100% DD)
            g.Length > 10 ? g[10] : 1.0,    // default: loss cap disabled (100% cap = never fires)
            g.Length > 11 ? g[11] : 1.0,
            g.Length > 12 ? g[12] : 1.0,    // default: profit protect disabled (need 100% gain to arm)
            g.Length > 13 ? g[13] : 0.10,
            g.Length > 14 ? g[14] : 1.0,    // default: no size reduction
            fitness);

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
                $"  [DynamicGuard] WARNING: DdEntryGatePct={DdEntryGatePct:F4} is outside the fraction bounds " +
                $"[{DynamicGuardGenotype.Bounds[DynamicGuardGenotype.DdGateGeneIndex, 0]:F2}, " +
                $"{DynamicGuardGenotype.Bounds[DynamicGuardGenotype.DdGateGeneIndex, 1]:F2}] — coerced to " +
                $"{ddGate:F2} ({(ddGate >= DynamicGuardGenotype.DdGateDisabled ? "gate OFF" : "clamped")}). " +
                "This genotype predates the DD-gate unit fix; RETRAIN the dynamic guard " +
                "(`dotnet run -- dynamicguardtrain`) before relying on it.");
        return new(AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars, BullMomBypass, EntryAtrGate, ddGate, ConfLossCapMin, ConfLossCapMax, ProfitProtectThreshold, ProfitProtectDrawback, ProfitProtectFactor, Fitness);
    }
}
