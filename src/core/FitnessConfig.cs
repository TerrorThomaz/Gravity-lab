using System.Text.Json;

namespace TradingGA;

// Fitness weights for FoldScoreHelper.Canonical.
//
// !! BREAKING-BEHAVIOUR NOTICE — the six canonical term weights below
//   (GainW, WrW, QualityW, FreqW, DdPenalty, RetentionW)
//   WERE COMPLETELY INERT until they were wired into FoldScoreHelper.Canonical.
//   They were declared here and read NOWHERE; Canonical hardcoded the coefficients
//   they were supposed to control. Any fitness_config.json in the wild — or any
//   variant preset / training-UI payload (frontend/Gravity Training.html sends all six)
//   — that sets these keys was silently doing NOTHING, and will now ACTUALLY TAKE
//   EFFECT and change fitness numbers.
//
//   Concretely: the old stored defaults were WrW=0.9, QualityW=0.8, FreqW=0.6,
//   DdPenalty=1.2. Those numbers never reached the formula. They are now 1.0
//   ("neutral"), chosen so that a default FitnessConfig reproduces the historical
//   hardcoded fitness BIT-FOR-BIT — existing genotypes stay comparable.
//
//   ACTION REQUIRED for anyone carrying a config that sets these six keys:
//   re-validate it against a fresh baseline, or delete the keys to fall back to
//   the neutral defaults. Copying the OLD defaults (0.9/0.8/0.6/1.2) forward is
//   NOT a no-op any more — it is a real, and different, fitness function.
//
//   The other weights (SharpeW, CalmarW, PfW, SortinoW, CVaRW, TailRatioW,
//   RegimeDiversityW, AtrLow/AtrHigh, EmbargoPct) were always live and are unaffected.
public record FitnessConfig(
    string VariantId       = "default",

    // ── Canonical fold-score term weights. 1.0 = neutral / no-op for every one. ──
    // Negative values are floored at 0.0 inside Canonical (0 = term switched off);
    // a negative weight would otherwise invert the term's sign.

    // Scales the raw gain term: gain * 100.0 * GainW.  1.0 = no-op.
    double GainW           = 1.0,
    // Scales the ABOVE-0.40 win-rate reward slope: 1.0 + (wr - 0.40) * 3.0 * WrW.
    // The sub-0.40 penalty ramp (wr / 0.40) is deliberately unweighted.  1.0 = no-op.
    double WrW             = 1.0,
    // Scales the quality multiplier's deviation from 1.0:
    // Clamp(Sqrt(pfMult*rrMult) * QualityW + (1 - QualityW), 0.0, 2.5).
    // Weighting happens BEFORE the 2.5 cap, so the cap still binds.  1.0 = no-op.
    double QualityW        = 1.0,
    // Scales the log trade-count bonus: 1.0 + 0.15 * FreqW * Log(max(1, n/minTrades)).
    // 1.0 = no-op.
    double FreqW           = 1.0,
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
