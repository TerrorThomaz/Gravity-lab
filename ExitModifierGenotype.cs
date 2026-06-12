using System.Text.Json.Serialization;

namespace TradingGA;

// Position-size scaling genotype based on portfolio context at trade entry.
//
// Context factors (computed by TradeEnricher):
//   atrRank          — 0..1 percentile of current ATR in 252-bar rolling distribution
//   openPositions    — concurrent open trades at entry
//   liquidityScore   — volume[entry] / 20-bar avg volume
//   recentTradeCount — trades opened in past 168h
//
// Size multiplier (clamped to [MinSizeMult, 1.0]):
//   atrFactor  = (1 - atrRank)^AtrRankPower
//   heatFactor = HeatMultPerPosition ^ max(0, openPositions - HeatThreshold)
//   liqFactor  = lerp(LiquidityMinMult, 1, liqScore/LiqFloor) when liqScore < LiqFloor, else 1
//   freqFactor = lerp(FreqMultAtMax, 1, FreqMax/recent)       when recent > FreqMax,    else 1
//   mult       = clamp(atrFactor × heatFactor × liqFactor × freqFactor, MinSizeMult, 1)
public record ExitModifierGenotype(
    double AtrRankPower,
    double HeatThreshold,
    double HeatMultPerPosition,
    double LiquidityFloor,
    double LiquidityMinMult,
    double FreqMaxPerWindow,
    double FreqMultAtMax,
    double MinSizeMult,
    double Fitness = 0)
{
    public static readonly double[,] Bounds = {
        { 0.0, 3.0 },
        { 1.0, 15.0 },
        { 0.5, 1.0 },
        { 0.1, 2.0 },
        { 0.0, 0.5 },
        { 1.0, 40.0 },
        { 0.1, 1.0 },
        { 0.0, 0.3 },
    };

    public double[] ToGenes() =>
        [AtrRankPower, HeatThreshold, HeatMultPerPosition, LiquidityFloor,
         LiquidityMinMult, FreqMaxPerWindow, FreqMultAtMax, MinSizeMult];

    public static ExitModifierGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6], g[7], fitness);

    public double ComputeMult(double atrRank, int openPositions, double liquidityScore, int recentTradeCount)
    {
        double atr  = Math.Pow(1.0 - atrRank, Math.Max(0.0, AtrRankPower));
        double heat = Math.Pow(HeatMultPerPosition,
                               Math.Max(0.0, openPositions - (int)HeatThreshold));
        double liq  = liquidityScore >= LiquidityFloor ? 1.0
                    : LiquidityMinMult + (1.0 - LiquidityMinMult)
                      * (liquidityScore / LiquidityFloor);
        double freq = recentTradeCount <= (int)FreqMaxPerWindow ? 1.0
                    : FreqMultAtMax + (1.0 - FreqMultAtMax)
                      * ((int)FreqMaxPerWindow / (double)Math.Max(1, recentTradeCount));
        return Math.Clamp(atr * heat * liq * freq, MinSizeMult, 1.0);
    }

    public override string ToString() =>
        $"AtrPow={AtrRankPower:F2} HeatThr={HeatThreshold:F0}@{HeatMultPerPosition:F2} "
      + $"Liq={LiquidityFloor:F2}/{LiquidityMinMult:F2} "
      + $"Freq={FreqMaxPerWindow:F0}@{FreqMultAtMax:F2} MinMult={MinSizeMult:F2} F={Fitness:F4}";
}

public record ExitModifierGenotypeDto(
    [property: JsonPropertyName("AtrRankPower")]        double AtrRankPower,
    [property: JsonPropertyName("HeatThreshold")]       double HeatThreshold,
    [property: JsonPropertyName("HeatMultPerPosition")] double HeatMultPerPosition,
    [property: JsonPropertyName("LiquidityFloor")]      double LiquidityFloor,
    [property: JsonPropertyName("LiquidityMinMult")]    double LiquidityMinMult,
    [property: JsonPropertyName("FreqMaxPerWindow")]    double FreqMaxPerWindow,
    [property: JsonPropertyName("FreqMultAtMax")]       double FreqMultAtMax,
    [property: JsonPropertyName("MinSizeMult")]         double MinSizeMult,
    [property: JsonPropertyName("Fitness")]             double Fitness)
{
    public ExitModifierGenotype ToGenotype() =>
        new(AtrRankPower, HeatThreshold, HeatMultPerPosition, LiquidityFloor,
            LiquidityMinMult, FreqMaxPerWindow, FreqMultAtMax, MinSizeMult, Fitness);
}
