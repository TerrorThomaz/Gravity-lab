using System.Text.Json.Serialization;

namespace TradingGA;

// Portfolio-level panic manager: reduces position sizes for directional-long strategies
// (Grid, DipLong, SwingLong) when portfolio equity is in drawdown.
// FadeShort and FadeLong are always exempt — crashes are their best environment.
//
// ComputeMult(ddFrac):
//   ddFrac <= ActivationDD  →  1.0          (no reduction)
//   ddFrac >= FullDD        →  SizeFloor    (maximum reduction)
//   linear blend between the two thresholds
public record DrawdownGuardGenotype(
    double ActivationDD,    // drawdown fraction that starts reduction  (0.02–0.12)
    double FullDD,          // drawdown fraction at which SizeFloor is reached (0.05–0.25)
    double SizeFloor,       // minimum size multiplier for guarded strategies (0.10–0.80)
    double DrawdownBrakeAt, // portfolio-level brake threshold replacing hardcoded 0.15 (0.05–0.40)
    double Fitness = 0)
{
    public static readonly double[,] Bounds = {
        { 0.02, 0.12 },
        { 0.05, 0.25 },
        { 0.10, 0.80 },
        { 0.05, 0.40 },
    };

    public double[] ToGenes() => [ActivationDD, FullDD, SizeFloor, DrawdownBrakeAt];

    public static DrawdownGuardGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], Math.Max(g[0] + 0.01, g[1]), g[2], g[3], fitness);

    public double ComputeMult(double drawdownFrac)
    {
        if (drawdownFrac <= ActivationDD) return 1.0;
        double span = Math.Max(1e-9, FullDD - ActivationDD);
        double t    = Math.Min(1.0, (drawdownFrac - ActivationDD) / span);
        return 1.0 - (1.0 - SizeFloor) * t;
    }

    public static bool IsGuarded(string strategy) =>
        strategy is "grid" or "diplong" or "swing_long";

    public override string ToString() =>
        $"ActivDD={ActivationDD:P1}  FullDD={FullDD:P1}  Floor={SizeFloor:F2}  Brake={DrawdownBrakeAt:P1}  F={Fitness:F4}";
}

public record DrawdownGuardGenotypeDto(
    [property: JsonPropertyName("ActivationDD")]    double ActivationDD,
    [property: JsonPropertyName("FullDD")]          double FullDD,
    [property: JsonPropertyName("SizeFloor")]       double SizeFloor,
    [property: JsonPropertyName("DrawdownBrakeAt")] double DrawdownBrakeAt,
    [property: JsonPropertyName("Fitness")]         double Fitness)
{
    public DrawdownGuardGenotype ToGenotype() =>
        new(ActivationDD, FullDD, SizeFloor, DrawdownBrakeAt, Fitness);
}
