using System.Text.Json;

namespace TradingGA;

// Fitness weights for FoldScoreHelper.Canonical. All default to 1.0 (neutral/no-op).
// NOTE: the six canonical weights (GainW..RetentionW) were previously inert (declared but never read).
// Old stored configs with non-1.0 values will now take effect and change fitness numbers.
public record FitnessConfig(
    string VariantId       = "default",

    // Canonical fold-score term weights. 1.0 = no-op. Negative values floored at 0 inside Canonical.

    // Scales the raw gain term: gain * 100.0 * GainW.  1.0 = no-op.
    double GainW           = 1.0,
    // Scales the ABOVE-0.40 win-rate reward slope: 1.0 + (wr - 0.40) * 3.0 * WrW.
    // The sub-0.40 penalty ramp (wr / 0.40) is deliberately unweighted.  1.0 = no-op.
    double WrW             = 1.0,
    // Scales the quality multiplier's deviation from 1.0:
    // Clamp(Sqrt(pfMult*rrMult) * QualityW + (1 - QualityW), 0.0, 2.5).
    // Weighting happens BEFORE the 2.5 cap, so the cap still binds.  1.0 = no-op.
    double QualityW        = 1.0,
    // Scales the log trade-count bonus:
    //   1.0 + 0.15 * FreqW * freqQuality * Log(max(1, n/minTrades)).
    // 1.0 = no-op.
    double FreqW           = 1.0,
    // Quality gate on frequency bonus: extra trades earn credit only when PF and avg return are good.
    // FreqPfFull: PF at which credit is full (zero at PF 1.0).
    double FreqPfFull      = 2.0,
    // FreqAvgFullPct: avg return/trade (%) at which credit is full. Zero at or below 0%.
    double FreqAvgFullPct  = 1.0,
    // Sample-size shrinkage: quality multipliers pulled toward 1.0 by w = n/(n+k). k=50 default.
    // Quality terms are blind to n; this prevents reward for selectivity by accident. Set to 0 to disable.
    double QualityShrinkK  = 50.0,
    // Scales drawdown sensitivity in the divisor: ddDiv = 1.0 + maxDd * 10.0 * DdPenalty.
    // Higher = harsher on drawdown. Floored at 0 so ddDiv >= 1.0.  1.0 = no-op.
    double DdPenalty       = 1.0,
    // Scales the end-of-fold retention multiplier's deviation from 1.0:
    // max(0.0, retentionRaw * RetentionW + (1 - RetentionW)), where retentionRaw keeps
    // its own 0.2 floor. Higher = harsher on giving gains back.  1.0 = no-op.
    double RetentionW      = 1.0,

    // ── Statistical bonus weights (always were live; 0.0 = term off) ──
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
