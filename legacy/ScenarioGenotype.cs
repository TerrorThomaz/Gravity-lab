using System.Text.Json.Serialization;

namespace TradingGA;

public record ScenarioGenotype(
    double CrashDepthPct,
    double CrashDurationHours,
    double RecoveryHours,
    double AtrExpansionPeak,
    double AltBetaPct,
    double LiquiditySqueezeHours,
    double InjectionOffsetFrac,
    double Fitness = 0)
{
    public static readonly double[,] Bounds = {
        { 0.10, 0.60  },
        { 12,   336   },
        { 0,    336   },
        { 1.5,  10.0  },
        { 0.5,  2.0   },
        { 6,    48    },
        { 0.1,  0.9   },
    };

    public double[] ToGenes() =>
        [CrashDepthPct, CrashDurationHours, RecoveryHours, AtrExpansionPeak,
         AltBetaPct, LiquiditySqueezeHours, InjectionOffsetFrac];

    public static ScenarioGenotype FromGenes(double[] g, double fitness = 0) =>
        new(g[0], g[1], g[2], g[3], g[4], g[5], g[6], fitness);
}

public record ScenarioGenotypeDto(
    [property: JsonPropertyName("CrashDepthPct")]         double CrashDepthPct,
    [property: JsonPropertyName("CrashDurationHours")]    double CrashDurationHours,
    [property: JsonPropertyName("RecoveryHours")]         double RecoveryHours,
    [property: JsonPropertyName("AtrExpansionPeak")]      double AtrExpansionPeak,
    [property: JsonPropertyName("AltBetaPct")]            double AltBetaPct,
    [property: JsonPropertyName("LiquiditySqueezeHours")] double LiquiditySqueezeHours,
    [property: JsonPropertyName("InjectionOffsetFrac")]   double InjectionOffsetFrac,
    [property: JsonPropertyName("Fitness")]               double Fitness)
{
    public ScenarioGenotype ToGenotype() =>
        new(CrashDepthPct, CrashDurationHours, RecoveryHours, AtrExpansionPeak,
            AltBetaPct, LiquiditySqueezeHours, InjectionOffsetFrac, Fitness);
}
