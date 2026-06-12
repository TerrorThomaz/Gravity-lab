namespace TradingGA;

public record DynamicGuardGenotype(
    double AtrLookback,    // 4h-bars for rolling avg ATR reference  (6–96)
    double AtrTrigger,     // ATR expansion ratio to start reducing   (1.0–3.5)
    double MomLookback,    // 4h-bars for momentum window             (2–48)
    double MomThreshold,   // negative return threshold               (-0.15–-0.01)
    double SizeFloor,      // minimum multiplier at max stress        (0.0–0.80)
    double PanicTrigger,   // ATR ratio → full halt (mult=0)          (1.5–5.0)
    double RecoveryBars,   // 4H bars to hold at floor after trigger  (0–48)
    double Fitness = 0)
{
    public static readonly double[,] Bounds =
    {
        {  6,    96   },   // AtrLookback
        {  1.0,   3.5 },   // AtrTrigger
        {  2,    48   },   // MomLookback
        { -0.15, -0.01},   // MomThreshold
        {  0.0,   0.80},   // SizeFloor (was 0.10; now 0 so GA can find full-halt)
        {  1.5,   5.0 },   // PanicTrigger
        {  0,    48   },   // RecoveryBars
    };

    public double[] ToGenes() => [AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars];

    public static DynamicGuardGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6], fitness);

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
        $"ATR(lk={(int)AtrLookback}h4 trig={AtrTrigger:F2} panic={PanicTrigger:F2}) " +
        $"Mom(lk={(int)MomLookback}h4 thr={MomThreshold:P0}) " +
        $"Floor={SizeFloor:F2} Rec={(int)RecoveryBars}bars  F={Fitness:F4}";
}

public record DynamicGuardGenotypeDto(
    double AtrLookback, double AtrTrigger,
    double MomLookback, double MomThreshold,
    double SizeFloor,   double Fitness,
    double PanicTrigger = 999.0,   // backward-compat default: panic disabled
    double RecoveryBars = 0.0)
{
    public DynamicGuardGenotype ToGenotype() =>
        new(AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, PanicTrigger, RecoveryBars, Fitness);
}
