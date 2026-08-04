using System.Text.Json;

namespace TradingGA;

public record FitnessConfig(
    string VariantId       = "default",
    double GainW           = 1.0,
    double WrW             = 0.9,
    double QualityW        = 0.8,
    double FreqW           = 0.6,
    double DdPenalty       = 1.2,
    double RetentionW      = 1.0,
    double SharpeW         = 0.5,
    double CalmarW         = 0.0,
    double PfW             = 0.0,
    double SortinoW        = 0.3,
    double AtrLow          = 0.0,
    double AtrHigh         = 9999.0,
    double CVaRW           = 0.3,
    double TailRatioW      = 0.2,
    double RegimeDiversityW = 0.2,
    double EmbargoPct      = 0.05
)
{
    private static readonly JsonSerializerOptions _opts = new() { PropertyNameCaseInsensitive = true };

    public static FitnessConfig Load(string path = "fitness_config.json")
    {
        if (!File.Exists(path)) return new FitnessConfig();
        return JsonSerializer.Deserialize<FitnessConfig>(File.ReadAllText(path), _opts)
               ?? new FitnessConfig();
    }
}
