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
    // Scales the log trade-count bonus:
    //   1.0 + 0.15 * FreqW * freqQuality * Log(max(1, n/minTrades)).
    // 1.0 = no-op.
    double FreqW           = 1.0,
    // ── Quality gate on the frequency bonus ─────────────────────────────────────────────
    // Extra trades only earn volume credit when they are also GOOD trades. freqQuality is the
    // product of two clamped ramps — profit factor and average return per trade — so a genotype
    // cannot buy frequency credit by loosening an entry filter while per-trade edge stays flat.
    //
    // Measured motivation: RipShort's regime-adapt gene bought 27% more trades at an unchanged
    // train PF (7.10 vs 7.03) and collected ~25% more fitness, while held-out PF moved the wrong
    // way (0.95 vs 1.00). The GA was not finding an edge, it was finding this term.
    //
    // FreqPfFull: PF at which frequency credit is full. Zero credit at PF 1.0 (breakeven) — a
    // fold that merely breaks even gets no reward for doing it many times.
    double FreqPfFull      = 2.0,
    // FreqAvgFullPct: average return per trade (in PERCENT) at which credit is full. Zero at or
    // below 0%. Set to the scale of a genuinely worthwhile trade, not the scale of a typical one.
    double FreqAvgFullPct  = 1.0,
    // ── Sample-size shrinkage on the quality multipliers ────────────────────────────────
    // Half-credit trade count: each quality multiplier (qualityMult, and the Sharpe / Calmar /
    // PF / Sortino bonus factors) is pulled toward its neutral 1.0 by w = n / (n + k).
    //   k = 50 -> w = 0.375 at n=30, 0.67 at n=100, 0.91 at n=500, 0.98 at n=2000
    // Set to 0 to disable, which is a bit-for-bit no-op.
    //
    // This exists because the quality terms are blind to sample size: PF 7 on 30 trades scored
    // identically to PF 7 on 3000, so the GA was paid to be selective by accident. It does not
    // punish genuine selectivity — a real edge converges to full credit as trades accumulate.
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
