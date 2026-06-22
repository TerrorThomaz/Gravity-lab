using System.Text.Json.Serialization;

namespace TradingGA;

// Position-size scaling genotype based on portfolio context at trade entry.
//
// Context factors (computed by TradeEnricher):
//   liquidityScore   — volume[entry] / 20-bar avg volume
//   recentTradeCount — trades opened in past 168h
//
// Size multiplier (clamped to [MinSizeMult, 1.0]):
//   liqFactor  = lerp(LiquidityMinMult, 1, liqScore/LiqFloor) when liqScore < LiqFloor, else 1
//   freqFactor = lerp(FreqMultAtMax, 1, FreqMax/recent)       when recent > FreqMax,    else 1
//   mult       = clamp(liqFactor × freqFactor, MinSizeMult, 1)
public record ExitModifierGenotype(
    double LiquidityFloor,
    double LiquidityMinMult,
    double FreqMaxPerWindow,
    double FreqMultAtMax,
    double MinSizeMult,
    double Fitness = 0)
{
    public static readonly double[,] Bounds = {
        { 0.1, 2.0 },
        { 0.0, 0.5 },
        { 1.0, 40.0 },
        { 0.1, 1.0 },
        { 0.0, 0.3 },
    };

    public double[] ToGenes() =>
        [LiquidityFloor, LiquidityMinMult, FreqMaxPerWindow, FreqMultAtMax, MinSizeMult];

    public static ExitModifierGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], fitness);

    public double ComputeMult(double liquidityScore, int recentTradeCount)
    {
        double liq  = liquidityScore >= LiquidityFloor ? 1.0
                    : LiquidityMinMult + (1.0 - LiquidityMinMult)
                      * (liquidityScore / LiquidityFloor);
        double freq = recentTradeCount <= (int)FreqMaxPerWindow ? 1.0
                    : FreqMultAtMax + (1.0 - FreqMultAtMax)
                      * ((int)FreqMaxPerWindow / (double)Math.Max(1, recentTradeCount));
        return Math.Clamp(liq * freq, MinSizeMult, 1.0);
    }

    public double ComputeMult(TradeEnricher.EnrichedTrade t) =>
        ComputeMult(t.LiquidityScore, t.RecentTradeCount);

    public override string ToString() =>
        $"Liq={LiquidityFloor:F2}/{LiquidityMinMult:F2} "
      + $"Freq={FreqMaxPerWindow:F0}@{FreqMultAtMax:F2} MinMult={MinSizeMult:F2} F={Fitness:F4}";
}

public record ExitModifierGenotypeDto(
    [property: JsonPropertyName("LiquidityFloor")]   double LiquidityFloor,
    [property: JsonPropertyName("LiquidityMinMult")] double LiquidityMinMult,
    [property: JsonPropertyName("FreqMaxPerWindow")] double FreqMaxPerWindow,
    [property: JsonPropertyName("FreqMultAtMax")]    double FreqMultAtMax,
    [property: JsonPropertyName("MinSizeMult")]      double MinSizeMult,
    [property: JsonPropertyName("Fitness")]          double Fitness)
{
    public ExitModifierGenotype ToGenotype() =>
        new(LiquidityFloor, LiquidityMinMult, FreqMaxPerWindow, FreqMultAtMax, MinSizeMult, Fitness);
}
