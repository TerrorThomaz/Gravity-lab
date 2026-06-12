namespace TradingGA;

public record DynamicGuardGenotype(
    double AtrLookback,   // 4h-bars for rolling avg ATR reference  (6–96)
    double AtrTrigger,    // ATR expansion ratio to start reducing   (1.0–3.5)
    double MomLookback,   // 4h-bars for momentum window             (2–48)
    double MomThreshold,  // negative return threshold               (-0.15–-0.01)
    double SizeFloor,     // minimum multiplier at max stress        (0.10–0.80)
    double Fitness = 0)
{
    public static readonly double[,] Bounds =
    {
        {  6,   96 },
        {  1.0,  3.5 },
        {  2,   48 },
        { -0.15, -0.01 },
        {  0.10,  0.80 },
    };

    public double[] ToGenes() => [AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor];

    public static DynamicGuardGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], fitness);

    // Returns multiplier in [SizeFloor, 1.0].
    // atrRatio = current4hATR / rollingAvgATR; momentum = (close_t - close_{t-N}) / close_{t-N}
    public double ComputeMult(double atrRatio, double momentum)
    {
        double atrStress = Math.Clamp((atrRatio - AtrTrigger) / Math.Max(AtrTrigger, 0.1), 0, 1);
        double momStress = Math.Clamp((-momentum - Math.Abs(MomThreshold)) / Math.Max(Math.Abs(MomThreshold), 1e-9), 0, 1);
        double stress    = Math.Max(atrStress, momStress);
        return SizeFloor + (1.0 - SizeFloor) * (1.0 - stress);
    }

    public override string ToString() =>
        $"ATR(lk={(int)AtrLookback}h4 trig={AtrTrigger:F2}) " +
        $"Mom(lk={(int)MomLookback}h4 thr={MomThreshold:P0}) " +
        $"Floor={SizeFloor:F2}  F={Fitness:F4}";
}

public record DynamicGuardGenotypeDto(
    double AtrLookback, double AtrTrigger,
    double MomLookback, double MomThreshold,
    double SizeFloor,   double Fitness)
{
    public DynamicGuardGenotype ToGenotype() =>
        new(AtrLookback, AtrTrigger, MomLookback, MomThreshold, SizeFloor, Fitness);
}
