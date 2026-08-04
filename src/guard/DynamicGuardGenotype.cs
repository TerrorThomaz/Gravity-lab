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
    double DdEntryGatePct,  // portfolio DD fraction above which DipLong/SwingLong entries are blocked (2–15)
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
        {  2,    15   },   // DdEntryGatePct
        {  0.03,  0.15},   // ConfLossCapMin
        {  0.10,  0.40},   // ConfLossCapMax
        {  0.05,  0.50},   // ProfitProtectThreshold
        {  0.01,  0.20},   // ProfitProtectDrawback
        {  0.20,  1.00},   // ProfitProtectFactor
    };

    public double[] ToGenes() => [AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars, BullMomBypass, EntryAtrGate, DdEntryGatePct, ConfLossCapMin, ConfLossCapMax, ProfitProtectThreshold, ProfitProtectDrawback, ProfitProtectFactor];

    public static DynamicGuardGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6],
            g.Length > 7  ? g[7]  : 0.0,
            g.Length > 8  ? g[8]  : 999.0,  // default: ATR gate disabled
            g.Length > 9  ? g[9]  : 15.0,   // default: DD gate disabled
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
        $"Floor={SizeFloor:F2} Rec={(int)RecoveryBars}bars  BullBypass={BullMomBypass:F3}  DdGate={DdEntryGatePct:P0}  " +
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
    double DdEntryGatePct          = 15.0,   // backward-compat default: DD gate disabled
    double ConfLossCapMin          = 1.0,    // backward-compat default: cap disabled
    double ConfLossCapMax          = 1.0,
    double ProfitProtectThreshold  = 1.0,    // backward-compat default: disabled (need 100% gain)
    double ProfitProtectDrawback   = 0.10,
    double ProfitProtectFactor     = 1.0)    // backward-compat default: no size reduction
{
    public DynamicGuardGenotype ToGenotype() =>
        new(AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars, BullMomBypass, EntryAtrGate, DdEntryGatePct, ConfLossCapMin, ConfLossCapMax, ProfitProtectThreshold, ProfitProtectDrawback, ProfitProtectFactor, Fitness);
}
