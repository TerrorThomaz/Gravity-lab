namespace TradingGA;

public static class FoldScoreHelper
{
    public static double Canonical(
        List<double> returns,
        double posFrac,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0)
    {
        if (returns.Count < minTradesPerFold) return -1.0;

        int wins = 0;
        double grossWins = 0, grossLoss = 0;
        foreach (var r in returns)
        {
            if (r > 0) { wins++; grossWins += r; }
            else         grossLoss -= r;
        }

        double wr = (double)wins / returns.Count;
        double pf = grossLoss > 1e-10 ? grossWins / grossLoss : (grossWins > 0 ? 5.0 : 0.0);

        if (pf < 1.0) return pf - 2.0;

        double balance = 1.0, peak = 1.0, maxDd = 0.0;
        foreach (var r in returns)
        {
            balance += r / 100.0 * posFrac;
            if (balance > peak) peak = balance;
            double dd = (peak - balance) / peak;
            if (dd > maxDd) maxDd = dd;
        }

        double gain = balance - 1.0;
        if (gain <= 0) return gain * 100 - 0.5;

        // ── Tunable term weights (FitnessConfig) ──────────────────────────────
        // Each of the six weights below scales exactly ONE term of the canonical score
        // and is a bit-for-bit no-op at its default of 1.0 — see FitnessConfig for the
        // per-field contract. Weights are floored at 0 defensively: a hand-edited or
        // UI-generated fitness_config.json can carry any number, and a NEGATIVE weight
        // would flip the sign of its term (turning a reward into a punishment and
        // inverting the GA's gradient). Worse, a negative DdPenalty would drive ddDiv
        // to exactly 0 or below — dividing by ~0 or flipping the sign of the entire
        // fold score. None of the six has a meaningful negative interpretation, so 0
        // ("term switched off") is the floor. NaN, if someone writes one, propagates.
        double gainW      = Math.Max(0.0, cfg.GainW);
        double wrW        = Math.Max(0.0, cfg.WrW);
        double qualityW   = Math.Max(0.0, cfg.QualityW);
        double freqW      = Math.Max(0.0, cfg.FreqW);
        double ddW        = Math.Max(0.0, cfg.DdPenalty);
        double retentionW = Math.Max(0.0, cfg.RetentionW);

        // WrW scales the ABOVE-knee reward slope only. The sub-0.40 branch is a
        // disqualifying penalty ramp, not a reward, so it stays unweighted — otherwise
        // WrW = 0 would erase the penalty for a terrible win rate instead of just
        // switching the bonus off.
        double wrMult = wr < 0.40 ? wr / 0.40 : 1.0 + (wr - 0.40) * 3.0 * wrW;

        double pfMult = pf < 1.5 ? (pf - 1.0) / 0.5 : 1.0 + (pf - 1.5) * 0.5;
        int losses = returns.Count - wins;
        double avgWin  = wins   > 0 ? grossWins / wins   : 0;
        double avgLoss = losses > 0 ? grossLoss / losses : avgWin;
        double rr      = avgLoss > 1e-10 ? avgWin / avgLoss : (avgWin > 0 ? 5.0 : 1.0);
        double rrMult  = RrMultiplier(rr);
        // QualityW scales the multiplier's DEVIATION FROM 1.0, written in the lerp form
        // `raw*w + (1-w)` rather than `1 + (raw-1)*w` so that w == 1.0 collapses to
        // `raw*1.0 + 0.0` — exactly `raw`, with no rounding drift at any magnitude.
        //
        // Clamp placement, deliberately:
        //  · the 2.5 cap is applied AFTER weighting. The cap exists to stop one
        //    spectacular fold's quality reading from dominating fitness; letting a
        //    QualityW > 1 punch through it would defeat exactly that purpose.
        //  · the 0.0 floor stops a large QualityW from turning a sub-1.0 quality
        //    reading (pf just over 1.0, or rr under 1.0) into a NEGATIVE multiplier,
        //    which would flip the sign of the whole fold score.
        // At QualityW = 1.0 neither clamp changes anything: Sqrt is never negative, so
        // Clamp(raw, 0, 2.5) == Min(raw, 2.5), the original expression.
        //
        // The rr term is RrMultiplier — strictly positive and strictly increasing on
        // (0, inf) — precisely so the sqrt below can never see a negative argument and can
        // never be pinned at zero. See RrMultiplier's header for why the old linear ramp
        // `(rr - 1)/1.5` floored at 0 was a defect rather than a guard.
        //
        // pfMult is >= 0 over the whole reachable domain (pf >= 1.0 is checked above), so
        // the Math.Max is defensive only. The IsNaN screen keeps the "NaN is impossible"
        // contract total: a NaN return value in the input distribution would otherwise
        // survive Math.Max (Math.Max(0, NaN) == NaN) and make the GA's sort order
        // undefined, since NaN compares false against everything.
        double pfTerm      = double.IsNaN(pfMult) ? 0.0 : Math.Max(0.0, pfMult);
        double qualityRaw  = Math.Sqrt(pfTerm * rrMult);
        double qualityMult = Math.Clamp(qualityRaw * qualityW + (1.0 - qualityW), 0.0, 2.5);

        // DdPenalty scales drawdown sensitivity. With ddW >= 0, ddDiv >= 1.0 always,
        // so this division can never blow up or change the score's sign.
        double ddDiv = 1.0 + maxDd * 10.0 * ddW;
        // FreqW scales the log trade-count bonus. The Math.Max(1.0, ...) inside the log
        // keeps it strictly one-sided — a fold that barely clears minTradesPerFold gets
        // no bonus, never a penalty (thin folds are already handled by the gate above).
        //
        // ── FREQUENCY IS PAID FOR TWICE. READ THIS BEFORE RAISING FreqW. ─────────────────
        // `gain` ALREADY scales with trade count: 100 trades at +0.6% produce more fold gain
        // than 10 trades at +0.6%. So the product below pays for frequency once through gain
        // and again through this term.
        //
        // The second payment is not small. At FreqW = 1.0, trading 100x minTradesPerFold is
        // worth 1.691x — the same fitness as lifting win rate from 40% to 63%. Loosening an
        // entry filter is far cheaper than finding 23 points of win rate, so the GA takes the
        // frequency every time.
        //
        // Measured consequence on FadeShort, which ran at FreqW = 1.0: 2519 OOS trades at 47%
        // WR and +0.60%/trade — the largest weak-edge trade generator in the suite. Its GA had
        // simultaneously pinned MinRally, TakeProfit and Lookback at their UPPER bounds, i.e.
        // straining toward selectivity while this term paid it to do the opposite.
        //
        // Set FreqW = 0 for any strategy whose problem is too many marginal trades rather than
        // too few. It is exactly 1.0 (a no-op) at FreqW = 0, and `gain` still rewards genuine
        // volume — you are removing the DOUBLE payment, not the incentive to trade.
        double freqBonus = 1.0 + 0.15 * freqW * Math.Log(Math.Max(1.0, returns.Count / (double)minTradesPerFold));

        // RetentionW scales retention's deviation from 1.0, same lerp form as quality.
        // The 0.2 floor stays on the RAW ratio — it defines how much end-of-fold
        // give-back the score is willing to look at at all, independent of weighting.
        // The 0.0 floor afterwards stops a RetentionW > 1 from driving a floored
        // retention (0.2) negative: 0.2*6 + (1-6) = -3.8 would invert the fold score.
        double peakGain     = peak - 1.0;
        double retentionRaw = peakGain > 0.01
            ? Math.Max(0.2, (balance - 1.0) / peakGain)
            : 1.0;
        double retentionMult = Math.Max(0.0, retentionRaw * retentionW + (1.0 - retentionW));

        // PER-TRADE Sharpe/Sortino — deliberately NOT Simulator.SharpeRatio.
        //
        // Simulator.SharpeRatio(returns, candleCount) multiplies mean/std by
        // sqrt(candleCount / 288), and its second parameter is documented as a count of
        // 5-minute-equivalent CANDLES. This call site used to pass returns.Count — the
        // TRADE count — so the "Sharpe" term silently scaled by sqrt(nTrades):
        // 0.295x at 25 trades, 0.932x at 250, 1.86x at 1000. That is a second, hidden
        // frequency bonus stacked on top of the explicit `freqBonus` a few lines above,
        // and it made two folds with identical return DISTRIBUTIONS score differently
        // purely because one of them traded more often. The fitness must contain exactly
        // ONE frequency term, and freqBonus is it.
        //
        // Simulator.SharpeRatio itself is correct and unchanged — every other caller
        // passes a genuine candle count.
        double sharpe  = PerTradeSharpe(returns);
        double calmar  = Simulator.CalmarRatio(returns);
        double pfStat  = Simulator.ProfitFactor(returns);
        double sortino = PerTradeSortino(returns);
        // GainW scales the raw gain term. At 1.0 the `* gainW` factor is exact, so the
        // product below is bit-identical to the historical `gain * 100.0 * wrMult * ...`.
        double base_   = gain * 100.0 * gainW * wrMult * qualityMult * freqBonus / ddDiv * retentionMult;
        double score   = base_
            * (1.0 + cfg.SharpeW  * Math.Max(0, Math.Min(sharpe  / 3.0,  statBonusCeiling)))
            * (1.0 + cfg.CalmarW  * Math.Max(0, Math.Min(calmar  / 2.0,  statBonusCeiling)))
            * (1.0 + cfg.PfW      * Math.Max(0, Math.Min(pfStat - 1.0,   statBonusCeiling)))
            * (1.0 + cfg.SortinoW * Math.Max(0, Math.Min(sortino / 4.0,  statBonusCeiling)))
            * volWeight;

        score *= CVaRPenalty(returns, cfg);
        score *= TailRatioBonus(returns, cfg);

        return score;
    }

    // ── Risk/reward leg of the quality term ──────────────────────────────────────
    //
    // WHAT THIS REPLACED, AND WHY IT WAS NOT A CORNER CASE.
    //
    // The rr leg used to be the linear ramp
    //     rrMult = rr < 2.5 ? (rr - 1.0) / 1.5 : 1.0 + (rr - 2.5) * 0.3
    // fed into `Sqrt(Max(0, pfMult) * Max(0, rrMult))`. That ramp is NEGATIVE for every
    // fold with rr < 1.0, so the Max floored it to 0, qualityRaw to 0, qualityMult to 0 at
    // the default QualityW = 1.0 — and `base_` is a pure PRODUCT, so the whole fold score
    // collapsed to exactly 0.0. The stat bonuses and both tail terms are multiplicative and
    // cannot rescue a zero.
    //
    // Measured on 100-trade folds under the default FitnessConfig:
    //     80 wins @ +1.0 / 20 losses @ -2.0  ->  pf 2.00, wr 80%, rr 0.50  ->  score 0.000
    //     40 wins @ +3.0 / 60 losses @ -1.5  ->  pf 1.33, wr 40%, rr 2.00  ->  score 0.147
    // i.e. a pf = 2.00 / wr = 80% fold ranked BELOW a pf = 1.33 / wr = 40% one, and every
    // rr < 1 fold tied at exactly 0 — a flat plateau with no gradient pointing back toward
    // rr = 1, so selection inside the entire rr < 1 half-space was a coin flip and mutation
    // had to jump the boundary blind.
    //
    // rr < 1.0 is structurally reachable, not exotic: Canonical has already established
    // pf >= 1.0 by this point, and pf >= 1 only needs wins/losses > 1/rr. TakeProfitAtrMult
    // and StopLossAtrMult are GENES, so the GA searches straight into TP < SL. Only the Grid
    // family escaped, via GridShape's QualityW = 0; FadeShort, DipLong, SwingLong, FadeLong
    // and RipShort all run the default 1.0.
    //
    // THE REPLACEMENT is a saturating hyperbola, chosen over "fall back to the no-op 1.0
    // below rr = 1" because that fallback would trade the plateau for a CLIFF: the old ramp
    // passes through 0 at rr = 1, so a fold at rr = 0.999 would score its full value and one
    // at rr = 1.001 would still score ~0. Defect (3) in this same file is exactly that
    // pathology, and re-introducing it one axis over is not a fix.
    //
    //     RrMultiplier(rr) = MaxRrMult * rr / (rr + RrSaturation),
    //     MaxRrMult        = 1 + RrSaturation / RrNeutral
    //
    // Properties, all of which the old ramp lacked:
    //  · STRICTLY POSITIVE on (0, inf) — the sqrt argument is never negative, the product
    //    is never zeroed, so NaN and the collapse-to-0.0 are both structurally impossible.
    //  · STRICTLY INCREASING on (0, inf) — d/d(rr) = MaxRrMult*RrSaturation/(rr+S)^2 > 0.
    //    There is a real gradient at every rr, including the whole rr < 1 region.
    //  · BOUNDED above by MaxRrMult (1.6), so a freak payoff ratio cannot dominate the
    //    score. The old ramp grew without limit above rr = 2.5 and leaned entirely on the
    //    downstream Clamp(..., 0, 2.5) to contain it.
    //  · CALIBRATION PRESERVED: RrNeutral = 2.5 is the old ramp's own no-op point, so
    //    RrMultiplier(2.5) == 1.0 exactly and the term is still "neutral at rr = 2.5".
    //
    // Reference values: rr 0.35 -> 0.303, 0.50 -> 0.400, 1.00 -> 0.640, 2.00 -> 0.914,
    // 2.50 -> 1.000, 5.00 -> 1.231, ->inf -> 1.600.
    public const double RrNeutral    = 2.5;   // rr at which the term is exactly 1.0 (no-op)
    public const double RrSaturation = 1.5;   // half-saturation constant of the hyperbola
    public const double MaxRrMult    = 1.0 + RrSaturation / RrNeutral;   // 1.6, the supremum

    public static double RrMultiplier(double rr)
    {
        // `!(rr > 0)` rather than `rr <= 0` so NaN takes this branch too: a NaN payoff ratio
        // yields quality 0, never a NaN that would poison the GA's sort order.
        if (!(rr > 0.0)) return 0.0;
        if (double.IsPositiveInfinity(rr)) return MaxRrMult;
        return MaxRrMult * rr / (rr + RrSaturation);
    }

    // ── Un-normalised per-trade risk-adjusted return ─────────────────────────────
    // mean/stdev of the TRADE-RETURN distribution, with no time normalisation at all.
    // Scale-free in the trade count: two folds with the same return distribution get the
    // same number whether they contain 25 trades or 2500. That is the whole point — see
    // the call-site comment in Canonical.
    //
    // The two entry guards are carried over verbatim from Simulator.SharpeRatio so that
    // only the sqrt(candleCount/288) factor differs:
    //   · fewer than 5 returns -> 0 (nothing to estimate from)
    //   · profit factor < 1.3  -> 0 (the bonus is for genuinely good folds only)
    public static double PerTradeSharpe(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double grossProfit = returns.Where(r => r > 0).Sum();
        double grossLoss   = Math.Abs(returns.Where(r => r <= 0).Sum());
        if (grossLoss < 1e-10 || grossProfit / grossLoss < 1.3) return 0;
        double mean = returns.Average();
        double std  = Math.Sqrt(returns.Select(r => (r - mean) * (r - mean)).Average());
        return std < 1e-10 ? 0 : mean / std;
    }

    // Downside equivalent of PerTradeSharpe: mean / downside-deviation, no time scaling.
    // Guards and the 999.99 saturation match Simulator.SortinoRatio.
    public static double PerTradeSortino(List<double> returns)
    {
        if (returns.Count < 5) return 0;
        double mean       = returns.Average();
        var    negReturns = returns.Where(r => r < 0).ToList();
        if (negReturns.Count == 0) return mean > 0 ? 99.99 : 0;
        double downStd = Math.Sqrt(negReturns.Select(r => r * r).Average());
        return downStd < 1e-10 ? 0 : Math.Min(mean / downStd, 999.99);
    }

    // ── Tail-estimator sample gate ───────────────────────────────────────────────
    // Both tail terms below read an ORDER STATISTIC out of the left/right 5% of the
    // sample. With fewer than 100 returns that bucket holds fewer than 5 observations,
    // and at MinTradesPerFold (20–25) it holds exactly ONE: "expected shortfall at 5%"
    // and "5th/95th percentile ratio" then degenerate into "the single worst trade" and
    // "best single trade / worst single trade". Those are extreme order statistics of a
    // fat-tailed distribution, not risk measures — they are dominated by whichever lucky
    // or unlucky fill happened to land in the fold.
    //
    // Rather than fabricate a number from that, both terms are damped toward the NEUTRAL
    // 1.0 as the sample thins — fully live at "the 5% tail bucket holds at least 5
    // observations", i.e. n * 0.05 >= 5  <=>  n >= 100, and fully off once the bucket is a
    // single trade. See TailRampLo / TailRampHi immediately below for the ramp itself.
    public const int MinTailSampleSize = 100;

    // ── …applied as a RAMP, not as a step ────────────────────────────────────────
    //
    // MinTailSampleSize used to be a HARD gate inside two MULTIPLICATIVE terms whose
    // ranges are [0.5, 1.0] (CVaRPenalty) and [1.0, 1.5] (TailRatioBonus). Nothing smoothed
    // it, so the objective had a cliff at exactly 100 trades. Measured on a 60/35/5 fold
    // (60 wins @ +4.0, 35 losses @ -1.0, 5 losses @ -8.0):
    //     n = 100  ->  fold score 4.970   (CVaRPenalty 0.70, the fat tail is charged for)
    //     n =  99  ->  fold score 6.750   (CVaRPenalty 1.00, the tail is free)
    // Deleting one WINNING trade — strictly less profit — raised the score by 35.8%. Every
    // MinTradesPerFold in the repo is 10-30, so the 10..99 band is inside the normal
    // operating range of every strategy, and the bear-window-filtered strategies live there
    // by construction. That is a live, reachable incentive to trade LESS.
    //
    // Both terms now blend toward their neutral 1.0 with
    //     w(n) = clamp((n - TailRampLo) / (TailRampHi - TailRampLo), 0, 1)
    //     term = 1 + w(n) * (raw - 1)
    //
    // The two ramp bounds are the two points where the estimator's own sample changes
    // character, and BOTH tail statistics cross them at the same n because both read a
    // 1-in-20 bucket — StatisticalTests.CVaR takes max(1, (int)(n*0.05)) returns and
    // TailRatioBonus takes max(1, n/20):
    //
    //   TailRampLo = 40  — the bucket holds 2 observations here (n*0.05 >= 2 <=> n >= 40).
    //                      BELOW it the bucket holds exactly ONE trade, so "expected
    //                      shortfall at 5%" and "p95/p5" degenerate into "the single worst
    //                      trade" and "best single trade / worst single trade". Those are
    //                      extreme order statistics of a fat-tailed distribution and carry
    //                      no information about the strategy, so the terms are switched
    //                      fully OFF (w = 0) — exactly the historical behaviour there.
    //   TailRampHi = 100 — MinTailSampleSize, the bucket holds 5 observations. This is the
    //                      point the gate was already calibrated at, so at and above 100
    //                      trades both terms are bit-for-bit what they were (w = 1).
    //
    // Continuity: w is 0 at and below TailRampLo and 1 at and above TailRampHi, so it meets
    // the flat regions on both sides with no step. n is an integer, so the finest possible
    // move is one trade; that now shifts a term by at most (raw - 1)/60 instead of by the
    // full deviation — the 100-vs-99 swing above drops from ~30% of the fold score to ~0.5%.
    public const int TailRampLo = 40;
    public const int TailRampHi = MinTailSampleSize;

    // Blend weight for the two tail terms at a sample of n returns. 0 = term fully neutral,
    // 1 = term fully live. Monotone non-decreasing in n, continuous at both bounds.
    public static double TailTermWeight(int n)
        => Math.Clamp((n - (double)TailRampLo) / (TailRampHi - TailRampLo), 0.0, 1.0);

    // CVaR (expected shortfall) left-tail penalty.
    //
    // UNITS — read before touching this. StatisticalTests.CVaR returns the SIGNED mean
    // of the worst `alpha` fraction of trade returns, expressed in PERCENT:
    //   cvar5 = -5.0  ->  the worst 5% of trades average a 5% LOSS  (fat, dangerous tail)
    //   cvar5 = -0.5  ->  the worst 5% barely lose anything          (tight, safe tail)
    // Negative = loss, and MORE negative = WORSE. The penalty must therefore grow as
    // cvar5 falls, never the other way round (an earlier version had the comparison
    // inverted and rewarded fat tails while punishing safe ones).
    //
    //   cvar5 >= -3%  ->  1.0                                  (tail inside tolerance)
    //   cvar5 <  -3%  ->  decreasing with the excess loss, saturating at cvar5 = -6%
    // The multiplier is floored at 0.5 so it can never reach 0 or go negative — that
    // would zero out or flip the sign of the whole fitness score.
    public static double CVaRPenalty(List<double> returns, FitnessConfig cfg)
    {
        // Gate raised 20 -> 100 and then RAMPED (see TailRampLo/TailRampHi):
        // StatisticalTests.CVaR takes the worst max(1, n*0.05) returns, so at n = 20..25 the
        // "expected shortfall" was a single trade. The weight is 0 at and below 40 trades,
        // so the estimator is never read while its bucket holds a single observation.
        double w = TailTermWeight(returns.Count);
        if (cfg.CVaRW <= 0 || w <= 0.0) return 1.0;
        double cvar5 = StatisticalTests.CVaR(returns, 0.05);

        const double tolerancePct = -3.0; // no penalty for tails shallower than this
        const double scalePct     =  3.0; // excess loss (pct) at which the penalty saturates

        if (cvar5 >= tolerancePct) return 1.0;
        double excess = Math.Min((tolerancePct - cvar5) / scalePct, 1.0); // 0..1
        double raw    = Math.Clamp(1.0 - excess * cfg.CVaRW, 0.5, 1.0);
        // Blend toward the neutral 1.0. raw is already inside [0.5, 1.0] and w inside
        // [0, 1], so the blend cannot leave that range: the multiplier still can never
        // reach 0 or flip the sign of the fold score.
        return 1.0 + w * (raw - 1.0);
    }

    // Right-tail / left-tail asymmetry bonus: |p95| / |p5| over the fold's trade returns.
    //
    // TWO changes from the original, both of which were live defects:
    //  (1) SAMPLE GATE. tailN = max(1, n/20) meant that at the achievable fold sizes
    //      (MinTradesPerFold is 10–25) tailN was exactly 1, so this "percentile ratio"
    //      was literally |best single trade| / |worst single trade|. Combined with (2)
    //      that made the objective PAY for having one outsized winner — the precise
    //      opposite of what a tail term is for. Now gated at MinTailSampleSize, where the
    //      tail bucket holds >= 5 observations.
    //  (2) UNBOUNDED ABOVE. The bonus grew linearly in the ratio with no ceiling, so a
    //      single 40% winner against a 0.5% worst loss bought an ~80x multiplier at
    //      TailRatioW = 1. The ratio is now clamped at MaxTailRatio and the resulting
    //      multiplier at MaxTailRatioBonus, so the term can shade a decision but can
    //      never dominate the score.
    // A ratio of 5 already means the good tail is five times the bad one; past that the
    // reading is telling us about one trade, not about the strategy.
    public const double MaxTailRatio      = 5.0;
    public const double MaxTailRatioBonus = 1.5;

    public static double TailRatioBonus(List<double> returns, FitnessConfig cfg)
    {
        double w = TailTermWeight(returns.Count);
        if (cfg.TailRatioW <= 0 || w <= 0.0) return 1.0;
        var sorted = returns.OrderBy(r => r).ToList();
        int n = sorted.Count;
        int tailN = Math.Max(1, n / 20);
        double p5  = sorted[tailN - 1];
        double p95 = sorted[n - tailN];
        double absP5  = Math.Abs(p5);
        double absP95 = Math.Abs(p95);
        if (absP5 < 1e-10) return 1.0;
        double ratio = Math.Min(absP95 / absP5, MaxTailRatio);
        if (ratio <= 1.5) return 1.0;
        double bonus = 1.0 + (ratio - 1.5) * 0.1 * cfg.TailRatioW;
        double raw   = Math.Clamp(bonus, 1.0, MaxTailRatioBonus);
        // Same ramp as CVaRPenalty, and the same containment argument: raw is inside
        // [1.0, MaxTailRatioBonus] and w inside [0, 1], so the blend stays inside it too.
        return 1.0 + w * (raw - 1.0);
    }

    public static double RegimeDiversityBonus(
        Dictionary<MarketRegime, List<double>> regimeReturns,
        FitnessConfig cfg)
    {
        if (cfg.RegimeDiversityW <= 0) return 1.0;
        int activeRegimes = regimeReturns.Count(r => r.Value.Count >= 10);
        if (activeRegimes <= 1) return 1.0;
        int profitableRegimes = regimeReturns
            .Where(r => r.Value.Count >= 10 && r.Value.Average() > 0)
            .Count();
        double diversity = (double)profitableRegimes / Math.Max(1, activeRegimes);
        return 1.0 + diversity * cfg.RegimeDiversityW;
    }

    // ── Fold boundaries ───────────────────────────────────────────────────
    // NOTE ON INDEX SPACES: the two Compute*FoldBoundaries methods below return bounds
    // in the INDEX SPACE OF THE ARRAY THEY WERE COMPUTED FROM. They are only safe to
    // apply to that same array. They must NOT be shared across coins — every coin has a
    // different array length, a different first candle, and (for the bear strategies) a
    // regime-filtered concatenation of candles, so index i is a different calendar date
    // on every coin. Cross-coin walk-forward folds go through the TIME-based
    // ComputeRegimeAwareFoldWindows + RangeForWindow pair instead.
    public static (int Start, int End)[] ComputeFoldBoundaries(int totalBars, int folds, double embargoPct = 0.05)
    {
        int foldSize = totalBars / folds;
        int embargoBars = (int)(foldSize * embargoPct);
        var bounds = new (int Start, int End)[folds];
        for (int f = 0; f < folds; f++)
        {
            int start = f * foldSize + (f > 0 ? embargoBars : 0);
            int end = f == folds - 1 ? totalBars : (f + 1) * foldSize;
            bounds[f] = (start, end);
        }
        return bounds;
    }

    public static (int Start, int End)[] ComputeRegimeAwareFoldBoundaries(
        RegimeBar[] btcSeries,
        Func<MarketRegime, bool> isActive,
        int folds,
        double embargoPct = 0.05)
    {
        if (btcSeries.Length < 220 || folds < 2)
            return ComputeFoldBoundaries(btcSeries.Length, folds, embargoPct);

        int totalActive = 0;
        for (int i = 0; i < btcSeries.Length; i++)
            if (isActive(btcSeries[i].Regime)) totalActive++;

        if (totalActive < folds * 40)
            return ComputeFoldBoundaries(btcSeries.Length, folds, embargoPct);

        int targetPerFold = totalActive / folds;
        int embargoBars = Math.Max(1, (int)(targetPerFold * embargoPct));

        var result = new List<(int Start, int End)>();
        int foldStart = 0;
        int activeInFold = 0;

        for (int i = 0; i < btcSeries.Length; i++)
        {
            if (isActive(btcSeries[i].Regime)) activeInFold++;

            if (activeInFold >= targetPerFold && result.Count < folds - 1)
            {
                result.Add((foldStart, i + 1));
                int embargoEnd = Math.Min(i + 1 + embargoBars, btcSeries.Length);
                foldStart = embargoEnd;
                activeInFold = 0;
                i = embargoEnd - 1;
            }
        }

        if (foldStart < btcSeries.Length)
            result.Add((foldStart, btcSeries.Length));

        while (result.Count < folds)
            result.Add((btcSeries.Length, btcSeries.Length));

        return result.Take(folds).ToArray();
    }

    // Regime-aware walk-forward folds expressed as [Start, End) TIME windows.
    //
    // Same active-bar balancing and embargo logic as ComputeRegimeAwareFoldBoundaries —
    // the indices are simply translated into the BTC series' own timestamps so that each
    // coin can map the window onto its OWN candle array (see RangeForWindow). This is
    // what makes the folds comparable across coins: fold f covers the same calendar
    // stretch of market history for every symbol, regardless of how long that symbol's
    // array is or where it starts.
    //
    // Fold 0 opens at DateTime.MinValue and the final fold closes at DateTime.MaxValue so
    // that coin history extending slightly beyond the BTC series on either side is still
    // scored rather than silently dropped. Folds that came back empty in index space are
    // returned as an empty (MaxValue, MaxValue) window.
    public static (DateTime Start, DateTime End)[] ComputeRegimeAwareFoldWindows(
        RegimeBar[] btcSeries,
        Func<MarketRegime, bool> isActive,
        int folds,
        double embargoPct = 0.05)
    {
        var idx = ComputeRegimeAwareFoldBoundaries(btcSeries, isActive, folds, embargoPct);
        var windows = new (DateTime Start, DateTime End)[idx.Length];

        for (int f = 0; f < idx.Length; f++)
        {
            int s = idx[f].Start, e = idx[f].End;
            if (btcSeries.Length == 0 || s >= e || s >= btcSeries.Length)
            {
                windows[f] = (DateTime.MaxValue, DateTime.MaxValue);
                continue;
            }
            DateTime start = f == 0 ? DateTime.MinValue : btcSeries[s].Time;
            DateTime end   = e < btcSeries.Length ? btcSeries[e].Time : DateTime.MaxValue;
            windows[f] = (start, end);
        }
        return windows;
    }

    // Maps a [windowStart, windowEnd) time window onto a coin's own time-ordered candle
    // array, returning the half-open index range [Start, End) of the candles inside it.
    // Returns an empty range when the coin has no candles in the window.
    //
    // Candles are ascending in time (bear-window training arrays are concatenations of
    // ordered slices, so they are ascending with gaps — binary search is still valid).
    // Mirrors the binary-search pattern in RegimeBarLookup.TagRegimes.
    public static (int Start, int End) RangeForWindow(
        ReadOnlySpan<Candle> candles, DateTime windowStart, DateTime windowEnd)
    {
        if (candles.Length == 0 || windowEnd <= windowStart) return (0, 0);
        int s = LowerBound(candles, windowStart);
        int e = LowerBound(candles, windowEnd);
        if (e < s) e = s;
        return (s, e);
    }

    // First index i with candles[i].Time >= t, or candles.Length if there is none.
    private static int LowerBound(ReadOnlySpan<Candle> candles, DateTime t)
    {
        long ticks = t.Ticks;
        int lo = 0, hi = candles.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (candles[mid].Time.Ticks < ticks) lo = mid + 1;
            else                                 hi = mid;
        }
        return lo;
    }

    // Per-coin percentage folds — the fallback used when no BTC regime series is
    // available. Each coin slices its OWN array into `folds` equal parts, so index
    // alignment is trivially correct by construction. An embargo gap of
    // embargoPct x foldSize bars is dropped from the start of every fold after the first.
    public static (int Start, int End) PerCoinFoldRange(
        int totalBars, int folds, int fold, double embargoPct = 0.05)
    {
        if (folds < 1) folds = 1;
        if (totalBars <= 0) return (0, 0);

        int start = (int)((long)totalBars * fold / folds);
        int end   = fold >= folds - 1 ? totalBars : (int)((long)totalBars * (fold + 1) / folds);
        if (fold > 0) start += (int)(totalBars / folds * embargoPct);
        if (start > end) start = end;
        return (start, end);
    }

    // ── Fold aggregation ─────────────────────────────────────────────────────────
    // Combines the walk-forward fold scores into one fitness value.
    //
    //     fitness = λ·CVaR_α({s_f}) + (1 − λ)·mean({s_f})     over ALL attempted folds
    //
    //     CVaR_α  = mean of the WORST ceil(α·K) fold scores, α = CVaRFoldAlpha (0.4)
    //     λ       = LambdaThick (0.4) .. LambdaThin (0.8), rising as the sample thins
    //     K       = attemptedFolds — the vector is CONSTANT LENGTH; an attempted fold that
    //               did not reach MinTradesPerFold enters it at the ThinFoldScore floor
    //
    // WHY THIS REPLACED `mean − stdMult·std`:
    //
    // (a) THE OLD FORM WAS NON-MONOTONE. For f = mean − c·σ_pop over k folds,
    //         ∂f/∂s_i = (1/k)·(1 − c·z_i),   z_i = (s_i − mean)/σ_pop
    //     which is NEGATIVE whenever z_i > 1/c. The maximum attainable z is sqrt(k−1),
    //     so at k = 3 and the stdMult FLOOR of 0.75 the inversion region (z > 1.33) is
    //     already inside the reachable range (max 1.41), and at the stdMult CEILING of
    //     2.0 it starts at z > 0.5 — most ordinary fold configurations. Concretely, at
    //     c = 0.75, k = 3: (10,10,30) -> 9.596 but (10,10,40) -> 9.393. Improving the
    //     best fold by a third LOWERED fitness, so the GA was selecting against exactly
    //     the outcome it was supposed to reward.
    //
    //     The replacement is monotone BY CONSTRUCTION, with no clamp condition to get
    //     right: mean is non-decreasing in every s_f (∂/∂s_f = 1/k), CVaR_α is
    //     non-decreasing in every s_f (∂/∂s_f ∈ {0, 1/ceil(αk)} — raising a fold either
    //     leaves the worst-set alone or moves the set's mean up), and λ ∈ [0,1] does not
    //     depend on the fold SCORES, so any convex combination of the two is
    //     non-decreasing for any k and any λ. The dispersion penalty survives because
    //     CVaR overweights the worst folds: at equal mean, a spread-out fold vector has
    //     a lower worst-40% mean than an even one.
    //
    // (b) NEITHER THE OLD FORM NOR THE `coverage = k/attempted` FACTOR THAT REPLACED IT
    //     REMOVED THE CONCENTRATION INCENTIVE. The old form returned a single surviving
    //     fold verbatim, so a genotype trading ONLY in its most favourable market window
    //     scored 30.0 against 24.70 for one trading consistently at (20,25,30,35,40) —
    //     concentration was strictly dominant. Multiplying by survivingFolds/attemptedFolds
    //     did NOT fix that, because coverage is a BOUNDED LINEAR haircut (it can cost at
    //     most a factor k/K) while the gain from deleting a bad fold is UNBOUNDED (it
    //     raises both mean and CVaR without limit). At K = 5, four folds at g and one at b,
    //     λ = 0.4:
    //         keep = 0.32·b + 0.68·g        drop = 0.8·g        ⇒ dropping wins iff b < 0.375·g
    //     At g = 30 a genotype was better off DROPPING any fold scoring +11.25 or less —
    //     including clearly PROFITABLE folds. Worse, at λ = 0.8 (thin samples, i.e. exactly
    //     where more conservatism is wanted) the threshold widened to b < 0.545·g. The move
    //     was reachable: every call site `continue`s past a fold under MinTradesPerFold, so
    //     "stop trading in your worst window" was one threshold-gene tightening away.
    //
    //     THE FIX IS TO DELETE THE MOVE, NOT TO PRICE IT. The fold vector is now CONSTANT
    //     LENGTH: it always has attemptedFolds entries, and an attempted fold that came
    //     back under MinTradesPerFold enters the mean and the CVaR at the explicit
    //     ThinFoldScore floor instead of vanishing. Withdrawing from a fold therefore does
    //     not shorten the vector, it replaces that fold's score with a WORSE one — and the
    //     aggregate is monotone, so it strictly falls. There is no "drop a fold" move left
    //     in the search space, and with the move gone the coverage multiplier has nothing
    //     to price: it was removed rather than left in as a redundant knob.
    //
    //     The caller must still report how many folds were ATTEMPTED, not just how many
    //     survived — every fold the walk-forward loop `continue`d past for being under
    //     MinTradesPerFold is what the floor is substituted for. Callers are unchanged;
    //     the substitution happens here, from `attemptedFolds - foldScores.Count`.
    //
    //     THIS IS NOT THE ORIGINAL SENTINEL BUG. The pre-8e54c1f code ALSO fed a constant
    //     (Canonical's -1.0) for thin folds, and that is what made fitness non-monotone.
    //     The difference is entirely in the AGGREGATOR, not in the use of a constant:
    //     under `mean − c·std` a constant inflates std and therefore SUBTRACTS, inverting
    //     d(fitness)/d(fold score) — measured at −0.38 with 2 dead folds and −0.60 with 4.
    //     Under `λ·CVaR + (1−λ)·mean` both terms are non-decreasing in every entry and the
    //     constant is simply a low entry, i.e. a penalty; the gradient with respect to
    //     every real fold stays >= 0 no matter how many floors sit beside it. That is
    //     pinned by FoldAggregationTests.
    //
    // (c) stdMult's VC-PROPORTIONAL INTENT IS PRESERVED, expressed as λ instead. The old
    //     stdMult ran 0.75 (thick sample) to 2.0 (thin), leaning harder on dispersion
    //     when there was less data to trust. λ now runs 0.4 -> 0.8 over exactly the same
    //     avgN/d curve, so a thin sample leans harder on the WORST fold. λ is a function
    //     of the trade COUNTS only, never of the fold scores, so the monotonicity proof
    //     in (a) is untouched.
    //
    //   1 surviving fold at full coverage -> that fold's score verbatim (CVaR == mean == it)
    //   every attempted fold thin          -> ThinFoldScore (the all-floor vector)
    //   nothing attempted at all           -> DeadFoldFitness
    //
    // DeadFoldFitness now covers only the degenerate `attemptedFolds == 0 && k == 0` case —
    // a caller that ran no walk-forward loop at all, which carries no information about the
    // genotype. Every GA call site passes its own fold count, so this is unreachable from
    // training; it exists so the function is total.
    public const double DeadFoldFitness = -1000.0;

    // ── The thin-fold floor ──────────────────────────────────────────────────────
    //
    // Substituted for every ATTEMPTED fold that did not reach MinTradesPerFold, so the fold
    // vector is constant length and "withdraw from a fold" is not a move (see (b) above).
    //
    // WHY -5.0, i.e. why this is not the old -1.0 sentinel with a new name. Canonical's
    // output range over its whole reachable domain is [-2.0, +inf):
    //   · pf < 1.0                  -> `pf - 2.0`, and pf >= 0, so this lies in [-2.0, -1.0)
    //     — this branch, at pf = 0, is the WORST score Canonical can produce;
    //   · pf >= 1.0 and gain <= 0   -> `gain*100 - 0.5`. pf >= 1 means grossWins >= grossLoss,
    //     so sum(returns) >= 0 and gain >= 0; the branch is therefore only reachable at
    //     gain == 0 exactly, where it returns -0.5;
    //   · otherwise the score is a product of non-negative factors, hence >= 0.
    // (CanonicalRegimeStratified only multiplies scores ABOVE -1.0 by RegimeDiversityBonus,
    // which is >= 1.0, so -0.5 x (1 + RegimeDiversityW) is the deepest that path reaches.)
    //
    // The floor must therefore sit BELOW -2.0, or a thin fold would score better than a
    // genuinely losing one and withdrawing from a loser would pay — which is exactly what
    // the -1.0 sentinel did: it sat ABOVE the [-2.0, -1.0) losing band, making "produce no
    // trades here" strictly better than "trade and lose here". -5.0 clears the worst real
    // score by 3.0 points (2.5x), so the drop move is strictly loss-making for every fold
    // Canonical can emit, with margin.
    //
    // And it must not be so low that it DOMINATES. At K = 5 one floored fold costs
    //   λ·(floor/m) + (1−λ)·(floor/K) = 0.4·(−2.5) + 0.6·(−1.0) = −1.6
    // against a fitness scale where a good fold contributes ~+18, so coverage shades the
    // ranking without erasing the gradient on the surviving folds' quality. DeadFoldFitness
    // (-1000) in that slot would swamp every other term and flatten the landscape to "how
    // many folds are non-thin" — which is the failure mode the -1.0 sentinel's replacement
    // was reaching for and overshot in the other direction.
    //
    // Canonical's -2.0 lower bound is load-bearing for this choice, so it is pinned by a
    // property test (FoldAggregationTests) rather than left as a comment.
    public const double ThinFoldScore = -5.0;

    // Tail fraction taken over the FOLD SCORES (not over trade returns).
    public const double CVaRFoldAlpha = 0.4;
    // λ endpoints: thick sample -> weight the mean more; thin sample -> the worst fold.
    public const double LambdaThick = 0.4;
    public const double LambdaThin  = 0.8;

    public static double AggregateFoldScores(
        IReadOnlyList<double> foldScores,
        IReadOnlyList<int> foldTradeCounts,
        int d,
        int attemptedFolds)
    {
        int k = foldScores.Count;

        // A caller that under-reports attempts must not be able to shrink the vector below
        // the folds it actually scored.
        int attempted = Math.Max(attemptedFolds, k);
        if (attempted == 0) return DeadFoldFitness;

        // CONSTANT-LENGTH FOLD VECTOR: the scored folds, then one ThinFoldScore entry for
        // every attempted fold the caller skipped for being under MinTradesPerFold.
        var scores = new double[attempted];
        var counts = new int[attempted];
        for (int i = 0; i < k; i++)
        {
            scores[i] = foldScores[i];
            counts[i] = i < foldTradeCounts.Count ? foldTradeCounts[i] : 0;
        }
        for (int i = k; i < attempted; i++)
        {
            scores[i] = ThinFoldScore;
            counts[i] = 0;   // a fold with no trades genuinely thins the sample — see below
        }

        double sum = 0.0;
        for (int i = 0; i < attempted; i++) sum += scores[i];
        double mean = sum / attempted;
        double cvar = WorstFoldMean(scores, CVaRFoldAlpha);

        // λ reads the padded counts, so withdrawing from a fold also thins avgN and pushes λ
        // up. That only ever LOWERS fitness (CVaR <= mean always, so a larger λ moves the
        // blend toward the smaller number), which reinforces (b) on a second channel — and
        // it cannot disturb monotonicity, because λ still depends on trade COUNTS only,
        // never on the fold SCORES.
        double lambda = FoldLambda(counts, d);

        return lambda * cvar + (1.0 - lambda) * mean;
    }

    // Mean of the worst ceil(alpha * k) fold scores — CVaR / expected shortfall over the
    // fold distribution. Non-decreasing in every element by construction.
    internal static double WorstFoldMean(IReadOnlyList<double> foldScores, double alpha)
    {
        int k = foldScores.Count;
        if (k == 0) return 0.0;
        int m = Math.Clamp((int)Math.Ceiling(alpha * k), 1, k);
        // Array.Sort on a plain copy rather than OrderBy: this runs once per genotype per
        // generation across ten GAs, and the monotonicity sweeps call it millions of times.
        var sorted = new double[k];
        for (int i = 0; i < k; i++) sorted[i] = foldScores[i];
        Array.Sort(sorted);
        double sum = 0;
        for (int i = 0; i < m; i++) sum += sorted[i];
        return sum / m;
    }

    // VC-proportional blend weight, carried over from the old stdMult curve:
    //   thinness = clamp(7.5 / max(1, avgN/d), 0.75, 2.0)   (identical to old stdMult)
    // then linearly remapped [0.75, 2.0] -> [LambdaThick, LambdaThin]. Depends only on
    // the per-fold TRADE COUNTS, never on the fold scores.
    internal static double FoldLambda(IReadOnlyList<int> foldTradeCounts, int d)
    {
        double avgN     = foldTradeCounts.Count > 0 ? foldTradeCounts.Average() : 0.0;
        double thinness = Math.Clamp(7.5 / Math.Max(1.0, avgN / Math.Max(1, d)), 0.75, 2.0);
        return LambdaThick + (thinness - 0.75) / (2.0 - 0.75) * (LambdaThin - LambdaThick);
    }

    // ── Grid-family fitness shape ────────────────────────────────────────────────
    // GridGA, GridShortGA and AccumulationGridGA used to inline their own copy of the
    // fold-score formula, which meant the six FitnessConfig term weights were inert for
    // them, CVaRPenalty/TailRatioBonus never applied, and the SHAPE itself diverged from
    // every other strategy (win-rate slope 0.5 vs 3.0, drawdown divisor x20 vs x10, no
    // quality/retention/frequency terms at all). CoevolveGA and RegimeRouterGA then
    // combined the outputs of those incomparable scales.
    //
    // They now all call FoldScoreHelper.Canonical, and the divergences that are actually
    // DELIBERATE are expressed here as a config transform instead of a second formula:
    //
    //  · WrW / 6      — Grid scores per-SESSION returns, whose win rate is structurally
    //                   high (a grid session closes green whenever price oscillates), so
    //                   the canonical 3.0 slope above the 0.40 knee would swamp every
    //                   other term. 0.5/3.0 = 1/6 reproduces the historical slope.
    //  · DdPenalty x2 — documented in GridGA's header: drawdown is punished twice as hard
    //                   as elsewhere because a grid's tail risk is the stop-out on the
    //                   whole ladder, not a single fill.
    //  · FreqW = 0    — also documented: grid session count is driven by coin volatility,
    //                   not by strategy quality, so a frequency bonus would just rank
    //                   coins. Canonical's freqBonus is exactly 1.0 at FreqW = 0.
    //  · QualityW = 0 — NOT in the original as a documented choice. It was ORIGINALLY kept
    //                   off because Canonical's quality term used the linear ramp
    //                   rrMult = (rr - 1)/1.5, which is negative — and therefore floored to
    //                   a CONSTANT ZERO — over a grid's whole operating range: a grid is
    //                   many small mean-reversion fills against an occasional whole-ladder
    //                   stop-out, i.e. rr < 1.0 by construction. That reason is gone;
    //                   RrMultiplier is strictly positive and strictly increasing on
    //                   (0, inf), so the term now carries a real gradient below rr = 1 and
    //                   is no longer undefined over the grid's range.
    //                   It stays at 0 anyway, as a CALIBRATION choice rather than a
    //                   soundness one: RrNeutral = 2.5 makes the term a no-op at a payoff
    //                   ratio a grid never reaches, so switching it on would multiply every
    //                   grid genotype by roughly the same sub-1 factor — a scale shift, not
    //                   a signal — while making Grid fitness incomparable with the genotypes
    //                   trained before it. Turning it on is a deliberate retrain, not a
    //                   free improvement.
    //
    // NOT preserved (deliberately re-enabled, because nothing documented them as
    // intentional and their absence made Grid's scale incomparable): RetentionW and
    // GainW, which pass through from the caller's config at their neutral 1.0 — both are
    // well-defined over the grid's operating range. The stat-bonus ceiling stays at 1.0
    // for the whole Grid family — see GridShortGA's header on sparse-Ranging overfitting.
    // CVaRPenalty and TailRatioBonus, which the inlined copies skipped entirely, now
    // apply on any fold that clears MinTailSampleSize.
    //
    // Multiplicative rather than absolute so a hand-tuned fitness_config.json still has
    // an effect: this transform is a SHAPE delta, not an override.
    public static FitnessConfig GridShape(FitnessConfig cfg) => cfg with
    {
        WrW       = cfg.WrW / 6.0,
        FreqW     = 0.0,
        QualityW  = 0.0,
        DdPenalty = cfg.DdPenalty * 2.0,
    };

    // ── FadeShort fitness shape ──────────────────────────────────────────────────
    // Same pattern as GridShape: a documented SHAPE DELTA, not a second formula and not a
    // `cfg with { ... }` inlined at a call site. It lived in TrainCommands for one session and
    // that is exactly the scattered-constant problem the rest of this file exists to avoid — a
    // fitness weight set in a command file is invisible to anyone reading the objective.
    //
    //  · FreqW = 0 — FadeShort was the suite's weak-edge volume generator (2519 OOS trades at
    //                47% WR, +0.60%/trade). freqBonus paid for that volume a SECOND time on top
    //                of `gain`, which already accumulates linearly per trade.
    //
    //                MEASURED EFFECT: essentially none. Trade count moved 2014 -> 2000 (0.7%)
    //                and held-out PF 1.34 -> 1.40. The GA kept its entry genes almost exactly
    //                (MinRally 16.1A -> 16.2A, Lookback 237 -> 237) and rewrote the EXITS
    //                instead. Both runs were --no-seed, so this is independent rediscovery, not
    //                a basin artifact.
    //
    //                WHY IT BARELY MOVED: zeroing FreqW makes freqBonus exactly 1.0 — NEUTRAL,
    //                not punitive. `gain` is a running sum over trades and still pays linearly,
    //                which is the dominant term; freqBonus only ever paid logarithmically. So
    //                this removes the weaker of two frequency rewards and leaves the stronger.
    //                Kept because it is a small genuine improvement and the double payment is
    //                indefensible, but it is NOT a selectivity mechanism. See RegimeSustain.
    //
    //  · WrW x2    — accuracy is what the GA can buy once volume is no longer rewarded twice.
    //                Note it did not buy accuracy: it bought payoff asymmetry (SL 2.00A -> 0.32A,
    //                TP 18.85A -> 22.94A). Win rate was unchanged at 35%.
    public static FitnessConfig FadeShortShape(FitnessConfig cfg) => cfg with
    {
        FreqW = 0.0,
        WrW   = cfg.WrW * 2.0,
    };

    public static double CanonicalRegime(
        List<(double Return, int RegimeBars)> returns,
        double posFrac,
        int sustainedBars,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0)
    {
        var valid = returns.Where(r => r.RegimeBars >= sustainedBars).Select(r => r.Return).ToList();
        return Canonical(valid, posFrac, minTradesPerFold, cfg, volWeight, statBonusCeiling);
    }

    public static double CanonicalRegimeStratified(
        List<(double Return, int RegimeBars, MarketRegime Regime)> returns,
        double posFrac,
        int sustainedBars,
        int minTradesPerFold,
        FitnessConfig cfg,
        double volWeight = 1.0,
        double statBonusCeiling = 1.0)
    {
        var valid = returns.Where(r => r.RegimeBars >= sustainedBars).ToList();
        if (valid.Count < minTradesPerFold) return -1.0;

        var plainReturns = valid.Select(r => r.Return).ToList();
        double baseScore = Canonical(plainReturns, posFrac, minTradesPerFold, cfg, volWeight, statBonusCeiling);
        if (baseScore <= -1.0) return baseScore;

        var regimeBuckets = new Dictionary<MarketRegime, List<double>>();
        foreach (var r in valid)
        {
            if (!regimeBuckets.ContainsKey(r.Regime))
                regimeBuckets[r.Regime] = new List<double>();
            regimeBuckets[r.Regime].Add(r.Return);
        }

        baseScore *= RegimeDiversityBonus(regimeBuckets, cfg);
        return baseScore;
    }
}
